declare void @llvm.prefetch.p0(ptr, i32 immarg, i32 immarg, i32 immarg)

define i32 @main() {
entry:
  %memory = alloca i32, align 4
  store i32 42, ptr %memory, align 4
  call void @llvm.prefetch.p0(ptr null, i32 0, i32 0, i32 1)
  call void @llvm.prefetch.p0(ptr %memory, i32 0, i32 3, i32 1)
  call void @llvm.prefetch.p0(ptr %memory, i32 1, i32 2, i32 1)
  %value = load i32, ptr %memory, align 4
  %result = sub i32 %value, 42
  ret i32 %result
}