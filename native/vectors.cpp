#include <llvm-c/Core.h>
#include <llvm/CodeGen/ExpandReductions.h>
#include <llvm/IR/Function.h>
#include <llvm/IR/IRBuilder.h>
#include <llvm/IR/Instructions.h>
#include <llvm/IR/Intrinsics.h>
#include <llvm/IR/Module.h>
#include <algorithm>
#include <llvm/IR/PassManager.h>
#include <llvm/Passes/PassBuilder.h>
#include <llvm/Support/CBindingWrapping.h>
#include <llvm/Transforms/Scalar/Scalarizer.h>

// Scalarizes the vector operations of the given function definitions as opt's
// function(expand-reductions,scalarizer<load-store>) does, and returns how many it ran on. The typed vector lowering translates the vector code of every other function; the
// compiler passes the functions whose vector code it does not translate (lanes of no CIL vector type, vector
// intrinsics without a lowering), which then run as scalar code, as all vector code did before.
extern "C" unsigned llvmnet_scalarize_functions(LLVMValueRef *functions, unsigned count)
{
    llvm::LoopAnalysisManager loops;
    llvm::FunctionAnalysisManager analyses;
    llvm::CGSCCAnalysisManager cgscc;
    llvm::ModuleAnalysisManager modules;
    llvm::PassBuilder builder;
    builder.registerModuleAnalyses(modules);
    builder.registerCGSCCAnalyses(cgscc);
    builder.registerFunctionAnalyses(analyses);
    builder.registerLoopAnalyses(loops);
    builder.crossRegisterProxies(loops, analyses, cgscc, modules);
    llvm::ScalarizerPassOptions options;
    options.ScalarizeLoadStore = true;
    llvm::FunctionPassManager passes;
    passes.addPass(llvm::ExpandReductionsPass());
    passes.addPass(llvm::ScalarizerPass(options));
    unsigned scalarized = 0;
    for (unsigned index = 0; index < count; ++index) {
        llvm::Function *function = llvm::unwrap<llvm::Function>(functions[index]);
        if (function == nullptr || function->isDeclaration()) continue;
        passes.run(*function, analyses);
        ++scalarized;
    }
    return scalarized;
}

