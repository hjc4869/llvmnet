; A musttail call with a 32-byte vector argument, which Windows x64 and ARM64 pass by reference to a copy in the caller's frame.
define <8 x float> @count(i64 %n, <8 x float> %a) {
entry:
  %done = icmp eq i64 %n, 0
  br i1 %done, label %exit, label %next
next:
  %less = sub i64 %n, 1
  %result = musttail call <8 x float> @count(i64 %less, <8 x float> %a)
  ret <8 x float> %result
exit:
  ret <8 x float> %a
}

define i32 @main() {
entry:
  %result = call <8 x float> @count(i64 10, <8 x float> zeroinitializer)
  %lane = extractelement <8 x float> %result, i32 0
  %value = fptosi float %lane to i32
  ret i32 %value
}
