target triple = "x86_64-pc-linux-gnu"

@exponent = global i32 3
declare double @llvm.powi.f64.i32(double, i32)
declare float @llvm.powi.f32.i32(float, i32)

define i32 @main() {
entry:
  %exponent = load i32, ptr @exponent
  %positive = call double @llvm.powi.f64.i32(double -2.0, i32 %exponent)
  %check0 = fcmp oeq double %positive, -8.0
  %negativeExponent = sub i32 0, %exponent
  %negative = call float @llvm.powi.f32.i32(float 2.0, i32 %negativeExponent)
  %check1 = fcmp oeq float %negative, 0.125
  %zero = call double @llvm.powi.f64.i32(double 0.0, i32 0)
  %check2 = fcmp oeq double %zero, 1.0
  %extreme = call double @llvm.powi.f64.i32(double -1.0, i32 -2147483648)
  %check3 = fcmp oeq double %extreme, 1.0
  %negativeZero = call float @llvm.powi.f32.i32(float -0.0, i32 %exponent)
  %zeroBits = bitcast float %negativeZero to i32
  %check4 = icmp eq i32 %zeroBits, -2147483648
  %infinity = call double @llvm.powi.f64.i32(double -0.0, i32 %negativeExponent)
  %check5 = fcmp oeq double %infinity, 0xFFF0000000000000
  %nan = call double @llvm.powi.f64.i32(double 0x7FF8000000000000, i32 %exponent)
  %check6 = fcmp uno double %nan, 0.0
  %pair0 = and i1 %check0, %check1
  %pair1 = and i1 %check2, %check3
  %pair2 = and i1 %check4, %check5
  %part0 = and i1 %pair0, %pair1
  %part1 = and i1 %pair2, %check6
  %valid = and i1 %part0, %part1
  %exit = select i1 %valid, i32 0, i32 1
  ret i32 %exit
}