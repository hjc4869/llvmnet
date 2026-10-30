/* llvmnet port of musl: system calls go to the managed host layer, which implements Linux x86-64
 * system call numbers and semantics over .NET APIs (src/LlvmNet.Runtime/Host/HostSyscalls.cs),
 * so musl's C code is used unchanged. */
#define __SYSCALL_LL_E(x) (x)
#define __SYSCALL_LL_O(x) (x)

long __llvmnet_syscall(long, long, long, long, long, long, long);

static __inline long __syscall0(long n) { return __llvmnet_syscall(n, 0, 0, 0, 0, 0, 0); }
static __inline long __syscall1(long n, long a1) { return __llvmnet_syscall(n, a1, 0, 0, 0, 0, 0); }
static __inline long __syscall2(long n, long a1, long a2) { return __llvmnet_syscall(n, a1, a2, 0, 0, 0, 0); }
static __inline long __syscall3(long n, long a1, long a2, long a3) { return __llvmnet_syscall(n, a1, a2, a3, 0, 0, 0); }
static __inline long __syscall4(long n, long a1, long a2, long a3, long a4) { return __llvmnet_syscall(n, a1, a2, a3, a4, 0, 0); }
static __inline long __syscall5(long n, long a1, long a2, long a3, long a4, long a5) { return __llvmnet_syscall(n, a1, a2, a3, a4, a5, 0); }
static __inline long __syscall6(long n, long a1, long a2, long a3, long a4, long a5, long a6) { return __llvmnet_syscall(n, a1, a2, a3, a4, a5, a6); }

#define IPC_64 0
