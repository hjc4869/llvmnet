#include <llvm-c/Core.h>
#include <llvm/Bitcode/BitcodeReader.h>
#include <llvm/Bitcode/BitcodeWriter.h>
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
#include <memory>
#include <string>
#include <vector>

extern "C" int llvmnet_link(const char *const *paths, unsigned count,
                            const char *output, const char *abi, char **message)
{
    llvm::LLVMContext context;
    auto linked = std::make_unique<llvm::Module>("llvmnet", context);
    std::vector<std::unique_ptr<llvm::Module>> members;
    auto fail = [&](const std::string &text) {
        *message = LLVMCreateMessage(text.c_str());
        return 1;
    };
    auto compatible = [&](const llvm::Module &module) {
        auto *flag = llvm::dyn_cast_or_null<llvm::MDString>(module.getModuleFlag("llvmnet.abi"));
        return flag ? flag->getString() == abi : llvm::StringRef(abi) == "managed-host-v1";
    };
    for (unsigned index = 0; index < count; ++index) {
        llvm::StringRef path(paths[index]);
        if (path.ends_with(".a")) {
            auto buffer = llvm::MemoryBuffer::getFile(path);
            if (!buffer)
                return fail(path.str() + ": " + buffer.getError().message());
            auto archive = llvm::object::Archive::create((*buffer)->getMemBufferRef());
            if (!archive)
                return fail(llvm::toString(archive.takeError()));
            llvm::Error iteration = llvm::Error::success();
            for (const auto &child : (*archive)->children(iteration)) {
                auto data = child.getMemoryBufferRef();
                if (!data)
                    return fail(llvm::toString(data.takeError()));
                auto module = llvm::parseBitcodeFile(*data, context);
                if (!module)
                    return fail(path.str() + ": archive members must be LLVM bitcode: " + llvm::toString(module.takeError()));
                members.push_back(std::move(*module));
            }
            if (iteration)
                return fail(llvm::toString(std::move(iteration)));
        } else {
            llvm::SMDiagnostic diagnostic;
            auto module = llvm::parseIRFile(path, diagnostic, context);
            if (!module) {
                std::string text;
                llvm::raw_string_ostream stream(text);
                diagnostic.print("llvmnet", stream);
                return fail(text);
            }
            if (!compatible(*module))
                return fail("ABI mode mismatch or untagged object: " + path.str() + "; recompile all inputs with the same --runtime mode");
            if (llvm::Linker::linkModules(*linked, std::move(module)))
                return fail("LLVM object link failed: " + path.str());
        }
    }
    bool progress;
    do {
        progress = false;
        for (auto &member : members) {
            if (!member)
                continue;
            bool needed = false;
            for (const llvm::GlobalValue &definition : member->global_values()) {
                if (definition.isDeclarationForLinker() || definition.hasLocalLinkage())
                    continue;
                const llvm::GlobalValue *reference = linked->getNamedValue(definition.getName());
                if (reference && reference->isDeclarationForLinker() &&
                    !reference->hasExternalWeakLinkage() && !reference->use_empty()) {
                    needed = true;
                    break;
                }
            }
            if (!needed)
                continue;
            if (!compatible(*member))
                return fail("ABI mode mismatch or untagged archive member: " + member->getModuleIdentifier());
            if (llvm::Linker::linkModules(*linked, std::move(member)))
                return fail("LLVM archive member link failed");
            progress = true;
        }
    } while (progress);
    std::string verification;
    llvm::raw_string_ostream diagnostics(verification);
    if (llvm::verifyModule(*linked, &diagnostics))
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