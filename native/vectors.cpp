#include <llvm-c/Core.h>
#include <llvm/ADT/SmallPtrSet.h>
#include <llvm/Analysis/ValueTracking.h>
#include <llvm/CodeGen/ExpandReductions.h>
#include <llvm/IR/Function.h>
#include <llvm/IR/GetElementPtrTypeIterator.h>
#include <llvm/IR/IRBuilder.h>
#include <llvm/IR/Instructions.h>
#include <llvm/IR/Intrinsics.h>
#include <llvm/IR/IntrinsicsX86.h>
#include <llvm/IR/Module.h>
#include <llvm/IR/Operator.h>
#include <algorithm>
#include <map>
#include <numeric>
#include <optional>
#include <llvm/IR/PassManager.h>
#include <llvm/Passes/PassBuilder.h>
#include <llvm/Support/CBindingWrapping.h>
#include <llvm/Transforms/Scalar/Scalarizer.h>
#include <llvm/Transforms/Utils/Local.h>

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

// Vector getelementptrs, which the loop vectorizer's runtime alias checks and gathers make (getelementptr i8, <2 x ptr>
// %bounds, i64 1), as integer arithmetic on the addresses: ptrtoint of the base (a scalar base splatted), plus each
// index sign-extended to 64 bits and scaled by its element size (a shift for a power of two), plus the field offsets of
// struct indices, and inttoptr back. The typed lowering translates those and scalarized a function with a vector GEP,
// its vectorized loops included (x264's frame_init_lowres_core ran 26 times native that way, SV-25). Returns how many
// it rewrote.
extern "C" unsigned llvmnet_expand_vector_geps(LLVMModuleRef moduleRef)
{
    auto *module = llvm::unwrap(moduleRef);
    const llvm::DataLayout &layout = module->getDataLayout();
    std::vector<llvm::GetElementPtrInst *> geps;
    for (auto &function : *module)
        for (auto &block : function)
            for (auto &instruction : block)
                if (auto *gep = llvm::dyn_cast<llvm::GetElementPtrInst>(&instruction); gep != nullptr && gep->getType()->isVectorTy())
                    geps.push_back(gep);
    for (auto *gep : geps) {
        llvm::IRBuilder<> builder(gep);
        auto *type = llvm::cast<llvm::FixedVectorType>(gep->getType());
        unsigned lanes = type->getNumElements();
        auto *addresses = llvm::FixedVectorType::get(builder.getInt64Ty(), lanes);
        auto splat = [&](llvm::Value *value) { return value->getType()->isVectorTy() ? value : builder.CreateVectorSplat(lanes, value); };
        llvm::Value *sum = builder.CreatePtrToInt(splat(gep->getPointerOperand()), addresses);
        uint64_t constant = 0;
        auto step = llvm::gep_type_begin(gep);
        for (auto index = gep->idx_begin(); index != gep->idx_end(); ++index, ++step) {
            llvm::Value *operand = *index;
            if (llvm::StructType *structure = step.getStructTypeOrNull()) {
                auto *field = llvm::cast<llvm::Constant>(operand);
                if (field->getType()->isVectorTy()) field = field->getSplatValue();
                constant += layout.getStructLayout(structure)->getElementOffset(llvm::cast<llvm::ConstantInt>(field)->getZExtValue());
                continue;
            }
            uint64_t stride = step.getSequentialElementStride(layout).getFixedValue();
            if (stride == 0) continue;
            if (auto *number = llvm::dyn_cast<llvm::ConstantInt>(operand)) {
                constant += number->getSExtValue() * stride;
                continue;
            }
            llvm::Value *offset = builder.CreateSExtOrTrunc(splat(operand), addresses);
            if (llvm::isPowerOf2_64(stride)) {
                if (stride > 1) offset = builder.CreateShl(offset, llvm::ConstantInt::get(addresses, llvm::Log2_64(stride)));
            } else {
                offset = builder.CreateMul(offset, llvm::ConstantInt::get(addresses, stride));
            }
            sum = builder.CreateAdd(sum, offset);
        }
        if (constant != 0) sum = builder.CreateAdd(sum, llvm::ConstantInt::get(addresses, constant));
        llvm::Value *result = builder.CreateIntToPtr(sum, type);
        result->takeName(gep);
        gep->replaceAllUsesWith(result);
        gep->eraseFromParent();
    }
    return static_cast<unsigned>(geps.size());
}

// llvm.experimental.cttz.elts(<N x i1> mask, i1 zero_is_poison): the index of the first true lane, N when there is none.
// LLVM's loop vectorizer makes it for early-exit loops (the lane where a vectorized search left the loop, as in
// for (...) if (a[i] == v) return i;). The typed lowering has no translation and LLVM's scalarizer leaves it, so a program
// with such a loop failed to link (SV-31); it becomes the umin reduction of the lane indices the mask selects, N in the
// other lanes, as SelectionDAG expands it generically, in lanes of 8 bits for fewer than 256 lanes. Returns how many it
// rewrote.
extern "C" unsigned llvmnet_expand_cttz_elts(LLVMModuleRef moduleRef)
{
    auto *module = llvm::unwrap(moduleRef);
    std::vector<llvm::CallInst *> calls;
    for (auto &function : *module)
        for (auto &block : function)
            for (auto &instruction : block)
                if (auto *call = llvm::dyn_cast<llvm::CallInst>(&instruction); call != nullptr &&
                    call->getIntrinsicID() == llvm::Intrinsic::experimental_cttz_elts && llvm::isa<llvm::FixedVectorType>(call->getArgOperand(0)->getType()))
                    calls.push_back(call);
    for (auto *call : calls) {
        llvm::IRBuilder<> builder(call);
        llvm::Value *mask = call->getArgOperand(0);
        unsigned lanes = llvm::cast<llvm::FixedVectorType>(mask->getType())->getNumElements();
        llvm::Type *element = builder.getIntNTy(lanes < 256 ? 8 : lanes < 65536 ? 16 : 32);
        llvm::SmallVector<llvm::Constant *, 16> indices;
        for (unsigned lane = 0; lane < lanes; ++lane) indices.push_back(llvm::ConstantInt::get(element, lane));
        auto *vector = llvm::FixedVectorType::get(element, lanes);
        llvm::Value *selected = builder.CreateSelect(mask, llvm::ConstantVector::get(indices), llvm::ConstantInt::get(vector, lanes));
        llvm::Value *first = builder.CreateUnaryIntrinsic(llvm::Intrinsic::vector_reduce_umin, selected);
        llvm::Value *result = builder.CreateZExtOrTrunc(first, call->getType());
        result->takeName(call);
        call->replaceAllUsesWith(result);
        call->eraseFromParent();
    }
    return static_cast<unsigned>(calls.size());
}

