target datalayout = "e-p:64:64-i64:64-n8:16:32:64-S128"
target triple = "x86_64-unknown-linux-gnu"
declare float @llvm.fmuladd.f32(float, float, float)
declare float @llvm.fma.f32(float, float, float)
define i32 @main() {
entry:
  %unfused = call float @llvm.fmuladd.f32(float 1.0001220703125, float 0.9998779296875, float -1.0)
  %fused = call float @llvm.fma.f32(float 1.0001220703125, float 0.9998779296875, float -1.0)
  %wrong_unfused = fcmp une float %unfused, 0.0
  %wrong_fused = fcmp une float %fused, -1.490116119384765625e-8
  %failed = or i1 %wrong_unfused, %wrong_fused
  %result = zext i1 %failed to i32
  ret i32 %result
}