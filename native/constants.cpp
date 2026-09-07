#include <llvm-c/Core.h>
#include <llvm/IR/Constants.h>
#include <llvm/Support/CBindingWrapping.h>
#include <algorithm>
#include <cstdint>

extern "C" unsigned llvmnet_constant_bits(LLVMValueRef value, uint8_t *destination, unsigned capacity)
{
    llvm::Value *unwrapped = llvm::unwrap(value);
    llvm::APInt bits;
    if (auto *integer = llvm::dyn_cast<llvm::ConstantInt>(unwrapped))
        bits = integer->getValue();
    else if (auto *real = llvm::dyn_cast<llvm::ConstantFP>(unwrapped))
        bits = real->getValueAPF().bitcastToAPInt();
    else
        return 0;
    unsigned size = (bits.getBitWidth() + 7) / 8;
    if (capacity < size)
        return size;
    for (unsigned index = 0; index < size; ++index)
        destination[index] = static_cast<uint8_t>(bits.extractBitsAsZExtValue(std::min(8u, bits.getBitWidth() - index * 8), index * 8));
    return size;
}