namespace {

unsigned LaneCount(llvm::Type *type)
{
    auto *vector = llvm::dyn_cast<llvm::FixedVectorType>(type);
    return vector ? vector->getNumElements() : 0;
}

bool PowerOfTwo(unsigned value) { return value != 0 && (value & (value - 1)) == 0; }

// Lanes first to first + count - 1 of a vector, zeros where they run past its last lane.
llvm::Value *Lanes(llvm::IRBuilder<> &builder, llvm::Value *vector, unsigned first, unsigned count)
{
    unsigned lanes = LaneCount(vector->getType());
    if (first == 0 && count == lanes) return vector;
    llvm::SmallVector<int, 64> mask(count);
    for (unsigned index = 0; index < count; ++index)
        mask[index] = static_cast<int>(first + index < lanes ? first + index : lanes + (first + index) % lanes);
    return builder.CreateShuffleVector(vector, llvm::Constant::getNullValue(vector->getType()), mask);
}

// The vectors one after the other (each of one type, a power of two of them).
llvm::Value *Concatenate(llvm::IRBuilder<> &builder, llvm::SmallVector<llvm::Value *, 8> parts)
{
    while (parts.size() > 1) {
        llvm::SmallVector<llvm::Value *, 8> pairs;
        for (size_t index = 0; index < parts.size(); index += 2) {
            llvm::SmallVector<int, 64> mask(LaneCount(parts[index]->getType()) * 2);
            std::iota(mask.begin(), mask.end(), 0);
            pairs.push_back(builder.CreateShuffleVector(parts[index], parts[index + 1], mask));
        }
        parts = std::move(pairs);
    }
    return parts[0];
}

// A leaf of a reduction that one instruction computes: the sum of absolute differences of the bytes of left and right,
// or the sum of products of left and right, 32-bit lanes of 16-bit values.
struct Idiom
{
    llvm::Instruction *leaf;
    bool products;
    llvm::Value *left, *right;
};

// abs(sub(zext a, zext b)) of <N x i8> a and b in 16- to 64-bit lanes, which psadbw computes: it sums each eight
// absolute differences into the low bits of a 64-bit lane. The difference may be taken at another width than the sum's
// and extended or truncated to it, which keeps its value (at most 255).
std::optional<Idiom> Differences(llvm::Instruction *leaf)
{
    auto *type = llvm::dyn_cast<llvm::FixedVectorType>(leaf->getType());
    if (type == nullptr || !type->getElementType()->isIntegerTy()) return std::nullopt;
    unsigned bits = type->getScalarSizeInBits(), lanes = type->getNumElements();
    if (bits < 16 || bits > 64 || !PowerOfTwo(bits) || !PowerOfTwo(lanes) || lanes < 2) return std::nullopt;
    llvm::Value *absolute = llvm::isa<llvm::ZExtInst>(leaf) || llvm::isa<llvm::TruncInst>(leaf) ? leaf->getOperand(0) : leaf;
    llvm::Value *difference = nullptr, *negated = nullptr;
    if (auto *call = llvm::dyn_cast<llvm::CallInst>(absolute); call != nullptr && call->getIntrinsicID() == llvm::Intrinsic::abs)
        difference = call->getArgOperand(0);
    else if (!llvm::isa<llvm::SelectInst>(absolute) || llvm::matchSelectPattern(absolute, difference, negated).Flavor != llvm::SPF_ABS)
        return std::nullopt;
    auto *subtract = llvm::dyn_cast_or_null<llvm::BinaryOperator>(difference);
    if (subtract == nullptr || subtract->getOpcode() != llvm::Instruction::Sub) return std::nullopt;
    auto bytes = [](llvm::Value *value) -> llvm::Value * {
        auto *extension = llvm::dyn_cast<llvm::ZExtInst>(value);
        return extension != nullptr && extension->getOperand(0)->getType()->getScalarType()->isIntegerTy(8) ? extension->getOperand(0) : nullptr;
    };
    llvm::Value *left = bytes(subtract->getOperand(0)), *right = bytes(subtract->getOperand(1));
    if (left == nullptr || right == nullptr) return std::nullopt;
    return Idiom{leaf, false, left, right};
}

// The 16-bit lanes whose sign extension the value is: the source of a sext from at most 16 bits or of a zext from fewer
// (extended to 16 bits before the builder's position), or a constant of 16-bit signed values; null for another value.
// Without a builder it only answers whether there are such lanes.
llvm::Value *Halfwords(llvm::IRBuilder<> *builder, llvm::Value *value)
{
    auto *type = llvm::cast<llvm::FixedVectorType>(value->getType());
    auto *narrow = llvm::FixedVectorType::get(llvm::Type::getInt16Ty(value->getContext()), type->getNumElements());
    if (auto *cast = llvm::dyn_cast<llvm::CastInst>(value)) {
        llvm::Value *source = cast->getOperand(0);
        unsigned bits = source->getType()->getScalarSizeInBits();
        bool sign = cast->getOpcode() == llvm::Instruction::SExt;
        if (!(sign && bits <= 16) && !(cast->getOpcode() == llvm::Instruction::ZExt && bits < 16)) return nullptr;
        if (builder == nullptr || bits == 16) return source;
        return sign ? builder->CreateSExt(source, narrow) : builder->CreateZExt(source, narrow);
    }
    auto *constant = llvm::dyn_cast<llvm::Constant>(value);
    if (constant == nullptr) return nullptr;
    for (unsigned lane = 0; lane < type->getNumElements(); ++lane) {
        auto *element = llvm::dyn_cast_or_null<llvm::ConstantInt>(constant->getAggregateElement(lane));
        if (element == nullptr || !element->getValue().isSignedIntN(16)) return nullptr;
    }
    return builder == nullptr ? constant : builder->CreateTrunc(constant, narrow);
}

// mul(ext a, ext b) in 32-bit lanes of values that fit 16 signed bits, which pmaddwd computes: it adds each two
// adjacent products, wrapping as the 32-bit sum does.
std::optional<Idiom> Products(llvm::Instruction *leaf)
{
    auto *type = llvm::dyn_cast<llvm::FixedVectorType>(leaf->getType());
    if (leaf->getOpcode() != llvm::Instruction::Mul || type == nullptr || !type->getElementType()->isIntegerTy(32)) return std::nullopt;
    unsigned lanes = type->getNumElements();
    if (!PowerOfTwo(lanes) || lanes < 2 || Halfwords(nullptr, leaf->getOperand(0)) == nullptr || Halfwords(nullptr, leaf->getOperand(1)) == nullptr)
        return std::nullopt;
    return Idiom{leaf, true, leaf->getOperand(0), leaf->getOperand(1)};
}

// The bytes one leaf's instruction reads per operand.
unsigned OperandBytes(const Idiom &idiom) { return LaneCount(idiom.leaf->getType()) * (idiom.products ? 2 : 1); }

// Computes a group of leaves of one kind, type and block, in order: the instruction on their operands concatenated,
// 16 bytes at a time (32, AVX2's forms, when vectors run as Vector256 fragments and the group has that many), padded
// with zeros to one instruction. The last leaf takes the result, as lanes of its type (concatenated with zeros or cut
// to its lanes; a reduction of the leaf itself takes all of them) and the others zero. A reduction of one row of at most
// 16 bytes becomes the total alone.
void Replace(llvm::ArrayRef<Idiom> group, unsigned fragmentBits, llvm::CallInst *reduction)
{
    llvm::Instruction *last = group.back().leaf;
    llvm::IRBuilder<> builder(last);
    llvm::SmallVector<llvm::Value *, 8> lefts, rights;
    for (const Idiom &idiom : group) {
        lefts.push_back(idiom.products ? Halfwords(&builder, idiom.left) : idiom.left);
        rights.push_back(idiom.products ? Halfwords(&builder, idiom.right) : idiom.right);
    }
    llvm::Value *left = Concatenate(builder, lefts), *right = Concatenate(builder, rights);
    bool products = group.front().products;
    bool wide = fragmentBits == 256 && OperandBytes(group.front()) * group.size() >= 32;
    llvm::Intrinsic::ID id = products ? wide ? llvm::Intrinsic::x86_avx2_pmadd_wd : llvm::Intrinsic::x86_sse2_pmadd_wd
                                      : wide ? llvm::Intrinsic::x86_avx2_psad_bw : llvm::Intrinsic::x86_sse2_psad_bw;
    unsigned chunk = (wide ? 32 : 16) / (products ? 2 : 1);
    if (reduction != nullptr && !products && LaneCount(left->getType()) <= 16) {
        // A row reduced on its own needs only its total (SV-35): llvmnet's llvm.llvmnet.sad of the row padded with zeros to
        // 16 bytes and its length, which x64 runs as psadbw and the 64-bit sums that summed the row, as below, and ARM64 as
        // uabd and uaddlv, where psadbw's two exact sums take three pairwise additions.
        llvm::Type *bytes = llvm::FixedVectorType::get(builder.getInt8Ty(), 16);
        llvm::FunctionCallee sad = last->getModule()->getOrInsertFunction("llvm.llvmnet.sad", builder.getInt64Ty(), bytes, bytes, builder.getInt32Ty());
        llvm::Value *total = builder.CreateCall(sad, {Lanes(builder, left, 0, 16), Lanes(builder, right, 0, 16), builder.getInt32(LaneCount(left->getType()))});
        builder.SetInsertPoint(reduction);
        reduction->replaceAllUsesWith(builder.CreateZExtOrTrunc(total, reduction->getType()));
        reduction->eraseFromParent();
        llvm::RecursivelyDeleteTriviallyDeadInstructions(last);
        return;
    }
    llvm::Function *intrinsic = llvm::Intrinsic::getOrInsertDeclaration(last->getModule(), id);
    llvm::SmallVector<llvm::Value *, 8> parts;
    for (unsigned first = 0; first < std::max(LaneCount(left->getType()), chunk); first += chunk)
        parts.push_back(builder.CreateCall(intrinsic, {Lanes(builder, left, first, chunk), Lanes(builder, right, first, chunk)}));
    llvm::Value *result = Concatenate(builder, parts);
    // psadbw's 64-bit sums as lanes of the leaf's width: each sum in the low lane of its 64 bits, the others zero.
    auto *type = llvm::cast<llvm::FixedVectorType>(last->getType());
    if (!products)
        result = builder.CreateBitCast(result, llvm::FixedVectorType::get(type->getElementType(), LaneCount(result->getType()) * 64 / type->getScalarSizeInBits()));
    if (reduction != nullptr) {
        builder.SetInsertPoint(reduction);
        llvm::Value *sum;
        if (products) {
            sum = builder.CreateAddReduce(result);
        } else {
            // The sum of psadbw's 64-bit sums (at most 16 bits each) that summed bytes of the operands: the parts add as
            // 64-bit lanes and only those lanes are read, so the zeros of each sum's upper bits and of the padding are
            // not folded in (SV-26: an 8-byte row is one movq, as native code's movd, where the generic reduction of its
            // 32-bit lanes took two shifts and two adds).
            llvm::Value *total = parts.front();
            for (size_t index = 1; index < parts.size(); ++index) total = builder.CreateAdd(total, parts[index]);
            unsigned live = std::min(LaneCount(total->getType()), (LaneCount(left->getType()) + 7) / 8);
            sum = builder.CreateExtractElement(total, uint64_t{0});
            for (unsigned lane = 1; lane < live; ++lane) sum = builder.CreateAdd(sum, builder.CreateExtractElement(total, uint64_t{lane}));
            sum = builder.CreateZExtOrTrunc(sum, reduction->getType());
        }
        reduction->replaceAllUsesWith(sum);
        reduction->eraseFromParent();
        llvm::RecursivelyDeleteTriviallyDeadInstructions(result);
    } else {
        last->replaceAllUsesWith(Lanes(builder, result, 0, type->getNumElements()));
    }
    for (const Idiom &idiom : group) {
        if (idiom.leaf != last) idiom.leaf->replaceAllUsesWith(llvm::Constant::getNullValue(type));
        llvm::RecursivelyDeleteTriviallyDeadInstructions(idiom.leaf);
    }
}

// The leaves of an llvm.vector.reduce.add: the values that reach it through adds and phis of its vector type, the tree,
// when the reduction alone observes the tree's values, so that only the sum of their lanes matters. A leaf with users
// outside the tree is left out. False when another instruction reads a value of the tree.
bool Leaves(llvm::CallInst *reduction, llvm::SmallVectorImpl<llvm::Instruction *> &leaves, llvm::SmallPtrSetImpl<llvm::Instruction *> &tree)
{
    llvm::SmallVector<llvm::Value *, 16> work{reduction->getArgOperand(0)};
    llvm::SmallPtrSet<llvm::Value *, 16> seen;
    while (!work.empty()) {
        auto *instruction = llvm::dyn_cast<llvm::Instruction>(work.pop_back_val());
        if (instruction == nullptr || !seen.insert(instruction).second) continue;
        if (llvm::isa<llvm::PHINode>(instruction) || instruction->getOpcode() == llvm::Instruction::Add) {
            tree.insert(instruction);
            work.append(instruction->op_begin(), instruction->op_end());
        } else {
            leaves.push_back(instruction);
        }
    }
    auto internal = [&](llvm::Instruction *instruction) {
        return llvm::all_of(instruction->users(), [&](llvm::User *user) { return user == reduction || tree.count(llvm::cast<llvm::Instruction>(user)) != 0; });
    };
    if (!llvm::all_of(tree, internal)) return false;
    llvm::erase_if(leaves, [&](llvm::Instruction *leaf) { return !internal(leaf); });
    return true;
}

} // namespace

