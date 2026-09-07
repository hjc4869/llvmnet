#include <llvm-c/Core.h>
#include <llvm/Bitcode/BitcodeWriter.h>
#include <llvm/IR/Constants.h>
#include <llvm/IR/IRBuilder.h>
#include <llvm/IR/Instructions.h>
#include <llvm/IR/Module.h>
#include <llvm/IRReader/IRReader.h>
#include <llvm/Support/CBindingWrapping.h>
#include <llvm/Support/FileSystem.h>
#include <llvm/Support/SourceMgr.h>
#include <llvm/Support/raw_ostream.h>

extern "C" int llvmnet_stamp_abi(const char *path, const char *tag, int textual, char **message)
{
    llvm::LLVMContext context;
    llvm::SMDiagnostic diagnostic;
    auto module = llvm::parseIRFile(path, diagnostic, context);
    if (!module) {
        *message = LLVMCreateMessage("Cannot parse object while recording llvmnet ABI");
        return 1;
    }
    module->addModuleFlag(llvm::Module::Error, "llvmnet.abi", llvm::MDString::get(context, tag));
    std::error_code error;
    llvm::raw_fd_ostream destination(path, error, llvm::sys::fs::OF_None);
    if (error) {
        *message = LLVMCreateMessage(error.message().c_str());
        return 1;
    }
    if (textual) module->print(destination, nullptr);
    else llvm::WriteBitcodeToFile(*module, destination);
    destination.close();
    if (destination.has_error()) {
        *message = LLVMCreateMessage(destination.error().message().c_str());
        return 1;
    }
    return 0;
}

extern "C" LLVMModuleRef llvmnet_create_native_shim(LLVMModuleRef source)
{
    auto *input = llvm::unwrap(source);
    auto *module = new llvm::Module("llvmnet-native-abi", input->getContext());
    module->setDataLayout(input->getDataLayout());
    module->setTargetTriple(input->getTargetTriple());
    return llvm::wrap(module);
}

extern "C" void llvmnet_add_native_thunk(LLVMModuleRef shimRef, LLVMValueRef functionRef,
                                        LLVMValueRef callRef, const char *exportName)
{
    auto *module = llvm::unwrap(shimRef);
    auto *source = llvm::cast<llvm::Function>(llvm::unwrap(functionRef));
    auto *callSource = callRef ? llvm::cast<llvm::CallBase>(llvm::unwrap(callRef)) : nullptr;
    llvm::LLVMContext &context = module->getContext();
    llvm::Type *pointer = llvm::PointerType::getUnqual(context);
    auto *signature = llvm::FunctionType::get(llvm::Type::getVoidTy(context), {pointer, pointer}, false);
    auto *thunk = llvm::Function::Create(signature, llvm::GlobalValue::ExternalLinkage, exportName, module);
    auto *block = llvm::BasicBlock::Create(context, "entry", thunk);
    llvm::IRBuilder<> builder(block);
    std::vector<llvm::Type *> types;
    if (callSource) {
        for (auto &argument : callSource->args()) types.push_back(argument->getType());
    } else {
        for (auto *type : source->getFunctionType()->params()) types.push_back(type);
    }
    std::vector<llvm::Value *> arguments;
    uint64_t offset = 0;
    for (auto *type : types) {
        uint64_t alignment = module->getDataLayout().getABITypeAlign(type).value();
        offset = llvm::alignTo(offset, alignment);
        auto *address = builder.CreateGEP(builder.getInt8Ty(), thunk->getArg(1), builder.getInt64(offset));
        auto *argument = builder.CreateLoad(type, address);
        argument->setAlignment(llvm::Align(1));
        arguments.push_back(argument);
        offset += module->getDataLayout().getTypeAllocSize(type).getFixedValue();
    }
    llvm::FunctionCallee target = module->getOrInsertFunction(source->getName(), source->getFunctionType());
    auto *call = builder.CreateCall(target, arguments);
    call->setCallingConv(callSource ? callSource->getCallingConv() : source->getCallingConv());
    call->setAttributes(callSource ? callSource->getAttributes() : source->getAttributes());
    if (!source->getReturnType()->isVoidTy())
        builder.CreateStore(call, thunk->getArg(0))->setAlignment(llvm::Align(1));
    builder.CreateRetVoid();
}