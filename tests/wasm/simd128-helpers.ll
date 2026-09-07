target triple = "x86_64-pc-linux-gnu"

@left = global <4 x i32> <i32 2147483647, i32 -1, i32 10, i32 -10>, align 16
@right = global <4 x i32> <i32 1, i32 2, i32 -20, i32 30>, align 16
@output = global <4 x i32> zeroinitializer, align 16

declare <16 x i8> @__llvmnet_simd128_load(ptr)
declare <16 x i8> @__llvmnet_simd128_add_i32(<16 x i8>, <16 x i8>)
declare void @__llvmnet_simd128_store(ptr, <16 x i8>)

define i32 @main() {
entry:
  %first = call <16 x i8> @__llvmnet_simd128_load(ptr @left)
  %second = call <16 x i8> @__llvmnet_simd128_load(ptr @right)
  %sum = call <16 x i8> @__llvmnet_simd128_add_i32(<16 x i8> %first, <16 x i8> %second)
  call void @__llvmnet_simd128_store(ptr @output, <16 x i8> %sum)
  %actual = load <4 x i32>, ptr @output, align 16
  %equal = icmp eq <4 x i32> %actual, <i32 -2147483648, i32 1, i32 -10, i32 20>
  %all = call i1 @llvm.vector.reduce.and.v4i1(<4 x i1> %equal)
  %failed = xor i1 %all, true
  %result = zext i1 %failed to i32
  ret i32 %result
}

declare i1 @llvm.vector.reduce.and.v4i1(<4 x i1>)

!llvm.module.flags = !{!0}
!0 = !{i32 1, !"llvmnet.abi", !"LLVMNET_TEST_ABI"}