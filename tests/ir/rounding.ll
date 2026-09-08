declare i32 @llvm.get.rounding()

define i32 @main() {
entry:
  %rounding = call i32 @llvm.get.rounding()
  %result = sub i32 %rounding, 1
  ret i32 %result
}