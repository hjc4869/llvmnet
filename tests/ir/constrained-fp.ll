; Constrained floating-point intrinsics (from #pragma STDC FENV_ACCESS or -frounding-math) with to-nearest or
; dynamic rounding compute the same values as the plain operations in llvmnet's round-to-nearest environment.
target triple = "x86_64-pc-linux-gnu"
declare double @llvm.experimental.constrained.fadd.f64(double, double, metadata, metadata)
declare double @llvm.experimental.constrained.fmul.f64(double, double, metadata, metadata)
declare double @llvm.experimental.constrained.fdiv.f64(double, double, metadata, metadata)
declare double @llvm.experimental.constrained.sqrt.f64(double, metadata, metadata)
declare double @llvm.experimental.constrained.fma.f64(double, double, double, metadata, metadata)
declare i32 @llvm.experimental.constrained.fptosi.i32.f64(double, metadata)
declare double @llvm.experimental.constrained.sitofp.f64.i64(i64, metadata, metadata)
declare float @llvm.experimental.constrained.fptrunc.f32.f64(double, metadata, metadata)
declare i1 @llvm.experimental.constrained.fcmp.f64(double, double, metadata, metadata)
declare i1 @llvm.experimental.constrained.fcmps.f64(double, double, metadata, metadata)

define i32 @main() strictfp {
entry:
  %sum = call double @llvm.experimental.constrained.fadd.f64(double 0.1, double 0.2, metadata !"round.tonearest", metadata !"fpexcept.ignore") strictfp
  %product = call double @llvm.experimental.constrained.fmul.f64(double %sum, double 3.0, metadata !"round.dynamic", metadata !"fpexcept.strict") strictfp
  %quotient = call double @llvm.experimental.constrained.fdiv.f64(double %product, double 7.0, metadata !"round.tonearest", metadata !"fpexcept.maytrap") strictfp
  %root = call double @llvm.experimental.constrained.sqrt.f64(double %quotient, metadata !"round.tonearest", metadata !"fpexcept.ignore") strictfp
  %fused = call double @llvm.experimental.constrained.fma.f64(double %root, double %root, double -0.12857142857142856, metadata !"round.tonearest", metadata !"fpexcept.ignore") strictfp
  %scaled = fmul double %fused, 1.0e17
  %integer = call i32 @llvm.experimental.constrained.fptosi.i32.f64(double %scaled, metadata !"fpexcept.ignore") strictfp
  %back = call double @llvm.experimental.constrained.sitofp.f64.i64(i64 9007199254740993, metadata !"round.tonearest", metadata !"fpexcept.ignore") strictfp
  %narrow = call float @llvm.experimental.constrained.fptrunc.f32.f64(double 0.1, metadata !"round.tonearest", metadata !"fpexcept.ignore") strictfp
  %less = call i1 @llvm.experimental.constrained.fcmp.f64(double %sum, double 0.30000000000000004, metadata !"oeq", metadata !"fpexcept.ignore") strictfp
  %signaling = call i1 @llvm.experimental.constrained.fcmps.f64(double %quotient, double 0.13, metadata !"olt", metadata !"fpexcept.ignore") strictfp
  %expected_back = fcmp oeq double %back, 9007199254740992.0
  %narrow_bits = bitcast float %narrow to i32
  %ok_narrow = icmp eq i32 %narrow_bits, 1036831949
  %ok_integer = icmp eq i32 %integer, 2
  %a = and i1 %less, %signaling
  %b = and i1 %a, %expected_back
  %c = and i1 %b, %ok_narrow
  %d = and i1 %c, %ok_integer
  %status = select i1 %d, i32 0, i32 1
  ret i32 %status
}
