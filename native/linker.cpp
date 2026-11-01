#include <llvm-c/Core.h>
#include <llvm/ADT/StringMap.h>
#include <llvm/BinaryFormat/Magic.h>
#include <llvm/Bitcode/BitcodeReader.h>
#include <llvm/Bitcode/BitcodeWriter.h>
#include <llvm/IR/AutoUpgrade.h>
#include <llvm/IR/DiagnosticInfo.h>
#include <llvm/IR/DiagnosticPrinter.h>
#include <llvm/IR/IRBuilder.h>
#include <llvm/IR/LLVMContext.h>
#include <llvm/IR/Module.h>
#include <llvm/IR/Verifier.h>
#include <llvm/IRReader/IRReader.h>
#include <llvm/Linker/Linker.h>
#include <llvm/Object/Archive.h>
#include <llvm/Support/FileSystem.h>
#include <llvm/Support/MemoryBuffer.h>
#include <llvm/Support/SourceMgr.h>
#include <llvm/Support/raw_ostream.h>
#include <algorithm>
#include <memory>
#include <string>
#include <vector>

// CIL has no x86_fp80 instructions: llvmnet computes long double arithmetic in software (Float80) and lowers the
// simple intrinsics itself, while the rounding and transcendental ones become calls to the C library's long double
// functions, as LLVM's own legalization does for targets without the operation. Those are musl's generic C in portable
// mode and glibc's in system mode. The mapping runs before archive extraction, so that the members defining the
// functions are linked.
static void lowerExtendedIntrinsics(llvm::Module &module)
{
    static const llvm::StringRef operations[] = {"sqrt", "ceil", "trunc", "rint", "nearbyint", "round", "roundeven", "sin", "cos",
                                                 "tan", "exp", "exp2", "exp10", "log", "log2", "log10", "pow", "ldexp", "frexp", "powi"};
    std::vector<llvm::Function *> intrinsics;
    for (llvm::Function &function : module)
        if (function.isIntrinsic())
            intrinsics.push_back(&function);
    for (llvm::Function *function : intrinsics) {
        llvm::StringRef name = function->getName();
        if (!name.starts_with("llvm."))
            continue;
        auto [operation, suffix] = name.drop_front(5).split('.');
        bool known = std::find(std::begin(operations), std::end(operations), operation) != std::end(operations);
        if (!known || suffix != (operation == "ldexp" || operation == "frexp" || operation == "powi" ? "f80.i32" : "f80"))
            continue;
        llvm::Type *extended = llvm::Type::getX86_FP80Ty(module.getContext());
        llvm::Type *integer = llvm::Type::getInt32Ty(module.getContext());
        if (operation == "frexp") {
            // frexpl returns the exponent through a pointer; the intrinsic returns { x86_fp80, i32 }.
            llvm::FunctionCallee frexpl = module.getOrInsertFunction(
                "frexpl", llvm::FunctionType::get(extended, {extended, llvm::PointerType::getUnqual(module.getContext())}, false));
            std::vector<llvm::CallInst *> calls;
            for (llvm::User *user : function->users())
                if (auto *call = llvm::dyn_cast<llvm::CallInst>(user); call && call->getCalledFunction() == function)
                    calls.push_back(call);
            for (llvm::CallInst *call : calls) {
                llvm::IRBuilder<> builder(call);
                llvm::BasicBlock &entry = call->getFunction()->getEntryBlock();
                llvm::IRBuilder<> prologue(&entry, entry.begin());
                llvm::AllocaInst *exponent = prologue.CreateAlloca(integer);
                llvm::Value *fraction = builder.CreateCall(frexpl, {call->getArgOperand(0), exponent});
                llvm::Value *aggregate = llvm::PoisonValue::get(call->getType());
                aggregate = builder.CreateInsertValue(aggregate, fraction, 0);
                aggregate = builder.CreateInsertValue(aggregate, builder.CreateLoad(integer, exponent), 1);
                call->replaceAllUsesWith(aggregate);
                call->eraseFromParent();
            }
            if (function->use_empty())
                function->eraseFromParent();
            continue;
        }
        // compiler-rt names the integer power __powixf2; the C library names the rest <operation>l.
        std::string libcall = operation == "powi" ? "__powixf2" : operation.str() + "l";
        llvm::FunctionCallee callee = module.getOrInsertFunction(libcall, function->getFunctionType());
        function->replaceAllUsesWith(callee.getCallee());
        function->eraseFromParent();
    }
}

