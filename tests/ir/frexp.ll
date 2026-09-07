target triple = "x86_64-pc-linux-gnu"

declare {float, i32} @llvm.frexp.f32.i32(float)
declare {double, i32} @llvm.frexp.f64.i32(double)

define i1 @check_float(float %input, float %expected, i32 %exponent) {
entry:
  %result = call {float, i32} @llvm.frexp.f32.i32(float %input)
  %fraction = extractvalue {float, i32} %result, 0
  %power = extractvalue {float, i32} %result, 1
  %bits = bitcast float %fraction to i32
  %expectedBits = bitcast float %expected to i32
  %checkFraction = icmp eq i32 %bits, %expectedBits
  %checkPower = icmp eq i32 %power, %exponent
  %valid = and i1 %checkFraction, %checkPower
  ret i1 %valid
}

define i1 @check_double(double %input, double %expected, i32 %exponent) {
entry:
  %result = call {double, i32} @llvm.frexp.f64.i32(double %input)
  %fraction = extractvalue {double, i32} %result, 0
  %power = extractvalue {double, i32} %result, 1
  %bits = bitcast double %fraction to i64
  %expectedBits = bitcast double %expected to i64
  %checkFraction = icmp eq i64 %bits, %expectedBits
  %checkPower = icmp eq i32 %power, %exponent
  %valid = and i1 %checkFraction, %checkPower
  ret i1 %valid
}

define i32 @main() {
entry:
  %normal32 = call i1 @check_float(float 12.0, float 0.75, i32 4)
  %negative32 = call i1 @check_float(float -12.0, float -0.75, i32 4)
  %tiny32 = bitcast i32 1 to float
  %subnormal32 = call i1 @check_float(float %tiny32, float 0.5, i32 -148)
  %zero32 = call i1 @check_float(float -0.0, float -0.0, i32 0)
  %normal64 = call i1 @check_double(double 12.0, double 0.75, i32 4)
  %negative64 = call i1 @check_double(double -12.0, double -0.75, i32 4)
  %subnormal64 = call i1 @check_double(double 0x0000000000000001, double 0.5, i32 -1073)
  %zero64 = call i1 @check_double(double -0.0, double -0.0, i32 0)
  %infinite = call {double, i32} @llvm.frexp.f64.i32(double 0x7FF0000000000000)
  %infiniteFraction = extractvalue {double, i32} %infinite, 0
  %checkInfinity = fcmp oeq double %infiniteFraction, 0x7FF0000000000000
  %nan = call {double, i32} @llvm.frexp.f64.i32(double 0x7FF8000000000000)
  %nanFraction = extractvalue {double, i32} %nan, 0
  %checkNan = fcmp uno double %nanFraction, 0.0
  %statusNan = select i1 %checkNan, i32 0, i32 10
  %statusInfinity = select i1 %checkInfinity, i32 %statusNan, i32 9
  %statusZero64 = select i1 %zero64, i32 %statusInfinity, i32 8
  %statusSubnormal64 = select i1 %subnormal64, i32 %statusZero64, i32 7
  %statusNegative64 = select i1 %negative64, i32 %statusSubnormal64, i32 6
  %statusNormal64 = select i1 %normal64, i32 %statusNegative64, i32 5
  %statusZero32 = select i1 %zero32, i32 %statusNormal64, i32 4
  %statusSubnormal32 = select i1 %subnormal32, i32 %statusZero32, i32 3
  %statusNegative32 = select i1 %negative32, i32 %statusSubnormal32, i32 2
  %exit = select i1 %normal32, i32 %statusNegative32, i32 1
  ret i32 %exit
}