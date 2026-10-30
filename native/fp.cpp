#include <llvm-c/Core.h>
#include <llvm/IR/IRBuilder.h>
#include <llvm/IR/IntrinsicInst.h>
#include <llvm/IR/Module.h>
#include <llvm/Support/CBindingWrapping.h>
#include <optional>
#include <string>
#include <vector>

// Replaces llvm.experimental.constrained.* calls with the equivalent plain instruction or intrinsic and returns
// the number replaced. llvmnet's floating-point environment is round-to-nearest-even without exception flags, so
// the plain form computes the same value unless the call names another static rounding mode; such calls are left
// for the compiler to reject.
extern "C" unsigned llvmnet_lower_constrained_fp(LLVMModuleRef handle)
{
    llvm::Module &module = *llvm::unwrap(handle);
    std::vector<llvm::ConstrainedFPIntrinsic *> calls;
    for (llvm::Function &function : module)
        for (llvm::BasicBlock &block : function)
            for (llvm::Instruction &instruction : block)
                if (auto *call = llvm::dyn_cast<llvm::ConstrainedFPIntrinsic>(&instruction))
                    calls.push_back(call);
    unsigned replaced = 0;
    for (llvm::ConstrainedFPIntrinsic *call : calls) {
        std::optional<llvm::RoundingMode> rounding = call->getRoundingMode();
        if (rounding && *rounding != llvm::RoundingMode::NearestTiesToEven && *rounding != llvm::RoundingMode::Dynamic)
            continue;
        llvm::IRBuilder<> builder(call);
        std::string name = call->getCalledFunction()->getName().str();
        const std::string prefix = "llvm.experimental.constrained.";
        std::string rest = name.substr(prefix.size());
        std::string operation = rest.substr(0, rest.find('.'));
        unsigned count = call->getNonMetadataArgCount();
        std::vector<llvm::Value *> arguments(call->arg_begin(), call->arg_begin() + count);
        llvm::Value *result = nullptr;
        if (auto *compare = llvm::dyn_cast<llvm::ConstrainedFPCmpIntrinsic>(call))
            result = builder.CreateFCmp(compare->getPredicate(), arguments[0], arguments[1]);
        else if (operation == "fadd") result = builder.CreateFAdd(arguments[0], arguments[1]);
        else if (operation == "fsub") result = builder.CreateFSub(arguments[0], arguments[1]);
        else if (operation == "fmul") result = builder.CreateFMul(arguments[0], arguments[1]);
        else if (operation == "fdiv") result = builder.CreateFDiv(arguments[0], arguments[1]);
        else if (operation == "frem") result = builder.CreateFRem(arguments[0], arguments[1]);
        else if (operation == "fptosi") result = builder.CreateFPToSI(arguments[0], call->getType());
        else if (operation == "fptoui") result = builder.CreateFPToUI(arguments[0], call->getType());
        else if (operation == "sitofp") result = builder.CreateSIToFP(arguments[0], call->getType());
        else if (operation == "uitofp") result = builder.CreateUIToFP(arguments[0], call->getType());
        else if (operation == "fptrunc") result = builder.CreateFPTrunc(arguments[0], call->getType());
        else if (operation == "fpext") result = builder.CreateFPExt(arguments[0], call->getType());
        else {
            // Math intrinsics keep their name and overload suffix: llvm.experimental.constrained.sqrt.f64 -> llvm.sqrt.f64.
            std::vector<llvm::Type *> types;
            for (llvm::Value *argument : arguments) types.push_back(argument->getType());
            llvm::FunctionCallee plain = module.getOrInsertFunction("llvm." + rest, llvm::FunctionType::get(call->getType(), types, false));
            result = builder.CreateCall(plain, arguments);
        }
        call->replaceAllUsesWith(result);
        call->eraseFromParent();
        ++replaced;
    }
    return replaced;
}
