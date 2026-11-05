#include <llvm-c/Core.h>
#include <llvm/ADT/SmallPtrSet.h>
#include <llvm/Analysis/ValueTracking.h>
#include <llvm/CodeGen/ExpandReductions.h>
#include <llvm/IR/Function.h>
#include <llvm/IR/IRBuilder.h>
#include <llvm/IR/Instructions.h>
#include <llvm/IR/Intrinsics.h>
#include <llvm/IR/IntrinsicsX86.h>
#include <llvm/IR/Module.h>
#include <algorithm>
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
// to its lanes; a reduction of the leaf itself takes all of them) and the others zero.
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
        reduction->replaceAllUsesWith(builder.CreateAddReduce(result));
        reduction->eraseFromParent();
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
