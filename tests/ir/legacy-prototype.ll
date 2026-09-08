target triple = "x86_64-pc-linux-gnu"

@counter = global i32 0

define void @initialize() {
entry:
  store i32 7, ptr @counter
  ret void
}

define void @update(ptr %destination, i32 %value) {
entry:
  store i32 %value, ptr %destination
  ret void
}

define i32 @identity(i32 %value) {
entry:
  ret i32 %value
}

define i32 @main() {
entry:
  call void (...) @initialize()
  %ignored = call i32 () @initialize()
  %initial = load i32, ptr @counter
  %initialValid = icmp eq i32 %initial, 7
  call void (ptr, ...) @update(ptr @counter, i32 13)
  %updated = load i32, ptr @counter
  %result = call i32 (...) @identity(i32 %updated)
  call void (i32) @identity(i32 99)
  %extra = call i32 (i32, i64) @identity(i32 13, i64 1234)
  %extraValid = icmp eq i32 %extra, 13
  %resultValid = icmp eq i32 %result, 13
  %firstValid = and i1 %initialValid, %resultValid
  %valid = and i1 %firstValid, %extraValid
  %exit = select i1 %valid, i32 0, i32 1
  ret i32 %exit
}