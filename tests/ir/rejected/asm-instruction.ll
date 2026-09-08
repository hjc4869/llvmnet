define i32 @main() {
entry:
  call void asm sideeffect "nop", ""()
  ret i32 0
}