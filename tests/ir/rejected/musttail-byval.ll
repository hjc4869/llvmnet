; A musttail call that forwards a byval argument: the callee's copy would come from the caller's released frame.
%struct.block = type { [6 x i64] }

define i64 @forward(ptr byval(%struct.block) align 8 %block, i64 %n) {
entry:
  %done = icmp eq i64 %n, 0
  br i1 %done, label %exit, label %next
next:
  %less = sub i64 %n, 1
  %result = musttail call i64 @forward(ptr byval(%struct.block) align 8 %block, i64 %less)
  ret i64 %result
exit:
  %first = load i64, ptr %block
  ret i64 %first
}

define i32 @main() {
entry:
  %block = alloca %struct.block, align 8
  store i64 0, ptr %block
  %result = call i64 @forward(ptr byval(%struct.block) align 8 %block, i64 10)
  %truncated = trunc i64 %result to i32
  ret i32 %truncated
}
