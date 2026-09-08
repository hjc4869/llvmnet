declare { double, double } @llvm.modf.f64(double)
declare { float, float } @llvm.modf.f32(float)

define i32 @check64(double %value, double %expected_fraction, double %expected_integral) {
entry:
  %parts = call { double, double } @llvm.modf.f64(double %value)
  %fraction = extractvalue { double, double } %parts, 0
  %integral = extractvalue { double, double } %parts, 1
  %fraction_bits = bitcast double %fraction to i64
  %integral_bits = bitcast double %integral to i64
  %expected_fraction_bits = bitcast double %expected_fraction to i64
  %expected_integral_bits = bitcast double %expected_integral to i64
  %fraction_bad = icmp ne i64 %fraction_bits, %expected_fraction_bits
  %integral_bad = icmp ne i64 %integral_bits, %expected_integral_bits
  %bad = or i1 %fraction_bad, %integral_bad
  %result = zext i1 %bad to i32
  ret i32 %result
}

define i32 @check32(float %value, float %expected_fraction, float %expected_integral) {
entry:
  %parts = call { float, float } @llvm.modf.f32(float %value)
  %fraction = extractvalue { float, float } %parts, 0
  %integral = extractvalue { float, float } %parts, 1
  %fraction_bits = bitcast float %fraction to i32
  %integral_bits = bitcast float %integral to i32
  %expected_fraction_bits = bitcast float %expected_fraction to i32
  %expected_integral_bits = bitcast float %expected_integral to i32
  %fraction_bad = icmp ne i32 %fraction_bits, %expected_fraction_bits
  %integral_bad = icmp ne i32 %integral_bits, %expected_integral_bits
  %bad = or i1 %fraction_bad, %integral_bad
  %result = zext i1 %bad to i32
  ret i32 %result
}

define i32 @main() {
entry:
  %positive64 = call i32 @check64(double 3.750000e+00, double 7.500000e-01, double 3.000000e+00)
  %negative64 = call i32 @check64(double -3.750000e+00, double -7.500000e-01, double -3.000000e+00)
  %zero64 = call i32 @check64(double 0.000000e+00, double 0.000000e+00, double 0.000000e+00)
  %negative_zero64 = call i32 @check64(double -0.000000e+00, double -0.000000e+00, double -0.000000e+00)
  %integer64 = call i32 @check64(double -2.000000e+00, double -0.000000e+00, double -2.000000e+00)
  %small64 = call i32 @check64(double -2.500000e-01, double -2.500000e-01, double -0.000000e+00)
  %large64 = call i32 @check64(double 0x4340000000000000, double 0.000000e+00, double 0x4340000000000000)
  %subnormal64 = call i32 @check64(double 0x0000000000000001, double 0x0000000000000001, double 0.000000e+00)
  %negative_subnormal64 = call i32 @check64(double 0x8000000000000001, double 0x8000000000000001, double -0.000000e+00)
  %infinity64 = call i32 @check64(double 0x7FF0000000000000, double 0.000000e+00, double 0x7FF0000000000000)
  %negative_infinity64 = call i32 @check64(double 0xFFF0000000000000, double -0.000000e+00, double 0xFFF0000000000000)
  %positive32 = call i32 @check32(float 3.750000e+00, float 7.500000e-01, float 3.000000e+00)
  %negative32 = call i32 @check32(float -3.750000e+00, float -7.500000e-01, float -3.000000e+00)
  %zero32 = call i32 @check32(float 0.000000e+00, float 0.000000e+00, float 0.000000e+00)
  %negative_zero32 = call i32 @check32(float -0.000000e+00, float -0.000000e+00, float -0.000000e+00)
  %integer32 = call i32 @check32(float -2.000000e+00, float -0.000000e+00, float -2.000000e+00)
  %small32 = call i32 @check32(float -2.500000e-01, float -2.500000e-01, float -0.000000e+00)
  %large32 = call i32 @check32(float 0x4170000000000000, float 0.000000e+00, float 0x4170000000000000)
  %subnormal32 = call i32 @check32(float 0x36A0000000000000, float 0x36A0000000000000, float 0.000000e+00)
  %negative_subnormal32 = call i32 @check32(float 0xB6A0000000000000, float 0xB6A0000000000000, float -0.000000e+00)
  %infinity32 = call i32 @check32(float 0x7FF0000000000000, float 0.000000e+00, float 0x7FF0000000000000)
  %negative_infinity32 = call i32 @check32(float 0xFFF0000000000000, float -0.000000e+00, float 0xFFF0000000000000)
  %nan64 = call { double, double } @llvm.modf.f64(double 0x7FF8000000000000)
  %nan_fraction64 = extractvalue { double, double } %nan64, 0
  %nan_integral64 = extractvalue { double, double } %nan64, 1
  %nan_fraction_bad64 = fcmp ord double %nan_fraction64, %nan_fraction64
  %nan_integral_bad64 = fcmp ord double %nan_integral64, %nan_integral64
  %nan_bad64 = or i1 %nan_fraction_bad64, %nan_integral_bad64
  %nan_result64 = zext i1 %nan_bad64 to i32
  %nan32 = call { float, float } @llvm.modf.f32(float 0x7FF8000000000000)
  %nan_fraction32 = extractvalue { float, float } %nan32, 0
  %nan_integral32 = extractvalue { float, float } %nan32, 1
  %nan_fraction_bad32 = fcmp ord float %nan_fraction32, %nan_fraction32
  %nan_integral_bad32 = fcmp ord float %nan_integral32, %nan_integral32
  %nan_bad32 = or i1 %nan_fraction_bad32, %nan_integral_bad32
  %nan_result32 = zext i1 %nan_bad32 to i32
  %failure01 = or i32 %positive64, %negative64
  %failure02 = or i32 %failure01, %zero64
  %failure03 = or i32 %failure02, %negative_zero64
  %failure04 = or i32 %failure03, %integer64
  %failure05 = or i32 %failure04, %small64
  %failure06 = or i32 %failure05, %large64
  %failure07 = or i32 %failure06, %subnormal64
  %failure08 = or i32 %failure07, %negative_subnormal64
  %failure09 = or i32 %failure08, %infinity64
  %failure10 = or i32 %failure09, %negative_infinity64
  %failure11 = or i32 %failure10, %positive32
  %failure12 = or i32 %failure11, %negative32
  %failure13 = or i32 %failure12, %zero32
  %failure14 = or i32 %failure13, %negative_zero32
  %failure15 = or i32 %failure14, %integer32
  %failure16 = or i32 %failure15, %small32
  %failure17 = or i32 %failure16, %large32
  %failure18 = or i32 %failure17, %subnormal32
  %failure19 = or i32 %failure18, %negative_subnormal32
  %failure20 = or i32 %failure19, %infinity32
  %failure21 = or i32 %failure20, %negative_infinity32
  %failure22 = or i32 %failure21, %nan_result64
  %failure23 = or i32 %failure22, %nan_result32
  ret i32 %failure23
}