// The widening reductions LLVM's x86 backend matches to psadbw and pmaddwd (SV-21, after X86PartialReduction): in the
// tree of adds and phis that feeds an llvm.vector.reduce.add, a sum of absolute differences of bytes becomes llvm.x86's
// psad.bw and a sum of 16-bit products pmadd.wd, whose runtime helpers run the instruction where the host has it and an
// exact composition elsewhere. Both put the sums in other lanes than the literal dataflow, which only the reduction's
// total can tell apart, so a tree whose values anything else reads is left alone; for the same reason two leaves of one
// block whose operands are half an instruction wide (the 64-bit vectors of LLVM's x86 cost model) share one instruction,
// and the other leaf becomes zero. Returns how many leaves of differences the function got and stores how many of products.
extern "C" unsigned llvmnet_widening_reductions(LLVMValueRef functionRef, unsigned fragmentBits, unsigned *products)
{
    auto *function = llvm::unwrap<llvm::Function>(functionRef);
    std::vector<llvm::CallInst *> reductions;
    for (auto &block : *function)
        for (auto &instruction : block)
            if (auto *call = llvm::dyn_cast<llvm::CallInst>(&instruction); call != nullptr && call->getIntrinsicID() == llvm::Intrinsic::vector_reduce_add)
                reductions.push_back(call);
    unsigned differences = 0;
    *products = 0;
    if (reductions.empty()) return 0;
    llvm::DenseMap<llvm::Instruction *, unsigned> position;
    unsigned next = 0;
    for (auto &block : *function)
        for (auto &instruction : block) position[&instruction] = next++;
    for (auto *reduction : reductions) {
        llvm::SmallVector<llvm::Instruction *, 8> leaves;
        llvm::SmallPtrSet<llvm::Instruction *, 16> tree;
        if (!Leaves(reduction, leaves, tree)) continue;
        std::vector<Idiom> idioms;
        for (auto *leaf : leaves)
            if (auto idiom = Differences(leaf); idiom || (idiom = Products(leaf)))
                idioms.push_back(*idiom);
        if (idioms.empty()) continue;
        for (const Idiom &idiom : idioms) ++(idiom.products ? *products : differences);
        if (tree.empty()) {
            // The reduction of one leaf sums the replacement's lanes, however many there are.
            Replace(idioms, fragmentBits, reduction);
            continue;
        }
        // Pairs, in program order, of the leaves of one block, kind and type that each fill half a fragment.
        std::sort(idioms.begin(), idioms.end(), [&](const Idiom &left, const Idiom &right) { return position.lookup(left.leaf) < position.lookup(right.leaf); });
        std::vector<bool> done(idioms.size());
        for (size_t index = 0; index < idioms.size(); ++index) {
            if (done[index]) continue;
            llvm::SmallVector<Idiom, 2> group{idioms[index]};
            for (size_t other = index + 1; other < idioms.size() && OperandBytes(idioms[index]) * 2 == fragmentBits / 8; ++other)
                if (!done[other] && idioms[other].leaf->getParent() == idioms[index].leaf->getParent() &&
                    idioms[other].products == idioms[index].products && idioms[other].leaf->getType() == idioms[index].leaf->getType()) {
                    group.push_back(idioms[other]);
                    done[other] = true;
                    break;
                }
            Replace(group, fragmentBits, nullptr);
        }
        // The partial sums now differ per lane, so the tree's adds may wrap where the literal ones did not; and an add
        // of a leaf that became zero is its other operand.
        std::vector<llvm::Instruction *> nodes(tree.begin(), tree.end());
        for (auto *node : nodes) {
            if (!llvm::isa<llvm::BinaryOperator>(node)) continue;
            node->setHasNoSignedWrap(false);
            node->setHasNoUnsignedWrap(false);
            for (unsigned operand = 0; operand < 2; ++operand)
                if (auto *constant = llvm::dyn_cast<llvm::Constant>(node->getOperand(operand)); constant != nullptr && constant->isNullValue()) {
                    node->replaceAllUsesWith(node->getOperand(1 - operand));
                    node->eraseFromParent();
                    break;
                }
        }
    }
    return differences;
}

