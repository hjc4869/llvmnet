define i32 @main() {
entry:
  %memory = alloca i32, align 4
  store i32 41, ptr %memory, align 4
  call void asm sideeffect "", "r,~{memory},~{dirflag},~{fpsr},~{flags}"(ptr %memory)
  %before = load i32, ptr %memory, align 4
  store i32 42, ptr %memory, align 4
  call void asm sideeffect "", "~{memory},~{dirflag},~{fpsr},~{flags}"()
  call void asm sideeffect "# matrix packing marker", "~{dirflag},~{fpsr},~{flags}"()
  call void asm sideeffect " \09# first comment; still a comment\0A\09\0A# second comment", "r,~{memory},~{dirflag},~{fpsr},~{flags}"(ptr %memory)
  %after = load i32, ptr %memory, align 4
  ; asm("" : "+r"(x)), asm("" : "=r"(p) : "0"(&m)) and asm("" : "+r"(x) : "r"(y), "r"(z) : "memory"): an empty
  ; template whose output is tied to an input yields the input.
  %tied = call i32 asm sideeffect "", "=r,0,~{dirflag},~{fpsr},~{flags}"(i32 %after)
  %pointer = call ptr asm "", "=r,0,~{dirflag},~{fpsr},~{flags}"(ptr %memory)
  %through = load i32, ptr %pointer, align 4
  %wide = call i64 asm sideeffect "", "=r,r,0,r,~{memory},~{dirflag},~{fpsr},~{flags}"(i32 1, i64 -5, i32 2)
  %tied_ok = icmp eq i32 %tied, 42
  %through_ok = icmp eq i32 %through, 42
  %wide_ok = icmp eq i64 %wide, -5
  %before_ok = icmp eq i32 %before, 41
  %after_ok = icmp eq i32 %after, 42
  %values_ok = and i1 %tied_ok, %through_ok
  %all_ok = and i1 %values_ok, %wide_ok
  %stored_ok = and i1 %before_ok, %after_ok
  %ok = and i1 %stored_ok, %all_ok
  %result = select i1 %ok, i32 0, i32 1
  ret i32 %result
}