; Library calls through K&R declarations, found by llvm-test-suite (WG-01):
; - cfrac declares "extern int free();" and calls the C library's void free(void *) as i32 (ptr, ...); the unused
;   result must neither break the call (system mode emitted invalid IL) nor be required.
; - C requires rand() before any srand() to give the sequence srand(1) gives. In system mode the .NET runtime seeds
;   the C library's generator while it starts (ft's graphs differed); the runtime restores the seed before main.
; Exits with 0 when both hold.
declare ptr @malloc(i64)
declare void @free(ptr)
declare i32 @rand()
declare void @srand(i32)

define i32 @main() {
entry:
  %block = call ptr @malloc(i64 16)
  store i8 1, ptr %block
  %ignored = call i32 (ptr, ...) @free(ptr %block)
  %first = call i32 @rand()
  call void @srand(i32 1)
  %again = call i32 @rand()
  %same = icmp eq i32 %first, %again
  %status = select i1 %same, i32 0, i32 1
  ret i32 %status
}
