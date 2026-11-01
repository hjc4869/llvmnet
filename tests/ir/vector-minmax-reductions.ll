; BE-43: floating-point minimum and maximum reductions without nnan, which expand-reductions leaves for the backend.
; fmax and fmin follow maxnum and minnum (a NaN lane is ignored), fmaximum and fminimum maximum and minimum (a NaN
; lane gives NaN). Exits with 0 when every result is right.
declare float @llvm.vector.reduce.fmax.v4f32(<4 x float>)
declare float @llvm.vector.reduce.fmin.v4f32(<4 x float>)
declare double @llvm.vector.reduce.fmaximum.v2f64(<2 x double>)
declare double @llvm.vector.reduce.fminimum.v2f64(<2 x double>)

define i32 @main() {
entry:
  %max = call float @llvm.vector.reduce.fmax.v4f32(<4 x float> <float 1.0, float 0x7FF8000000000000, float 7.5, float -2.0>)
  %min = call float @llvm.vector.reduce.fmin.v4f32(<4 x float> <float 1.0, float 0x7FF8000000000000, float 7.5, float -2.0>)
  %maximum = call double @llvm.vector.reduce.fmaximum.v2f64(<2 x double> <double 3.0, double 4.0>)
  %propagated = call double @llvm.vector.reduce.fminimum.v2f64(<2 x double> <double 3.0, double 0x7FF8000000000000>)
  %minimum = call double @llvm.vector.reduce.fminimum.v2f64(<2 x double> <double 3.0, double -4.0>)
  %max_ok = fcmp oeq float %max, 7.5
  %min_ok = fcmp oeq float %min, -2.0
  %maximum_ok = fcmp oeq double %maximum, 4.0
  %propagated_ok = fcmp uno double %propagated, 0.0
  %minimum_ok = fcmp oeq double %minimum, -4.0
  %single_ok = and i1 %max_ok, %min_ok
  %double_ok = and i1 %maximum_ok, %minimum_ok
  %all_double = and i1 %double_ok, %propagated_ok
  %ok = and i1 %single_ok, %all_double
  %status = select i1 %ok, i32 0, i32 1
  ret i32 %status
}
