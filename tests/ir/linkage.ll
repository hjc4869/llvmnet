; Linkage forms that musl and libc++ rely on (BE-07): aliases to functions and globals,
; alias chains with offsets, weak definitions, and undefined extern_weak references that must be null.
target triple = "x86_64-pc-linux-gnu"

@table = global [4 x i32] [i32 10, i32 20, i32 30, i32 40]
@second = alias i32, getelementptr (i8, ptr @table, i64 4)
@second_again = weak alias i32, ptr @second
@counter = weak global i32 5
@missing_weak_data = extern_weak global i32

declare extern_weak i32 @missing_weak_function(i32)

define i32 @implementation(i32 %value) {
entry:
  %result = add i32 %value, 1
  ret i32 %result
}

@public_name = alias i32 (i32), ptr @implementation
@weak_name = weak alias i32 (i32), ptr @public_name

define weak i32 @overridable() {
entry:
  ret i32 7
}

define i32 @main() {
entry:
  %direct = call i32 @public_name(i32 41)
  %ok1 = icmp eq i32 %direct, 42
  %through_weak = call i32 @weak_name(i32 1)
  %ok2 = icmp eq i32 %through_weak, 2
  %same_address = icmp eq ptr @weak_name, @implementation
  %element = load i32, ptr @second
  %ok3 = icmp eq i32 %element, 20
  %chained = load i32, ptr @second_again
  %ok4 = icmp eq i32 %chained, 20
  %missing_function = icmp eq ptr @missing_weak_function, null
  %missing_data = icmp eq ptr @missing_weak_data, null
  br i1 %missing_function, label %skip, label %call_missing

call_missing:
  %never = call i32 @missing_weak_function(i32 0)
  ret i32 99

skip:
  %weak_value = load i32, ptr @counter
  %ok5 = icmp eq i32 %weak_value, 5
  %overridable_value = call i32 @overridable()
  %ok6 = icmp eq i32 %overridable_value, 7
  %a = and i1 %ok1, %ok2
  %b = and i1 %a, %same_address
  %c = and i1 %b, %ok3
  %d = and i1 %c, %ok4
  %e = and i1 %d, %missing_data
  %f = and i1 %e, %ok5
  %g = and i1 %f, %ok6
  %status = select i1 %g, i32 0, i32 1
  ret i32 %status
}
