; A musttail call that forwards the caller's variadic arguments.
define i32 @sum(i32 %count, ...) {
entry:
  ret i32 0
}

define i32 @forward(i32 %count, ...) {
entry:
  %result = musttail call i32 (i32, ...) @sum(i32 %count, ...)
  ret i32 %result
}

define i32 @main() {
entry:
  %result = call i32 (i32, ...) @forward(i32 2, i32 1, i32 2)
  ret i32 %result
}
