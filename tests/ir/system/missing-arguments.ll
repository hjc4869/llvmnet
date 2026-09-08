target triple = "x86_64-pc-linux-gnu"

@number = global i64 0

define void @requires_length(ptr %memory, i64 %length) {
entry:
  store i64 %length, ptr %memory
  ret void
}

define i32 @main(i32 %count, ptr %arguments) {
entry:
  %invoke = icmp sgt i32 %count, 1
  br i1 %invoke, label %invalid, label %done
invalid:
  call void (ptr) @requires_length(ptr @number)
  br label %done
done:
  ret i32 0
}