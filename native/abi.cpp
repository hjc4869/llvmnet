#include <llvm-c/Core.h>
#include <llvm/Bitcode/BitcodeWriter.h>
#include <llvm/IR/Constants.h>
#include <llvm/IR/IRBuilder.h>
#include <llvm/IR/Instructions.h>
#include <llvm/IR/InlineAsm.h>
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

extern "C" unsigned llvmnet_wrap_vector_invokes(LLVMModuleRef sourceRef)
{
    auto *module = llvm::unwrap(sourceRef);
    std::vector<llvm::InvokeInst *> invokes;
    for (auto &function : *module)
        for (auto &block : function)
            if (auto *invoke = llvm::dyn_cast<llvm::InvokeInst>(block.getTerminator()))
                if (invoke->getType()->isVectorTy()) invokes.push_back(invoke);
    for (auto *invoke : invokes) {
        auto &context = module->getContext();
        auto *resultType = llvm::StructType::get(context, llvm::ArrayRef<llvm::Type *>{invoke->getType()});
        std::vector<llvm::Type *> parameters;
        std::vector<llvm::Value *> arguments;
        std::vector<llvm::AttributeSet> attributes;
        for (unsigned index = 0; index < invoke->arg_size(); ++index) {
            parameters.push_back(invoke->getArgOperand(index)->getType());
            arguments.push_back(invoke->getArgOperand(index));
            attributes.push_back(invoke->getAttributes().getParamAttrs(index));
        }
        parameters.push_back(invoke->getCalledOperand()->getType());
        arguments.push_back(invoke->getCalledOperand());
        attributes.push_back({});
        auto wrapperAttributes = llvm::AttributeList::get(context, invoke->getAttributes().getFnAttrs(), {}, attributes);
        auto *signature = llvm::FunctionType::get(resultType, parameters, false);
        auto *wrapper = llvm::Function::Create(signature, llvm::GlobalValue::InternalLinkage, "__llvmnet_vector_invoke", module);
        wrapper->setAttributes(wrapperAttributes);
        auto *entry = llvm::BasicBlock::Create(context, "entry", wrapper);
        llvm::IRBuilder<> builder(entry);
        std::vector<llvm::Value *> forwarded;
        for (unsigned index = 0; index < invoke->arg_size(); ++index) forwarded.push_back(wrapper->getArg(index));
        auto *call = builder.CreateCall(invoke->getFunctionType(), wrapper->getArg(invoke->arg_size()), forwarded);
        call->setAttributes(invoke->getAttributes());
        call->setCallingConv(invoke->getCallingConv());
        builder.CreateRet(builder.CreateInsertValue(llvm::PoisonValue::get(resultType), call, 0));
        auto *normal = invoke->getNormalDest();
        auto *continuation = llvm::BasicBlock::Create(context, "vector.result", invoke->getFunction(), normal);
        normal->replacePhiUsesWith(invoke->getParent(), continuation);
        llvm::IRBuilder<> replacement(invoke);
        auto *wrapped = replacement.CreateInvoke(wrapper, continuation, invoke->getUnwindDest(), arguments);
        wrapped->setAttributes(wrapperAttributes);
        llvm::IRBuilder<> resume(continuation);
        auto *value = resume.CreateExtractValue(wrapped, 0);
        resume.CreateBr(normal);
        invoke->replaceAllUsesWith(value);
        invoke->eraseFromParent();
    }
    return invokes.size();
}

extern "C" void llvmnet_add_native_thunk(LLVMModuleRef shimRef, LLVMValueRef functionRef,
                                        LLVMValueRef callRef, const char *exportName)
{
    auto *module = llvm::unwrap(shimRef);
    auto *source = functionRef ? llvm::cast<llvm::Function>(llvm::unwrap(functionRef)) : nullptr;
    auto *callSource = callRef ? llvm::cast<llvm::CallBase>(llvm::unwrap(callRef)) : nullptr;
    auto *assembly = !source && callSource ? llvm::dyn_cast<llvm::InlineAsm>(callSource->getCalledOperand()) : nullptr;
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
    if (!source && !assembly) types.push_back(pointer);
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
    llvm::FunctionCallee target;
    if (source) {
        target = module->getOrInsertFunction(source->getName(), source->getFunctionType());
    } else if (assembly) {
        target = llvm::FunctionCallee(callSource->getFunctionType(), assembly);
    } else {
        target = llvm::FunctionCallee(callSource->getFunctionType(), arguments.back());
        arguments.pop_back();
    }
    auto *call = builder.CreateCall(target, arguments);
    call->setCallingConv(callSource ? callSource->getCallingConv() : source->getCallingConv());
    call->setAttributes(callSource ? callSource->getAttributes() : source->getAttributes());
    if (!call->getType()->isVoidTy())
        builder.CreateStore(call, thunk->getArg(0))->setAlignment(llvm::Align(1));
    builder.CreateRetVoid();
}