namespace {

// The terms of a sum of at most two levels of adds, as the vectorizers leave (a + b + 1) in any order.
void Terms(llvm::Value *value, llvm::SmallVectorImpl<llvm::Value *> &terms, unsigned depth)
{
    auto *add = llvm::dyn_cast<llvm::BinaryOperator>(value);
    if (depth == 0 || add == nullptr || add->getOpcode() != llvm::Instruction::Add) {
        terms.push_back(value);
        return;
    }
    Terms(add->getOperand(0), terms, depth - 1);
    Terms(add->getOperand(1), terms, depth - 1);
}

bool SplatOne(llvm::Value *value)
{
    auto *constant = llvm::dyn_cast<llvm::Constant>(value);
    llvm::Constant *splat = constant != nullptr ? constant->getSplatValue() : nullptr;
    return splat != nullptr && splat->isOneValue();
}

// A value known to have no bits above narrow, as lanes of narrow bits: the source of a zext from that width, else a
// zext of a narrower source or a trunc, inserted before the builder's position.
llvm::Value *Narrowed(llvm::IRBuilder<> &builder, llvm::Value *value, llvm::Type *type)
{
    if (auto *extension = llvm::dyn_cast<llvm::ZExtInst>(value)) {
        llvm::Value *source = extension->getOperand(0);
        if (source->getType() == type) return source;
        if (source->getType()->getScalarSizeInBits() < type->getScalarSizeInBits()) return builder.CreateZExt(source, type);
    }
    return builder.CreateTrunc(value, type);
}

} // namespace