// libomp's generic C __kmp_invoke_microtask, which the portable runtime uses, calls a parallel region's outlined
// function with up to 15 arguments. Flang passes every variable a region uses as its own argument, so a region may
// have more: for such a region the fork call gets a thunk that takes the argument array the runtime collected and
// calls the outlined function with each element, loaded as the parameter's own type (ports/openmp/README.md).
static void wrapLargeMicrotasks(llvm::Module &module)
{
    static const struct { const char *name; unsigned firstArgument; } forks[] = {
        {"__kmpc_fork_call", 3}, {"__kmpc_fork_call_if", 4}, {"__kmpc_fork_teams", 3}};
    llvm::Type *pointer = llvm::PointerType::getUnqual(module.getContext());
    for (const auto &fork : forks) {
        llvm::Function *entry = module.getFunction(fork.name);
        if (!entry)
            continue;
        std::vector<llvm::CallBase *> calls;
        for (llvm::User *user : entry->users())
            if (auto *call = llvm::dyn_cast<llvm::CallBase>(user); call && call->getCalledOperand() == entry)
                calls.push_back(call);
        for (llvm::CallBase *call : calls) {
            auto *count = llvm::dyn_cast<llvm::ConstantInt>(call->getArgOperand(1));
            auto *microtask = llvm::dyn_cast<llvm::Function>(call->getArgOperand(2)->stripPointerCasts());
            if (!count || !microtask || count->getZExtValue() <= 15 || microtask->getName().ends_with(".llvmnet_argv") ||
                microtask->getFunctionType()->getNumParams() != count->getZExtValue() + 2)
                continue;
            std::string thunkName = (microtask->getName() + ".llvmnet_argv").str();
            llvm::Function *thunk = module.getFunction(thunkName);
            if (!thunk) {
                thunk = llvm::Function::Create(llvm::FunctionType::get(llvm::Type::getVoidTy(module.getContext()), {pointer, pointer, pointer}, false),
                                               llvm::GlobalValue::InternalLinkage, thunkName, module);
                llvm::IRBuilder<> builder(llvm::BasicBlock::Create(module.getContext(), "", thunk));
                std::vector<llvm::Value *> arguments = {thunk->getArg(0), thunk->getArg(1)};
                for (unsigned index = 0; index < count->getZExtValue(); ++index)
                    arguments.push_back(builder.CreateLoad(microtask->getFunctionType()->getParamType(index + 2),
                                                           builder.CreateConstInBoundsGEP1_64(pointer, thunk->getArg(2), index)));
                builder.CreateCall(microtask, arguments);
                builder.CreateRetVoid();
            }
            call->setArgOperand(2, thunk);
        }
    }
}

// A duplicate definition is reported by LLVM's linker through the context's diagnostic handler as "Linking globals
// named 'x': symbol multiply defined!"; the driver's message names both files, so the file that first defined each
// symbol is recorded as modules are linked.
struct LinkDiagnostics {
    std::string last;
    std::vector<std::string> files;
    llvm::StringMap<size_t> definedIn;

    void record(const llvm::Module &module, const std::string &file)
    {
        files.push_back(file);
        for (const llvm::GlobalValue &value : module.global_values())
            if (!value.isDeclarationForLinker() && !value.hasLocalLinkage())
                definedIn.try_emplace(value.getName(), files.size() - 1);
    }

