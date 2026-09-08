declare ptr @llvm.stacksave.p0()
declare void @llvm.stackrestore.p0(ptr)

define i32 @check(i64 %size) {
entry:
  %preserved = alloca i32, align 32
  store volatile i32 41, ptr %preserved, align 32
  %outer = call ptr @llvm.stacksave.p0()
  br label %loop

loop:
  %iteration = phi i32 [ 0, %entry ], [ %next, %continue ]
  %buffer = alloca i8, i64 %size, align 64
  %address = ptrtoint ptr %buffer to i64
  %alignment = and i64 %address, 63
  %aligned = icmp eq i64 %alignment, 0
  %last_index = sub i64 %size, 1
  %last = getelementptr i8, ptr %buffer, i64 %last_index
  store volatile i8 23, ptr %buffer, align 64
  store volatile i8 59, ptr %last, align 1
  %inner = call ptr @llvm.stacksave.p0()
  %temporary = alloca i8, i64 %size, align 32
  store volatile i8 99, ptr %temporary, align 32
  call void @llvm.stackrestore.p0(ptr %inner)
  %first_value = load volatile i8, ptr %buffer, align 64
  %last_value = load volatile i8, ptr %last, align 1
  %first_ok = icmp eq i8 %first_value, 23
  %last_ok = icmp eq i8 %last_value, 59
  %values_ok = and i1 %first_ok, %last_ok
  %buffer_ok = and i1 %values_ok, %aligned
  call void @llvm.stackrestore.p0(ptr %outer)
  %preserved_value = load volatile i32, ptr %preserved, align 32
  %preserved_ok = icmp eq i32 %preserved_value, 41
  %ok = and i1 %buffer_ok, %preserved_ok
  br i1 %ok, label %continue, label %failure

continue:
  %next = add i32 %iteration, 1
  %done = icmp eq i32 %next, 20000
  br i1 %done, label %success, label %loop

success:
  ret i32 0

failure:
  ret i32 1
}

define i32 @main() {
entry:
  %result = call i32 @check(i64 4096)
  ret i32 %result
}