// The rounding average LLVM's x86 backend matches to pavgb and pavgw (SV-28, detectAVGPattern): lshr(a + b + 1, 1)
// in lanes wider than 8 (or 16) bits whose a and b have no bits above them, which the vectorizers make of C's
// (a + b + 1) >> 1 on bytes or halfwords (x264's pixel averages and lowres filter, every video codec's half-pel
// interpolation). It becomes llvm.x86's pavg.b or pavg.w on 16-byte pieces (32, AVX2's forms, with 256-bit fragments),
// whose helpers run Sse2.Average, AdvSimd's urhadd or an exact composition, and a trunc of the shift to the narrow lanes
// is the average itself. Returns how many it rewrote.
extern "C" unsigned llvmnet_rounding_averages(LLVMValueRef functionRef, unsigned fragmentBits)
{
    auto *function = llvm::unwrap<llvm::Function>(functionRef);
    const llvm::DataLayout &layout = function->getParent()->getDataLayout();
    std::vector<llvm::BinaryOperator *> shifts;
    for (auto &block : *function)
        for (auto &instruction : block)
            if (auto *shift = llvm::dyn_cast<llvm::BinaryOperator>(&instruction); shift != nullptr && shift->getOpcode() == llvm::Instruction::LShr &&
                llvm::isa<llvm::FixedVectorType>(shift->getType()) && shift->getType()->getScalarType()->isIntegerTy() && SplatOne(shift->getOperand(1)))
                shifts.push_back(shift);
    llvm::SmallVector<llvm::WeakTrackingVH, 16> dead;
    unsigned averages = 0;
    for (auto *shift : shifts) {
        auto *type = llvm::cast<llvm::FixedVectorType>(shift->getType());
        unsigned bits = type->getScalarSizeInBits(), lanes = type->getNumElements();
        llvm::SmallVector<llvm::Value *, 4> terms;
        Terms(shift->getOperand(0), terms, 2);
        if (terms.size() != 3) continue;
        auto one = std::find_if(terms.begin(), terms.end(), SplatOne);
        if (one == terms.end()) continue;
        terms.erase(one);
        unsigned narrow = 0;
        for (unsigned candidate : {8u, 16u})
            if (bits > candidate && llvm::all_of(terms, [&](llvm::Value *term) { return llvm::computeKnownBits(term, layout).countMinLeadingZeros() >= bits - candidate; })) {
                narrow = candidate;
                break;
            }
        unsigned perPiece = (fragmentBits == 256 && lanes * narrow >= 256 ? 256 : 128) / std::max(narrow, 1u);
        unsigned pieces = narrow == 0 ? 0 : (lanes + perPiece - 1) / perPiece;
        if (narrow == 0 || !PowerOfTwo(pieces)) continue;
        llvm::IRBuilder<> builder(shift);
        auto *narrowType = llvm::FixedVectorType::get(builder.getIntNTy(narrow), lanes);
        llvm::Value *left = Narrowed(builder, terms[0], narrowType), *right = Narrowed(builder, terms[1], narrowType);
        llvm::Intrinsic::ID id = narrow == 8 ? perPiece == 32 ? llvm::Intrinsic::x86_avx2_pavg_b : llvm::Intrinsic::x86_sse2_pavg_b
                                             : perPiece == 16 ? llvm::Intrinsic::x86_avx2_pavg_w : llvm::Intrinsic::x86_sse2_pavg_w;
        llvm::Function *intrinsic = llvm::Intrinsic::getOrInsertDeclaration(function->getParent(), id);
        llvm::SmallVector<llvm::Value *, 8> parts;
        for (unsigned first = 0; first < lanes; first += perPiece)
            parts.push_back(builder.CreateCall(intrinsic, {Lanes(builder, left, first, perPiece), Lanes(builder, right, first, perPiece)}));
        llvm::Value *average = Lanes(builder, Concatenate(builder, parts), 0, lanes);
        std::vector<llvm::TruncInst *> truncations;
        for (llvm::User *user : shift->users())
            if (auto *truncation = llvm::dyn_cast<llvm::TruncInst>(user); truncation != nullptr && truncation->getType() == narrowType)
                truncations.push_back(truncation);
        for (auto *truncation : truncations) {
            truncation->replaceAllUsesWith(average);
            truncation->eraseFromParent();
        }
        if (!shift->use_empty()) {
            llvm::Value *wide = builder.CreateZExt(average, type);
            shift->replaceAllUsesWith(wide);
            dead.push_back(wide);
        }
        dead.push_back(shift->getOperand(0));
        shift->eraseFromParent();
        ++averages;
    }
    llvm::RecursivelyDeleteTriviallyDeadInstructionsPermissive(dead);
    return averages;
}


