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
  %before_ok = icmp eq i32 %before, 41
  %after_ok = icmp eq i32 %after, 42
  %ok = and i1 %before_ok, %after_ok
  %result = select i1 %ok, i32 0, i32 1
  ret i32 %result
}