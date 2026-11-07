; Masks of more than 64 lanes (SV-32): the typed lowering holds a mask as bits in one ulong, so a function with an
; <80 x i1> (SPEC CPU2017 548.exchange2_r's main program, vectorized by flang) is scalarized, and the vector the
; scalarizer rebuilds for the bitcast to i80 must keep all 80 lanes. main returns 0 when the count, the top lanes and
; lane 71 match.
target triple = "x86_64-pc-linux-gnu"
@values = global [80 x i32] zeroinitializer
declare i80 @llvm.ctpop.i80(i80)
define void @fill() {
entry:
  br label %loop
loop:
  %i = phi i32 [ 0, %entry ], [ %next, %loop ]
  %m = urem i32 %i, 3
  %v = sub i32 %m, 1
  %slot = getelementptr [80 x i32], ptr @values, i32 0, i32 %i
  store i32 %v, ptr %slot
  %next = add i32 %i, 1
  %done = icmp eq i32 %next, 80
  br i1 %done, label %exit, label %loop
exit:
  ret void
}
define i32 @wide() noinline {
  %v = load <80 x i32>, ptr @values
  %positive = icmp sgt <80 x i32> %v, zeroinitializer
  %bits = bitcast <80 x i1> %positive to i80
  %count = call i80 @llvm.ctpop.i80(i80 %bits)
  %c0 = icmp ne i80 %count, 26
  %top = lshr i80 %bits, 77
  %c1 = icmp ne i80 %top, 1
  %lane = extractelement <80 x i1> %positive, i32 71
  %c2 = icmp ne i1 %lane, true
  %c01 = or i1 %c0, %c1
  %f = or i1 %c01, %c2
  %r = zext i1 %f to i32
  ret i32 %r
}
define i32 @main() {
  call void @fill()
  %r = call i32 @wide()
  ret i32 %r
}
