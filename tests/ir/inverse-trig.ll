declare double @llvm.acos.f64(double)
declare float @llvm.asin.f32(float)
declare double @llvm.tan.f64(double)
declare double @llvm.fabs.f64(double)
declare float @llvm.fabs.f32(float)

define i32 @main() {
entry:
  %angle = call double @llvm.acos.f64(double 0.000000e+00)
  %difference = fsub double %angle, 0x3FF921FB54442D18
  %error = call double @llvm.fabs.f64(double %difference)
  %angle_ok = fcmp olt double %error, 1.000000e-14
  %single = call float @llvm.asin.f32(float 1.000000e+00)
  %single_difference = fsub float %single, 0x3FF921FB60000000
  %single_error = call float @llvm.fabs.f32(float %single_difference)
  %single_ok = fcmp olt float %single_error, 0x3EB0C6F7A0000000
  %negative_zero = call float @llvm.asin.f32(float -0.000000e+00)
  %zero_bits = bitcast float %negative_zero to i32
  %zero_ok = icmp eq i32 %zero_bits, -2147483648
  %invalid = call double @llvm.acos.f64(double 2.000000e+00)
  %nan_ok = fcmp uno double %invalid, %invalid
  %tangent = call double @llvm.tan.f64(double 0.000000e+00)
  %tan_ok = fcmp oeq double %tangent, 0.000000e+00
  %pair = and i1 %angle_ok, %single_ok
  %pair2 = and i1 %zero_ok, %nan_ok
  %combined = and i1 %pair, %pair2
  %ok = and i1 %combined, %tan_ok
  %result = select i1 %ok, i32 0, i32 1
  ret i32 %result
}