namespace {

// An address as the start of its chain of inbounds getelementptrs (the root), a sum of values times constants and a
// constant offset in bytes: the addresses of one root lie in the root's object, and two with the same terms differ by
// their constants. Integers are taken modulo 2^64, as addresses are, so only 64-bit adds, disjoint ors, subtractions
// and shifts or multiplications by constants are looked through; any other value is a term of its own.
struct Address
{
    llvm::Value *root = nullptr;
    std::vector<std::pair<llvm::Value *, uint64_t>> terms;
    uint64_t offset = 0;
    bool Same(const Address &other) const { return root == other.root && terms == other.terms; }
};

void Linear(llvm::Value *value, uint64_t scale, std::map<llvm::Value *, uint64_t> &terms, uint64_t &offset, unsigned depth)
{
    if (auto *number = llvm::dyn_cast<llvm::ConstantInt>(value); number != nullptr && number->getBitWidth() <= 64) {
        offset += scale * number->getValue().sext(64).getZExtValue();
        return;
    }
    auto *operation = llvm::dyn_cast<llvm::BinaryOperator>(value);
    if (depth != 0 && operation != nullptr && operation->getType()->isIntegerTy(64)) {
        llvm::Value *left = operation->getOperand(0), *right = operation->getOperand(1);
        auto *constant = llvm::dyn_cast<llvm::ConstantInt>(right);
        switch (operation->getOpcode()) {
        case llvm::Instruction::Or:
            if (!llvm::cast<llvm::PossiblyDisjointInst>(operation)->isDisjoint()) break;
            [[fallthrough]];
        case llvm::Instruction::Add:
            Linear(left, scale, terms, offset, depth - 1);
            Linear(right, scale, terms, offset, depth - 1);
            return;
        case llvm::Instruction::Sub:
            Linear(left, scale, terms, offset, depth - 1);
            Linear(right, 0 - scale, terms, offset, depth - 1);
            return;
        case llvm::Instruction::Shl:
            if (constant == nullptr || constant->getZExtValue() >= 64) break;
            Linear(left, scale << constant->getZExtValue(), terms, offset, depth - 1);
            return;
        case llvm::Instruction::Mul:
            if (constant == nullptr) break;
            Linear(left, scale * constant->getZExtValue(), terms, offset, depth - 1);
            return;
        default:
            break;
        }
    }
    terms[value] += scale;
}

Address Decompose(llvm::Value *pointer, const llvm::DataLayout &layout)
{
    std::map<llvm::Value *, uint64_t> terms;
    uint64_t offset = 0;
    while (auto *gep = llvm::dyn_cast<llvm::GEPOperator>(pointer)) {
        llvm::SmallMapVector<llvm::Value *, llvm::APInt, 4> variables;
        llvm::APInt constant(64, 0);
        if (!gep->isInBounds() || gep->getType()->isVectorTy() || layout.getIndexTypeSizeInBits(gep->getType()) != 64 ||
            !gep->collectOffset(layout, 64, variables, constant))
            break;
        for (auto &[index, scale] : variables) Linear(index, scale.getZExtValue(), terms, offset, 8);
        offset += constant.getZExtValue();
        pointer = gep->getPointerOperand();
    }
    Address address{pointer, {}, offset};
    for (auto &[value, scale] : terms)
        if (scale != 0) address.terms.emplace_back(value, scale);
    return address;
}

// A simple load or store of a block: its address, its size and the stretch of the block it executes with.
struct Access
{
    unsigned region;
    Address address;
    uint64_t size;
};

// An insertelement chain that builds every lane of a vector from a simple load of its block, lane k's load at lane 0's
// address plus k strides of one range, and the plan for it: pieces (vector loads of piece bytes) at offsets from lane
// 0's address, out of whose concatenation one shufflevector takes the lanes.
struct Gather
{
    llvm::InsertElementInst *end;
    llvm::SmallVector<llvm::LoadInst *, 16> loads;
    Address address;
    uint64_t stride = 0, element = 0, span = 0, piece = 0;
    llvm::LoadInst *last = nullptr;
    unsigned position = 0, epoch = 0, region = 0;
    llvm::SmallVector<int64_t, 4> starts;
    uint64_t Pieces() const { return (span + piece - 1) / piece; }
};

// The gather the chain ending at end is, if it is one: at least four lanes of 1 to 8 bytes, each set once, a stride
// apart that is a multiple of the lane size, within 64 bytes.
std::optional<Gather> Chain(llvm::InsertElementInst *end, const llvm::DataLayout &layout)
{
    auto *type = llvm::cast<llvm::FixedVectorType>(end->getType());
    llvm::Type *lane = type->getElementType();
    unsigned lanes = type->getNumElements();
    uint64_t bytes = layout.getTypeStoreSize(lane);
    if (lanes < 4 || lanes > 64 || !(lane->isIntegerTy() || lane->isFloatingPointTy()) || bytes > 8 || !llvm::isPowerOf2_64(bytes) ||
        layout.getTypeSizeInBits(lane) != bytes * 8)
        return std::nullopt;
    Gather gather{end};
    gather.loads.assign(lanes, nullptr);
    llvm::Value *vector = end;
    while (auto *insert = llvm::dyn_cast<llvm::InsertElementInst>(vector)) {
        auto *index = llvm::dyn_cast<llvm::ConstantInt>(insert->getOperand(2));
        auto *load = llvm::dyn_cast<llvm::LoadInst>(insert->getOperand(1));
        if ((insert != end && !insert->hasOneUse()) || insert->getParent() != end->getParent() || index == nullptr || index->getZExtValue() >= lanes ||
            load == nullptr || !load->isSimple() || load->getType() != lane || load->getParent() != end->getParent() ||
            gather.loads[index->getZExtValue()] != nullptr)
            return std::nullopt;
        gather.loads[index->getZExtValue()] = load;
        vector = insert->getOperand(0);
    }
    if (!llvm::isa<llvm::UndefValue>(vector) || llvm::is_contained(gather.loads, nullptr)) return std::nullopt;
    gather.address = Decompose(gather.loads[0]->getPointerOperand(), layout);
    for (unsigned index = 1; index < lanes; ++index) {
        Address other = Decompose(gather.loads[index]->getPointerOperand(), layout);
        if (index == 1) gather.stride = other.offset - gather.address.offset;
        if (!other.Same(gather.address) || other.offset - gather.address.offset != gather.stride * index) return std::nullopt;
    }
    gather.element = bytes;
    gather.span = gather.stride * (lanes - 1) + bytes;
    if (gather.stride < bytes || gather.stride % bytes != 0 || gather.span > 64) return std::nullopt;
    return gather;
}

// Whether an instruction ends the stretch of a block in which accesses prove each other's bytes dereferenceable: a call,
// which may free the object, or an instruction after which the block may not go on.
bool Barrier(llvm::Instruction &instruction)
{
    return (llvm::isa<llvm::CallBase>(instruction) && !llvm::isa<llvm::IntrinsicInst>(instruction)) ||
           !llvm::isGuaranteedToTransferExecutionToSuccessor(&instruction);
}

// Multiples of a power of two at or below and at or above a value.
int64_t Floor(int64_t value, int64_t unit) { return value & -unit; }
int64_t Ceil(int64_t value, int64_t unit) { return (value + unit - 1) & -unit; }

// Plans the gathers of one block, in the order of their last loads. A gather's pieces read the bytes between and around
// its lanes only where the block shows them dereferenceable: in the stretch the gather's loads execute with (no barrier
// between), every simple access through the same root and terms is in the root's object, the object is contiguous, so
// every byte from the lowest to the highest of those accesses is; so are the bytes the root is known to point to (an
// alloca, a global, a dereferenceable argument or load) when the address has no terms. The pieces are 16 bytes (fewer for
// a shorter gather), as few as cover the gather, inside those bytes; where they do not fit, the pieces start at the first
// lane and end at the last one, overlapping, and a gather shorter than its piece reads two halves. Gathers that see one
// memory (no write between them) share pieces at the same address, whose offsets interval stabbing picks.
std::vector<Gather> Plan(llvm::BasicBlock &block, const llvm::DataLayout &layout)
{
    struct Place
    {
        unsigned position, epoch, region;
    };
    std::vector<Gather> gathers;
    std::vector<Access> accesses;
    llvm::DenseMap<llvm::Instruction *, Place> places;
    unsigned position = 0, epoch = 0, region = 0;
    for (auto &instruction : block) {
        places[&instruction] = {position++, epoch, region};
        auto *load = llvm::dyn_cast<llvm::LoadInst>(&instruction);
        auto *store = llvm::dyn_cast<llvm::StoreInst>(&instruction);
        if ((load != nullptr && load->isSimple()) || (store != nullptr && store->isSimple()))
            accesses.push_back({region, Decompose(llvm::getLoadStorePointerOperand(&instruction), layout),
                                layout.getTypeStoreSize(llvm::getLoadStoreType(&instruction)).getFixedValue()});
        auto *insert = llvm::dyn_cast<llvm::InsertElementInst>(&instruction);
        auto *next = insert != nullptr && insert->hasOneUse() ? llvm::dyn_cast<llvm::InsertElementInst>(*insert->user_begin()) : nullptr;
        if (insert != nullptr && llvm::isa<llvm::FixedVectorType>(insert->getType()) && (next == nullptr || next->getOperand(0) != insert))
            if (auto gather = Chain(insert, layout)) {
                // The loads see one memory when nothing between them writes and execute together when no barrier is
                // between them; the pieces load before the last of them.
                bool together = true;
                for (auto *lane : gather->loads) {
                    Place place = places.lookup(lane);
                    if (gather->last == nullptr) {
                        gather->epoch = place.epoch;
                        gather->region = place.region;
                    }
                    together &= place.epoch == gather->epoch && place.region == gather->region;
                    if (gather->last == nullptr || place.position > gather->position) {
                        gather->last = lane;
                        gather->position = place.position;
                    }
                }
                if (together) gathers.push_back(std::move(*gather));
            }
        if (Barrier(instruction)) ++region;
        if (instruction.mayWriteToMemory()) ++epoch;
    }
    // The offsets, from lane 0, at which a gather's pieces may start one after the other.
    struct Window
    {
        size_t gather;
        int64_t low, high;
    };
    std::vector<Window> windows;
    for (size_t index = 0; index < gathers.size(); ++index) {
        Gather &gather = gathers[index];
        auto element = static_cast<int64_t>(gather.element), span = static_cast<int64_t>(gather.span);
        int64_t low = 0, high = span;
        for (const Access &access : accesses)
            if (access.region == gather.region && access.address.Same(gather.address)) {
                auto relative = static_cast<int64_t>(access.address.offset - gather.address.offset);
                low = std::min(low, relative);
                high = std::max(high, relative + static_cast<int64_t>(access.size));
            }
        if (gather.address.terms.empty()) {
            bool canBeNull = false, canBeFreed = false;
            auto known = static_cast<int64_t>(gather.address.root->getPointerDereferenceableBytes(layout, canBeNull, canBeFreed));
            int64_t start = -static_cast<int64_t>(gather.address.offset);
            if (known > 0 && start < high && low < start + known) {
                low = std::min(low, start);
                high = std::max(high, start + known);
            }
        }
        gather.piece = std::min<uint64_t>(16, llvm::PowerOf2Ceil(gather.span));
        auto piece = static_cast<int64_t>(gather.piece), size = static_cast<int64_t>(gather.Pieces()) * piece;
        int64_t first = Ceil(std::max(low, span - size), element), last = Floor(std::min<int64_t>(0, high - size), element);
        if (first <= last) {
            windows.push_back({index, first, last});
        } else if (span < piece) {
            gather.piece /= 2;
            gather.starts = {0, span - piece / 2};
        } else {
            for (int64_t at = 0; at + 1 < static_cast<int64_t>(gather.Pieces()); ++at) gather.starts.push_back(at * piece);
            gather.starts.push_back(span - piece);
        }
    }
    std::vector<bool> placed(windows.size());
    for (size_t index = 0; index < windows.size(); ++index) {
        if (placed[index]) continue;
        const Gather &reference = gathers[windows[index].gather];
        // Offsets from the reference's lane 0, the same modulo the lane size in a class.
        auto shift = [&](const Window &window) { return static_cast<int64_t>(gathers[window.gather].address.offset - reference.address.offset); };
        std::vector<size_t> members;
        for (size_t other = index; other < windows.size(); ++other) {
            const Gather &gather = gathers[windows[other].gather];
            if (!placed[other] && gather.epoch == reference.epoch && gather.address.Same(reference.address) &&
                gather.loads[0]->getType() == reference.loads[0]->getType() && gather.piece == reference.piece &&
                gather.Pieces() == reference.Pieces() && shift(windows[other]) % static_cast<int64_t>(gather.element) == 0) {
                placed[other] = true;
                members.push_back(other);
            }
        }
        std::stable_sort(members.begin(), members.end(),
                         [&](size_t left, size_t right) { return windows[left].high + shift(windows[left]) < windows[right].high + shift(windows[right]); });
        std::optional<int64_t> stab;
        for (size_t member : members) {
            const Window &window = windows[member];
            if (!stab || window.low + shift(window) > *stab) stab = window.high + shift(window);
            Gather &gather = gathers[window.gather];
            for (uint64_t at = 0; at < gather.Pieces(); ++at) gather.starts.push_back(*stab - shift(window) + static_cast<int64_t>(at * gather.piece));
        }
    }
    std::stable_sort(gathers.begin(), gathers.end(), [](const Gather &left, const Gather &right) { return left.position < right.position; });
    return gathers;
}

// The pieces one after the other, as a tree of concatenating shufflevectors; a piece without a partner gets undefined
// lanes above it.
llvm::Value *Join(llvm::IRBuilder<> &builder, llvm::SmallVector<llvm::Value *, 8> parts)
{
    while (parts.size() > 1) {
        llvm::SmallVector<llvm::Value *, 8> pairs;
        for (size_t index = 0; index < parts.size(); index += 2) {
            unsigned lanes = LaneCount(parts[index]->getType());
            llvm::SmallVector<int, 64> mask(lanes * 2);
            std::iota(mask.begin(), mask.end(), 0);
            if (index + 1 < parts.size()) {
                pairs.push_back(builder.CreateShuffleVector(parts[index], parts[index + 1], mask));
                continue;
            }
            std::fill(mask.begin() + lanes, mask.end(), -1);
            pairs.push_back(builder.CreateShuffleVector(parts[index], mask));
        }
        parts = std::move(pairs);
    }
    return parts[0];
}

// Replaces each planned gather's chain by one shufflevector of its pieces' concatenation, at the chain's end, unless
// apply is false, and returns how many pieces they load. A piece loads before the last load of the first gather that
// uses it, from lane 0's address plus its offset, at the alignment lane 0's load guarantees there; gathers that see one
// memory share it.
unsigned Rewrite(std::vector<Gather> &gathers, bool apply)
{
    struct Piece
    {
        unsigned epoch;
        Address address;
        llvm::Type *lane;
        uint64_t offset, size;
        llvm::LoadInst *load;
    };
    std::vector<Piece> pieces;
    for (Gather &gather : gathers) {
        llvm::Type *lane = gather.loads[0]->getType();
        auto *type = llvm::FixedVectorType::get(lane, static_cast<unsigned>(gather.piece / gather.element));
        llvm::SmallVector<llvm::Value *, 8> parts;
        for (int64_t start : gather.starts) {
            uint64_t offset = gather.address.offset + static_cast<uint64_t>(start);
            auto found = std::find_if(pieces.begin(), pieces.end(), [&](const Piece &piece) {
                return piece.epoch == gather.epoch && piece.lane == lane && piece.offset == offset && piece.size == gather.piece &&
                       piece.address.Same(gather.address);
            });
            if (found != pieces.end()) {
                parts.push_back(found->load);
                continue;
            }
            llvm::LoadInst *load = nullptr;
            if (apply) {
                llvm::IRBuilder<> builder(gather.last);
                llvm::Value *pointer = gather.loads[0]->getPointerOperand();
                if (start != 0) pointer = builder.CreateConstInBoundsGEP1_64(builder.getInt8Ty(), pointer, static_cast<uint64_t>(start));
                load = builder.CreateAlignedLoad(type, pointer, llvm::commonAlignment(gather.loads[0]->getAlign(), static_cast<uint64_t>(start)));
            }
            pieces.push_back({gather.epoch, gather.address, lane, offset, gather.piece, load});
            parts.push_back(load);
        }
        if (!apply) continue;
        auto per = static_cast<int64_t>(gather.piece / gather.element), element = static_cast<int64_t>(gather.element);
        llvm::SmallVector<int, 64> mask(gather.loads.size());
        for (size_t index = 0; index < mask.size(); ++index) {
            auto at = static_cast<int64_t>(index * gather.stride);
            for (size_t part = 0; part < gather.starts.size(); ++part)
                if (at >= gather.starts[part] && at + element <= gather.starts[part] + static_cast<int64_t>(gather.piece)) {
                    mask[index] = static_cast<int>(static_cast<int64_t>(part) * per + (at - gather.starts[part]) / element);
                    break;
                }
        }
        llvm::IRBuilder<> builder(gather.end);
        llvm::Value *gathered = builder.CreateShuffleVector(Join(builder, parts), mask);
        gather.end->replaceAllUsesWith(gathered);
        llvm::RecursivelyDeleteTriviallyDeadInstructions(gather.end);
    }
    return static_cast<unsigned>(pieces.size());
}

} // namespace

