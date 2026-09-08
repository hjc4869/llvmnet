define i32 @main() {
entry:
  call void asm sideeffect "# comment\0Anop", ""()
  ret i32 0
}