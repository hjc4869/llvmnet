target triple = "x86_64-pc-linux-gnu"

@value = global double 1.0
declare double @llvm.tanh.f64(double)
declare double @llvm.sinh.f64(double)
declare double @llvm.cosh.f64(double)
declare float @llvm.tanh.f32(float)
declare float @llvm.sinh.f32(float)
declare float @llvm.cosh.f32(float)

define i32 @main() {
entry:
  %value = load double, ptr @value
  %tanh = call double @llvm.tanh.f64(double %value)
  %sinh = call double @llvm.sinh.f64(double %value)
  %cosh = call double @llvm.cosh.f64(double %value)
  %lower = fcmp ogt double %tanh, 0.7615941559
  %upper = fcmp olt double %tanh, 0.7615941560
  %sinhLower = fcmp ogt double %sinh, 1.1752011936
  %sinhUpper = fcmp olt double %sinh, 1.1752011937
  %coshLower = fcmp ogt double %cosh, 1.5430806348
  %coshUpper = fcmp olt double %cosh, 1.5430806349
  %negativeZero = call float @llvm.tanh.f32(float -0.0)
  %zeroBits = bitcast float %negativeZero to i32
  %zeroValid = icmp eq i32 %zeroBits, -2147483648
  %sinhZero = call float @llvm.sinh.f32(float -0.0)
  %sinhBits = bitcast float %sinhZero to i32
  %sinhZeroValid = icmp eq i32 %sinhBits, -2147483648
  %coshZero = call float @llvm.cosh.f32(float 0.0)
  %coshZeroValid = fcmp oeq float %coshZero, 1.0
  %infinity = call double @llvm.tanh.f64(double 0x7FF0000000000000)
  %infinityValid = fcmp oeq double %infinity, 1.0
  %nan = call double @llvm.tanh.f64(double 0x7FF8000000000000)
  %nanValid = fcmp uno double %nan, 0.0
  %valid0 = and i1 %lower, %upper
  %valid1 = and i1 %sinhLower, %sinhUpper
  %valid2 = and i1 %coshLower, %coshUpper
  %valid3 = and i1 %zeroValid, %sinhZeroValid
  %valid4 = and i1 %coshZeroValid, %infinityValid
  %pair0 = and i1 %valid0, %valid1
  %pair1 = and i1 %valid2, %valid3
  %pair2 = and i1 %valid4, %nanValid
  %finite = and i1 %pair0, %pair1
  %valid = and i1 %finite, %pair2
  %exit = select i1 %valid, i32 0, i32 1
  ret i32 %exit
}