// Gathers of evenly strided elements of one range (SV-40), which the loop vectorizer's SSE2 cost model leaves as one
// scalar load and insertelement per lane (x264's frame_init_lowres_core reads 144 bytes two apart that way): each
// becomes wide loads and one shufflevector, which the typed lowering runs as uzp1/uzp2, packs or pshufb. Only bytes
// the block shows dereferenceable are read (Plan). With 256-bit fragments, whose IR comes from the AVX2 cost model that
// loads interleaved groups whole, gathers are left alone. Returns how many gathers the function has and stores their
// lanes and the wide loads they take; rewrites them unless apply is zero, so that the typed lowering's cost can count
// the inserts it saves first.
extern "C" unsigned llvmnet_strided_gathers(LLVMValueRef functionRef, unsigned fragmentBits, int apply, unsigned *lanes, unsigned *loads)
{
    auto *function = llvm::unwrap<llvm::Function>(functionRef);
    const llvm::DataLayout &layout = function->getParent()->getDataLayout();
    unsigned gathers = 0;
    *lanes = 0;
    *loads = 0;
    if (fragmentBits != 128) return 0;
    for (auto &block : *function) {
        std::vector<Gather> planned = Plan(block, layout);
        gathers += static_cast<unsigned>(planned.size());
        for (const Gather &gather : planned) *lanes += static_cast<unsigned>(gather.loads.size());
        *loads += Rewrite(planned, apply != 0);
    }
    return gathers;
}
