declare ptr @llvm.invariant.start.p0(i64 immarg, ptr nocapture)
declare void @llvm.invariant.end.p0(ptr, i64 immarg, ptr nocapture)
declare ptr @llvm.launder.invariant.group.p0(ptr)
declare ptr @llvm.strip.invariant.group.p0(ptr)

define i32 @main() {
entry:
  %memory = alloca i64, align 8
  store i64 41, ptr %memory, align 8
  %token = call ptr @llvm.invariant.start.p0(i64 8, ptr %memory)
  %laundered = call ptr @llvm.launder.invariant.group.p0(ptr %memory)
  %stripped = call ptr @llvm.strip.invariant.group.p0(ptr %laundered)
  %before = load i64, ptr %stripped, align 8
  call void @llvm.invariant.end.p0(ptr %token, i64 8, ptr %memory)
  store i64 42, ptr %memory, align 8
  %after = load i64, ptr %memory, align 8
  %same = icmp eq ptr %stripped, %memory
  %before_ok = icmp eq i64 %before, 41
  %after_ok = icmp eq i64 %after, 42
  %values_ok = and i1 %before_ok, %after_ok
  %ok = and i1 %values_ok, %same
  %result = select i1 %ok, i32 0, i32 1
  ret i32 %result
}