    std::string describe(const std::string &file)
    {
        std::string symbol;
        if (size_t open = last.find('\''); open != std::string::npos)
            if (size_t close = last.find('\'', open + 1); close != std::string::npos)
                symbol = last.substr(open + 1, close - open - 1);
        auto first = definedIn.find(symbol);
        if (!symbol.empty() && first != definedIn.end())
            return "duplicate definition of '" + symbol + "' in " + file + "; it is already defined in " + files[first->second];
        return "cannot link " + file + ": " + (last.empty() ? std::string("LLVM linker error") : last);
    }
};

// Links objects and archives into one module as a native linker resolves them: every object and every member of a
// whole archive, then the members of the other archives that define a symbol the module references but does not
// define, rescanning the archives until no member resolves anything more. An archive is recognized by its content,
// whatever its name. `entry` names the program's entry point (main, or the Fortran main program): while the module
// defines none of them, a member that defines one is linked, as crt1.o's reference to main extracts it natively (the
// gtest_main and Catch2 idiom). Libraries and relocatable links have no entry point.
//
// Bitcode is loaded lazily, so a function body is read only when its definition is linked: archive members that are
// never needed, and the copies of an inline function that every C++ object carries, cost little more than their
// symbol tables. One Linker serves the whole link, because a Linker indexes every struct type of the destination
// module when it is constructed: one per input made the link of a large program quadratic in its size.
extern "C" int llvmnet_link(const char *const *paths, const int *wholeArchive, unsigned count, const char *const *entry,
                            unsigned entryCount, const char *output, const char *abi, char **message)
{
    llvm::LLVMContext context;
    LinkDiagnostics diagnostics;
    context.setDiagnosticHandlerCallBack(
        [](const llvm::DiagnosticInfo *info, void *state) {
            std::string text;
            llvm::raw_string_ostream stream(text);
            llvm::DiagnosticPrinterRawOStream printer(stream);
            info->print(printer);
            static_cast<LinkDiagnostics *>(state)->last = text;
        },
        &diagnostics);
    // Lazily loaded members read their archive's buffer (or, for a thin archive, buffers the archive owns).
    std::vector<std::unique_ptr<llvm::MemoryBuffer>> archiveBuffers;
    std::vector<std::unique_ptr<llvm::object::Archive>> archives;
    auto linked = std::make_unique<llvm::Module>("llvmnet", context);
    llvm::Linker linker(*linked);
    std::vector<std::unique_ptr<llvm::Module>> members;
    std::vector<std::string> memberFiles;
    auto fail = [&](const std::string &text) {
        *message = LLVMCreateMessage(text.c_str());
        return 1;
    };
    // Every input must carry the ABI tag that llvmnet -c records; an untagged module could have been built for any target.
    auto incompatible = [&](const llvm::Module &module, const std::string &name) -> std::string {
        auto *flag = llvm::dyn_cast_or_null<llvm::MDString>(module.getModuleFlag("llvmnet.abi"));
        if (!flag)
            return "untagged LLVM input " + name + ": compile sources with llvmnet -c, or LLVM IR with llvmnet --runtime=<mode> -x ir -c, so that its ABI is recorded";
        if (flag->getString() != abi)
            return "ABI mismatch: " + name + " was compiled for " + flag->getString().str() + ", but this link is " + abi +
                "; recompile all inputs with the same --runtime mode";
        return "";
    };
    // Links one module; `name` is how an ABI problem names it. A lazily loaded module gets the debug info upgrade
    // that the bitcode reader applies to a module it reads in full.
    auto add = [&](std::unique_ptr<llvm::Module> module, const std::string &file, const std::string &name) -> std::string {
        if (std::string problem = incompatible(*module, name); !problem.empty())
            return problem;
        llvm::UpgradeDebugInfo(*module);
        diagnostics.record(*module, file);
        if (linker.linkInModule(std::move(module)))
            return diagnostics.describe(file);
        return "";
    };
    for (unsigned index = 0; index < count; ++index) {
        std::string path(paths[index]);
        auto buffer = llvm::MemoryBuffer::getFile(path);
        if (!buffer)
            return fail(path + ": " + buffer.getError().message());
        if (llvm::identify_magic((*buffer)->getBuffer()) != llvm::file_magic::archive) {
            // An object: LLVM bitcode, or textual LLVM IR, which is parsed in full.
            llvm::SMDiagnostic diagnostic;
            auto module = llvm::getLazyIRModule(std::move(*buffer), diagnostic, context);
            if (!module) {
                std::string text;
                llvm::raw_string_ostream stream(text);
                diagnostic.print("llvmnet", stream);
                return fail(text);
            }
            if (std::string problem = add(std::move(module), path, path); !problem.empty())
                return fail(problem);
            continue;
        }
        auto archive = llvm::object::Archive::create((*buffer)->getMemBufferRef());
        if (!archive)
            return fail(llvm::toString(archive.takeError()));
        llvm::Error iteration = llvm::Error::success();
        for (const auto &child : (*archive)->children(iteration)) {
            auto data = child.getMemoryBufferRef();
            if (!data)
                return fail(llvm::toString(data.takeError()));
            auto module = llvm::getLazyBitcodeModule(*data, context);
            if (!module)
                return fail(path + ": archive members must be LLVM bitcode: " + llvm::toString(module.takeError()));
            auto name = child.getName();
            std::string memberFile = path + "(" + (name ? name->str() : (*module)->getModuleIdentifier()) + ")";
            if (wholeArchive[index]) {
                // --whole-archive: every member is linked like an object, in archive order.
                if (std::string problem = add(std::move(*module), memberFile, memberFile); !problem.empty())
                    return fail(problem);
                continue;
            }
            members.push_back(std::move(*module));
            memberFiles.push_back(memberFile);
        }
        if (iteration)
            return fail(llvm::toString(std::move(iteration)));
        archiveBuffers.push_back(std::move(*buffer));
        archives.push_back(std::move(*archive));
    }
    auto isEntry = [&](llvm::StringRef name) {
        return std::any_of(entry, entry + entryCount, [&](const char *candidate) { return name == candidate; });
    };
    auto entryUndefined = [&] {
        return entryCount != 0 && std::none_of(entry, entry + entryCount, [&](const char *candidate) {
            const llvm::GlobalValue *value = linked->getNamedValue(candidate);
            return value && !value->isDeclarationForLinker();
        });
    };
    bool progress;
    do {
        progress = false;
        lowerExtendedIntrinsics(*linked);
        wrapLargeMicrotasks(*linked);
        bool needEntry = entryUndefined();
        for (size_t memberIndex = 0; memberIndex < members.size(); ++memberIndex) {
            auto &member = members[memberIndex];
            if (!member)
                continue;
            bool needed = false;
            for (const llvm::GlobalValue &definition : member->global_values()) {
                if (definition.isDeclarationForLinker() || definition.hasLocalLinkage())
                    continue;
                const llvm::GlobalValue *reference = linked->getNamedValue(definition.getName());
                if ((reference && reference->isDeclarationForLinker() && !reference->hasExternalWeakLinkage() &&
                     !reference->use_empty()) ||
                    (needEntry && isEntry(definition.getName()))) {
                    needed = true;
                    break;
                }
            }
            if (!needed)
                continue;
            if (std::string problem = add(std::move(member), memberFiles[memberIndex], "archive member " + memberFiles[memberIndex]);
                !problem.empty())
                return fail(problem);
            needEntry = entryUndefined();
            progress = true;
        }
    } while (progress);
    std::string verification;
    llvm::raw_string_ostream verifier(verification);
    if (llvm::verifyModule(*linked, &verifier))
        return fail(verification);
    std::error_code error;
    llvm::raw_fd_ostream destination(output, error, llvm::sys::fs::OF_None);
    if (error)
        return fail(error.message());
    llvm::WriteBitcodeToFile(*linked, destination);
    destination.close();
    if (destination.has_error())
        return fail(destination.error().message());
    return 0;
}