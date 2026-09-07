target datalayout = "e-p:64:64-i64:64-n8:16:32:64-S128"
target triple = "x86_64-unknown-linux-gnu"

define i32 @pick(i64 %value) {
entry:
  switch i64 %value, label %fallback [
    i64 -5, label %small
    i64 42, label %small
    i64 4294967297, label %large
  ]
small:
  ret i32 3
large:
  ret i32 7
fallback:
  ret i32 9
}

define i32 @main() {
entry:
  %first = call i32 @pick(i64 -5)
  %second = call i32 @pick(i64 42)
  %third = call i32 @pick(i64 4294967297)
  %fourth = call i32 @pick(i64 0)
  %wrong_first = icmp ne i32 %first, 3
  %wrong_second = icmp ne i32 %second, 3
  %wrong_third = icmp ne i32 %third, 7
  %wrong_fourth = icmp ne i32 %fourth, 9
  %wrong_pair = or i1 %wrong_first, %wrong_second
  %wrong_triple = or i1 %wrong_pair, %wrong_third
  %wrong_any = or i1 %wrong_triple, %wrong_fourth
  %result = zext i1 %wrong_any to i32
  ret i32 %result
}