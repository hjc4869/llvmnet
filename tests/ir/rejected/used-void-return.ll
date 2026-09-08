define void @initialize() {
entry:
  ret void
}

define i32 @main() {
entry:
  %value = call i32 () @initialize()
  ret i32 %value
}