target datalayout = "e-p:64:64-i64:64-n8:16:32:64-S128"
target triple = "x86_64-unknown-linux-gnu"
@packed = global <{ i24, i8, i40, i8 }> <{ i24 0, i8 71, i40 0, i8 99 }>
define i32 @main() {
entry:
  store i24 1193046, ptr @packed, align 1
  %first_flag = getelementptr <{i24, i8, i40, i8}>, ptr @packed, i64 0, i32 1
  %marker = load i8, ptr %first_flag
  %first_bad = icmp ne i8 %marker, 71
  %wide_slot = getelementptr <{i24, i8, i40, i8}>, ptr @packed, i64 0, i32 2
  store i40 78187493530, ptr %wide_slot, align 1
  %wide_loaded = load i40, ptr %wide_slot, align 1
  %second_bad = icmp ne i40 %wide_loaded, 78187493530
  %last_slot = getelementptr <{i24, i8, i40, i8}>, ptr @packed, i64 0, i32 3
  %last_flag = load i8, ptr %last_slot
  %third_bad = icmp ne i8 %last_flag, 99
  %first_two = or i1 %first_bad, %second_bad
  %all_bad = or i1 %first_two, %third_bad
  %result = zext i1 %all_bad to i32
  ret i32 %result
}