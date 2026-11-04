#include <llvm-c/Core.h>
#include <llvm/CodeGen/ExpandReductions.h>
#include <llvm/IR/Function.h>
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
