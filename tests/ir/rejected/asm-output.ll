define i32 @main() {
entry:
  %value = call i32 asm "", "=r"()
  ret i32 %value
}