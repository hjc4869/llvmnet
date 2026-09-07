target datalayout = "e-p:64:64-i64:64-i128:128-n8:16:32:64-S128"
target triple = "x86_64-unknown-linux-gnu"
declare i32 @llvm.scmp.i32.i128(i128, i128)
define i32 @classify(i128 %value) {
entry:
  switch i128 %value, label %fallback [i128 1267650600228229401496703205376, label %large]
large:
  ret i32 7
fallback:
  ret i32 9
}
define i32 @main() {
entry:
  %extended = sext i65 -1 to i128
  %bad_sign = icmp ne i128 %extended, -1
  %classified = call i32 @classify(i128 1267650600228229401496703205376)
  %bad_switch = icmp ne i32 %classified, 7
  %compared = call i32 @llvm.scmp.i32.i128(i128 -7, i128 11)
  %bad_compare = icmp ne i32 %compared, -1
  %pair = or i1 %bad_sign, %bad_switch
  %failed = or i1 %pair, %bad_compare
  %result = zext i1 %failed to i32
  ret i32 %result
}