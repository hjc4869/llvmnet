target triple = "x86_64-pc-linux-gnu"

define i64 @stack_argument(i64 %first, i64 %second, i64 %third, i64 %fourth, i64 %fifth, i64 %sixth, i64 %last) noinline {
entry:
  ret i64 %last
}

define i32 @main() {
entry:
  %result = call i64 (i64, i64, i64, i64, i64, i64, i32) @stack_argument(i64 0, i64 0, i64 0, i64 0, i64 0, i64 0, i32 -1)
  %truncated = trunc i64 %result to i32
  ret i32 %truncated
}