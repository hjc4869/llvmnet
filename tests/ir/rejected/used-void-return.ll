define void @initialize(i32 %value) {
entry:
  ret void
}

define i32 @main() {
entry:
  %value = call i32 (i32) @initialize(i32 1)
  ret i32 %value
}
