target triple = "x86_64-pc-linux-gnu"

@integers = global <2 x i64> <i64 9223372036854775807, i64 3>, align 16
@floats = global <4 x float> <float 16777216.0, float 1.0, float -16777216.0, float 2.0>, align 16
@packed = global <10 x i1> <i1 1, i1 0, i1 1, i1 0, i1 1, i1 0, i1 0, i1 0, i1 1, i1 0>
@index = global i64 9

declare i64 @llvm.vector.reduce.add.v2i64(<2 x i64>)
declare float @llvm.vector.reduce.fadd.v4f32(float, <4 x float>)
declare i1 @llvm.vector.reduce.and.v2i1(<2 x i1>)

define <10 x i1> @setbit(<10 x i1> %mask, i64 %index) {
entry:
  %bit = extractelement <10 x i1> %mask, i64 %index
  %flipped = xor i1 %bit, true
  %result = insertelement <10 x i1> %mask, i1 %flipped, i64 %index
  ret <10 x i1> %result
}

define i32 @main() {
entry:
  %integers = load <2 x i64>, ptr @integers, align 16
  %sum = call i64 @llvm.vector.reduce.add.v2i64(<2 x i64> %integers)
  %check0 = icmp eq i64 %sum, -9223372036854775806
  %floats = load <4 x float>, ptr @floats, align 16
  %floatSum = call float @llvm.vector.reduce.fadd.v4f32(float 0.0, <4 x float> %floats)
  %check1 = fcmp oeq float %floatSum, 2.0
  %mask = icmp sgt <2 x i64> %integers, zeroinitializer
  %check2 = call i1 @llvm.vector.reduce.and.v2i1(<2 x i1> %mask)
  %packed = load <10 x i1>, ptr @packed
  %index = load i64, ptr @index
  %flipped = call <10 x i1> @setbit(<10 x i1> %packed, i64 %index)
  %bits = bitcast <10 x i1> %flipped to i10
  %check3 = icmp eq i10 %bits, 789
  %pair = and i1 %check0, %check1
  %masks = and i1 %check2, %check3
  %valid = and i1 %pair, %masks
  %exit = select i1 %valid, i32 0, i32 1
  ret i32 %exit
}