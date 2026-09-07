target datalayout = "e-p:64:64-i64:64-n8:16:32:64-S128"
target triple = "x86_64-unknown-linux-gnu"

@table = internal global [2 x i32] [i32 4, i32 1234]
declare ptr @llvm.load.relative.i64(ptr, i64)
declare i32 @llvm.sadd.sat.i32(i32, i32)
declare i64 @llvm.usub.sat.i64(i64, i64)
declare i32 @llvm.uadd.sat.i32(i32, i32)
declare i64 @llvm.llrint.i64.f64(double)
declare i1 @llvm.is.fpclass.f64(double, i32)
declare float @llvm.ldexp.f32.i32(float, i32)
declare i8 @llvm.cttz.i8(i8, i1)
declare i16 @llvm.ctlz.i16(i16, i1)
declare i2 @llvm.bitreverse.i2(i2)
declare i16 @llvm.fshr.i16(i16, i16, i16)
declare i32 @llvm.fptosi.sat.i32.f32(float)

define i32 @main() {
entry:
  %address = call ptr @llvm.load.relative.i64(ptr @table, i64 0)
  %loaded = load i32, ptr %address
  %first = icmp ne i32 %loaded, 1234
  %signed = call i32 @llvm.sadd.sat.i32(i32 2147483647, i32 1)
  %second = icmp ne i32 %signed, 2147483647
  %subtracted = call i64 @llvm.usub.sat.i64(i64 4, i64 9)
  %third = icmp ne i64 %subtracted, 0
  %added = call i32 @llvm.uadd.sat.i32(i32 -1, i32 42)
  %fourth = icmp ne i32 %added, -1
  %rounded = call i64 @llvm.llrint.i64.f64(double 2.5)
  %fifth = icmp ne i64 %rounded, 2
  %negative_zero = call i1 @llvm.is.fpclass.f64(double -0.0, i32 32)
  %sixth = xor i1 %negative_zero, true
  %scaled = call float @llvm.ldexp.f32.i32(float 1.5, i32 3)
  %seventh = fcmp une float %scaled, 12.0
  %trailing = call i8 @llvm.cttz.i8(i8 0, i1 false)
  %eighth = icmp ne i8 %trailing, 8
  %leading = call i16 @llvm.ctlz.i16(i16 1, i1 false)
  %ninth = icmp ne i16 %leading, 15
  %reversed = call i2 @llvm.bitreverse.i2(i2 1)
  %tenth = icmp ne i2 %reversed, 2
  %rotated = call i16 @llvm.fshr.i16(i16 4660, i16 4660, i16 20)
  %eleventh = icmp ne i16 %rotated, 16675
  %clamped = call i32 @llvm.fptosi.sat.i32.f32(float 1.000000e+10)
  %twelfth = icmp ne i32 %clamped, 2147483647
  %first_two = or i1 %first, %second
  %next_two = or i1 %third, %fourth
  %last_two = or i1 %fifth, %sixth
  %first_four = or i1 %first_two, %next_two
  %failed = or i1 %first_four, %last_two
  %any_failure = or i1 %failed, %seventh
  %bit_failure = or i1 %eighth, %ninth
  %all_failure = or i1 %any_failure, %bit_failure
  %total_failure = or i1 %all_failure, %tenth
  %complete_failure = or i1 %total_failure, %eleventh
  %final_failure = or i1 %complete_failure, %twelfth
  %result = zext i1 %final_failure to i32
  ret i32 %result
}