// Integers wider than 128 bits that LLVM's InstCombine makes of 256- and 512-bit vectors (SV-09): the vector bitcast to
// one integer, of which lshr and trunc take a part (a sub-vector at an offset, as Highway's AVX2 broadcasts compile), or
// which is compared with zero or all ones. llvmnet has no integers wider than 128 bits, so the parts become shuffles or
// element extracts and the comparisons or- and and-reductions of 64-bit lanes. Returns how many it rewrote.
extern "C" unsigned llvmnet_narrow_vector_integers(LLVMModuleRef moduleRef)
{
    auto *module = llvm::unwrap(moduleRef);
    std::vector<llvm::BitCastInst *> casts;
    for (auto &function : *module)
        for (auto &block : function)
            for (auto &instruction : block)
                if (auto *cast = llvm::dyn_cast<llvm::BitCastInst>(&instruction))
                    if (cast->getType()->isIntegerTy() && cast->getType()->getIntegerBitWidth() > 128 &&
                        llvm::isa<llvm::FixedVectorType>(cast->getOperand(0)->getType()) && cast->getOperand(0)->getType()->getScalarSizeInBits() >= 8)
                        casts.push_back(cast);
    unsigned rewritten = 0;
    for (auto *cast : casts) {
        llvm::Value *vector = cast->getOperand(0);
        auto *vectorType = llvm::cast<llvm::FixedVectorType>(vector->getType());
        unsigned elementBits = vectorType->getScalarSizeInBits(), lanes = vectorType->getNumElements();
        std::vector<llvm::User *> users(cast->user_begin(), cast->user_end());
        for (auto *user : users) {
            auto *instruction = llvm::cast<llvm::Instruction>(user);
            uint64_t shift = 0;
            llvm::TruncInst *truncation = llvm::dyn_cast<llvm::TruncInst>(instruction);
            if (!truncation && instruction->getOpcode() == llvm::Instruction::LShr && instruction->getOperand(0) == cast &&
                llvm::isa<llvm::ConstantInt>(instruction->getOperand(1)) && instruction->hasOneUse() &&
                llvm::isa<llvm::TruncInst>(*instruction->user_begin())) {
                shift = llvm::cast<llvm::ConstantInt>(instruction->getOperand(1))->getZExtValue();
                truncation = llvm::cast<llvm::TruncInst>(*instruction->user_begin());
            }
            if (truncation) {
                unsigned bits = truncation->getType()->getIntegerBitWidth();
                if (shift % elementBits != 0 || bits % elementBits != 0 || shift / elementBits + bits / elementBits > lanes)
                    continue;
                unsigned first = shift / elementBits, count = bits / elementBits;
                llvm::IRBuilder<> builder(truncation);
                llvm::Value *part;
                if (count == 1) {
                    part = builder.CreateExtractElement(vector, builder.getInt64(first));
                } else {
                    llvm::SmallVector<int, 16> mask;
                    for (unsigned index = 0; index < count; ++index) mask.push_back(static_cast<int>(first + index));
                    part = builder.CreateShuffleVector(vector, mask);
                }
                truncation->replaceAllUsesWith(builder.CreateBitCast(part, truncation->getType()));
                truncation->eraseFromParent();
                if (instruction != truncation) instruction->eraseFromParent();
                ++rewritten;
                continue;
            }
            auto *compare = llvm::dyn_cast<llvm::ICmpInst>(instruction);
            auto *constant = compare ? llvm::dyn_cast<llvm::ConstantInt>(compare->getOperand(1)) : nullptr;
            if (!compare || !compare->isEquality() || compare->getOperand(0) != cast || !constant ||
                !(constant->isZero() || constant->isMinusOne()) || vectorType->getPrimitiveSizeInBits() % 64 != 0)
                continue;
            llvm::IRBuilder<> builder(compare);
            bool zero = constant->isZero();
            llvm::Value *quads = builder.CreateBitCast(vector, llvm::FixedVectorType::get(builder.getInt64Ty(), vectorType->getPrimitiveSizeInBits() / 64));
            llvm::Value *reduced = zero ? builder.CreateOrReduce(quads) : builder.CreateAndReduce(quads);
            compare->replaceAllUsesWith(builder.CreateICmp(compare->getPredicate(), reduced, builder.getInt64(zero ? 0 : ~0ull)));
            compare->eraseFromParent();
            ++rewritten;
        }
        if (cast->use_empty()) cast->eraseFromParent();
    }
    return rewritten;
}

// llvm.fmuladd may round once or twice. Native code rounds once wherever the function's target has FMA (-march=x86-64-v3,
// -mfma, target("fma")), and llvmnet otherwise rounds twice (architecture.md), so in those functions the calls become
// llvm.fma, which .NET computes fused on every host (SV-09). Runs before the target attributes are removed; returns how
// many calls it changed.
extern "C" unsigned llvmnet_fuse_multiply_adds(LLVMModuleRef moduleRef)
{
    auto *module = llvm::unwrap(moduleRef);
    unsigned fused = 0;
    for (auto &function : *module) {
        if (function.isDeclaration()) continue;
        llvm::Attribute features = function.getFnAttribute("target-features");
        if (!features.isValid()) continue;
        llvm::SmallVector<llvm::StringRef, 64> list;
        features.getValueAsString().split(list, ',');
        if (std::find(list.begin(), list.end(), "+fma") == list.end()) continue;
        std::vector<llvm::CallInst *> calls;
        for (auto &block : function)
            for (auto &instruction : block)
                if (auto *call = llvm::dyn_cast<llvm::CallInst>(&instruction))
                    if (call->getIntrinsicID() == llvm::Intrinsic::fmuladd) calls.push_back(call);
        for (auto *call : calls) {
            llvm::Function *fma = llvm::Intrinsic::getOrInsertDeclaration(module, llvm::Intrinsic::fma, {call->getType()});
            call->setCalledFunction(fma);
            ++fused;
        }
    }
    return fused;
}
