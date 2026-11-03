# Limitations

What llvmnet does not do, in one place. The two tables are generated from code: the compiler's rejected constructs from the probes behind [support-matrix.md](support-matrix.md), and the portable host's refused system calls from the runtime's own table; `scripts/limitations.py` writes this page and `tests/limitations.sh` keeps it current. Everything else here is the documented state of the 0.x releases; the plan items in parentheses track the open ones.

## Compiler

Programs are compiled through Clang and Flang 22 and translated from LLVM IR to CIL; what the frontends reject is theirs to report. The backend rejects the constructs below with a compile error naming the function and the instruction ([driver.md](driver.md#exit-status)); everything else in the support matrix is translated.

32 of the 223 probed constructs are rejected (191 translated).

| Construct | Diagnostic |
| --- | --- |
| Types: `i256` | Unsupported LLVM type: i256 |
| Types: `half` | Unsupported LLVM type: half |
| Types: `bfloat` | Unsupported LLVM type: bfloat |
| Types: `fp128` | Unsupported LLVM type: fp128 |
| Types: `ppc_fp128` | Unsupported LLVM type: ppc_fp128 |
| Types: `<vscale x 4 x i32>` | Unsupported LLVM type: <vscale x 4 x i32> |
| Instructions: `indirectbr (computed goto)` | Unresolved value: ptr blockaddress(@probe, %b) |
| Instructions: `atomicrmw uinc_wrap` | Atomic RMW operation 15 |
| Instructions: `inline asm (instructions)` | Inline assembly with instructions or outputs is not supported; use a pure C configuration. |
| Instructions: `callbr (asm goto)` | Unsupported LLVM opcode 67 |
| Instructions: `va_arg instruction` | Unsupported LLVM opcode 49 |
| Instructions: `musttail call with a byval argument` | musttail call to probe: a byval argument cannot be a tail call, because its copy is in the caller's frame. |
| Instructions: `musttail call forwarding variadic arguments` | musttail call to probe: calls that forward variadic arguments cannot be tail calls. |
| Instructions: `musttail call with a 32-byte vector argument` | musttail call to probe with type <8 x float> (<8 x float>): arguments and results of more than 16 bytes cannot be tail calls, because Windows x64 and ARM64 pass them by reference to a copy in the caller's frame. |
| Instructions: `catchswitch / catchpad (Windows EH)` | Unsupported LLVM type: token |
| Intrinsics: control: `llvm.returnaddress / frameaddress` | Unsupported intrinsic: llvm.returnaddress |
| Intrinsics: control: `llvm.eh.sjlj.setjmp / longjmp (__builtin_setjmp)` | Unsupported intrinsic: llvm.eh.sjlj.longjmp |
| Intrinsics: control: `llvm.set.rounding` | Unsupported intrinsic: llvm.set.rounding |
| Intrinsics: integer: `llvm.sshl.sat.i128` | Wide integer intrinsic not implemented: llvm.sshl.sat.i128 |
| Intrinsics: integer: `llvm.ushl.sat.i128` | Wide integer intrinsic not implemented: llvm.ushl.sat.i128 |
| Intrinsics: integer: `llvm.smul.fix.i32` | Unsupported intrinsic: llvm.smul.fix.i32 |
| Intrinsics: floating (x86_fp80): `llvm.canonicalize.f80` | Unsupported extended floating intrinsic: llvm.canonicalize.f80 |
| Intrinsics: floating (x86_fp80): `llvm.minimum.f80` | Unsupported extended floating intrinsic: llvm.minimum.f80 |
| Intrinsics: floating (x86_fp80): `llvm.maximum.f80` | Unsupported extended floating intrinsic: llvm.maximum.f80 |
| Intrinsics: floating (x86_fp80): `llvm.minimumnum.f80` | Unsupported extended floating intrinsic: llvm.minimumnum.f80 |
| Intrinsics: floating (x86_fp80): `llvm.maximumnum.f80` | Unsupported extended floating intrinsic: llvm.maximumnum.f80 |
| Intrinsics: floating (x86_fp80): `llvm.fptosi.sat.i32.f80` | Saturated conversion type: llvm.fptosi.sat.i32.f80 |
| Intrinsics: vector: `llvm.vector.reverse` | Unsupported intrinsic: llvm.vector.reverse.v4i32 |
| Intrinsics: vector: `llvm.get.active.lane.mask` | Unsupported intrinsic: llvm.get.active.lane.mask.v4i1.i64 |
| Intrinsics: x86: `SSE and AVX operations (llvm.x86.sse2.psad.bw)` | x86 target intrinsic llvm.x86.sse2.psad.bw is not translated; build the code's portable C path instead |
| Linkage: `ifunc` | GNU indirect function (ifunc) i is not supported; build without ifunc resolvers. |
| Linkage: `external thread_local global` | External thread-local global requires a bitcode definition: t |

Further limits of the translation:

- Vector IR runs as scalar code: LLVM's vectorizers are off by default and their output is scalarized (ADR-10); the opt-in `--simd128` header path maps `wasm_simd128.h` intrinsics to `Vector128` ([simd128.md](simd128.md)).
- x86 target intrinsics (`llvm.x86.*`, from SSE and AVX code paths) are not translated, apart from `_mm_pause` and the SSE fences: compiling a unit that contains one warns, and linking a program that reaches it fails. System mode predefines `__SSE__` and `__SSE2__`, so libraries take such paths there; `-U__SSE__ -U__SSE2__` or the library's own switch selects its C path ([modes.md](modes.md#system-abi)).
- `longjmp` cannot cross native frames (system mode), and an external thread-local global needs a bitcode definition (SA-03).
- A `musttail` call (`[[clang::musttail]]`) becomes a CIL `tail.` call and runs in constant stack under the JIT and NativeAOT; a function that makes one takes its allocas and variadic call area from llvmnet's per-thread arena instead of the CLR stack, because NativeAOT reuses the frame only when it has no `localloc`. The `musttail` calls in the table above are rejected: a `byval` argument, an argument or result of more than 16 bytes (which Windows x64 and ARM64 pass by reference to a copy in the caller's frame), and forwarded variadic arguments. Windows x64 also passes 16-byte vectors, `long double` and 128-bit integers by reference, so there a NativeAOT executable makes a `musttail` call with one an ordinary call (the JIT keeps the guarantee through its tail-call helpers). In system mode a `musttail` call to a native function is an ordinary call. Ordinary `tail` calls stay ordinary calls: the prefix would keep RyuJIT from inlining the callee.
- A function with very many aggregate values or `setjmp` sites can exceed the CLR's limits on locals, and a very large function runs without JIT optimization; `--diagnose-limits` reports both (BE-25, BE-26).
- Emitted assemblies carry no debug information yet, so stack traces show LLVM symbol names without source lines; `-g` gives compiler diagnostics a `file:line:col` location (BE-28).
- Native code cannot call a translated variadic or aggregate callback (system mode, SA-02).
- A conservative garbage collector that finds its roots by scanning the C stack and the registers `setjmp` saved (Boehm GC, SIOD and similar interpreters) does not see pointers that translated code keeps in CIL locals and evaluation-stack slots, so it can free live objects; such a collector needs its roots registered explicitly (found by llvm-test-suite's SIOD, WG-01).

## Portable mode

A portable program (`dotnet64-v2`) contains musl, libc++ and flang-rt, and reaches the operating system through the host layer in `LlvmNet.Runtime`, which implements Linux system-call semantics over .NET ([dev/host-abi.md](../dev/host-abi.md)). The services .NET cannot provide are refused with the errno below; a number the host does not implement at all returns `ENOSYS` without a name.

| Area | Calls | Result | Why |
| --- | --- | --- | --- |
| Processes | fork, vfork, clone, clone3 | `ENOSYS` | .NET cannot duplicate a process or create a raw thread; posix_spawn, system and popen start child processes, and pthread_create goes through the host's thread entry point |
| Processes | execve, execveat | `ENOSYS` | a .NET process cannot replace its image; posix_spawn starts the program as a child |
| Processes | setpgid, setsid | `EPERM` | process groups and sessions are not controllable from .NET; getpgrp and getsid report the process id |
| Processes | ptrace, personality, chroot, mount, umount2, unshare, seccomp | `ENOSYS` | no process tracing, execution domains, root changes, mounts, namespaces or system-call filters in a managed process |
| Processes | sched_setaffinity | `ENOSYS` | managed threads have no processor affinity; sched_getaffinity reports the processors .NET uses |
| Processes | process_vm_readv, process_vm_writev | `ENOSYS` | another process's memory is not reachable |
| Sockets | socket, socketpair, connect, bind, listen, accept, accept4, send and receive calls, shutdown, getsockname, getpeername, setsockopt, getsockopt | `ENOSYS` | sockets are not provided in 1.0 (LC-13); a later release may implement them over .NET sockets |
| File system | link, linkat | `EPERM` | .NET cannot create hard links (LC-30) |
| File system | mknod, mknodat | `EPERM` | no device files or named pipes; an existing FIFO is reported as a regular file (LC-30) |
| File system | setxattr, getxattr, listxattr, removexattr and their l- and f- forms | `ENOSYS` | extended attributes have no .NET interface |
| File system | inotify_init, inotify_init1, inotify_add_watch, inotify_rm_watch | `ENOSYS` | file-system notification is not provided |
| File system | splice, tee, vmsplice | `ENOSYS` | pipe-to-pipe and pipe-to-memory transfers are not provided; read and write work on pipes |
| Descriptors | epoll_create, epoll_create1, epoll_ctl, epoll_wait, epoll_pwait | `ENOSYS` | no epoll; poll, ppoll, select and pselect work on pipes and files |
| Descriptors | eventfd, eventfd2, signalfd, signalfd4, timerfd_create, memfd_create, userfaultfd, io_uring_setup | `ENOSYS` | these descriptor kinds have no .NET equivalent; pipes serve as wake-up descriptors |
| Interprocess communication | shmget, shmat, shmdt, shmctl, semget, semop, semctl, msgget, msgsnd, msgrcv, msgctl, mq_open | `ENOSYS` | System V and POSIX message queues, semaphores and shared memory are not provided (LC-30); shared file mappings within one process work |
| Timers | timer_create | `ENOSYS` | POSIX interval timers that deliver signals are not provided; nanosleep, clock_nanosleep and alarm-free waits work |

Behavior that differs from a Linux kernel, in short ([dev/host-abi.md](../dev/host-abi.md#behavior-that-differs-from-a-linux-kernel) has the full list):

- Signals raised inside the process are delivered synchronously on the raising thread; `SIGINT`, `SIGTERM`, `SIGHUP` and `SIGQUIT` from outside reach a handler on a .NET signal thread; faults such as `SIGSEGV` are not delivered, the CLR reports them.
- Child processes are host processes that inherit only the standard descriptors; `system` and `popen` need `/bin/sh` on the host.
- File mappings are copies written back at `msync` and `munmap`; there is no memory protection.
- The process runs as user and group 1000 and owns every file it can see; inode numbers are hashes of the path.
- Descriptor-relative calls (`openat` and the other `*at` calls) resolve against the directory's path, so they are not safe against a directory being replaced between calls.
- Only the C and C.UTF-8 locales exist; the floating-point environment is fixed to round-to-nearest without exception flags.
- `__linux__`, `__unix__`, `__x86_64__` and the other host macros are not defined (ADR-14); a program that selects platform code by them needs a portable path or a documented per-program flag.
- Thread stacks are at least 8 MB and `CLOCK_THREAD_CPUTIME_ID` reports process time.
- Native libraries are reached only through native extensions ([extensions.md](extensions.md)), which pass integers, floating point, pointers and structures by value without unions or bit-fields; Vulkan's window-system extensions, variadic functions and C++ interfaces are not proxied, a callback on a thread the host library started gets thread state without thread-local or key destructors, and `dlopen` serves only the libraries of the linked extensions.

## Hosts other than Linux

The same image runs on Linux x64 and ARM64, Windows x64 and macOS ARM64 ([deployment.md](deployment.md#supported-hosts)); macOS and ARM64 behave like Linux x64 in every host-interface check. On Windows the file system and the shell differ ([dev/host-abi.md](../dev/host-abi.md#hosts-other-than-linux)):

- Paths: a drive is a root directory named by its letter (`/c/Users/...`), `/tmp` is the temporary directory, the file system is case-insensitive.
- Permissions: there are no mode bits; `stat` reports `0644` or `0755`, `chmod` sets or clears the read-only attribute, and no directory is unsearchable.
- Symbolic links need the create-symbolic-link privilege (Developer Mode or an elevated process).
- `system` and `popen` need `sh.exe` from Git for Windows on `PATH`.
- Time-zone abbreviations from generated zoneinfo images are numeric (`+01`).

The compiler itself runs on Linux x64 only (ADR-05): NativeAOT executables for Windows and macOS are published on those hosts from the project `--aot-project` writes.

## System mode

- Linux x86-64 only (`system-linux-x64-v1`); a Linux ARM64 system ABI is post-1.0 (SA-08).
- Native code cannot call translated variadic or aggregate callbacks (SA-02); thread cancellation and `pthread_exit` across managed frames (SA-05), signal handlers in translated code (SA-06), external native TLS globals (SA-03) and callbacks on threads created by native libraries (SA-04) are incomplete.
- No native C++ library interchange: C++ library code must be linked as bitcode (the optional libstdc++ archive, or libc++ in portable mode).
- `fork`, `vfork`, `_Fork` and `forkpty` fail with `ENOSYS` under the JIT, whose W^X protection maps generated code from shared memory that a child would share with its parent; NativeAOT executables, and the JIT with `DOTNET_EnableWriteXorExecute=0`, call the host's `fork`, and the child should do no more than a multithreaded program's child may before `exec`. `posix_spawn`, `system` and `popen` work in every case ([modes.md](modes.md#system-abi)).
- OpenMP needs a staged LLVM OpenMP runtime on the loader path (`scripts/install-system-openmp.sh`).
- `-static` is not accepted: the host's C library and native libraries are reached through generated bridges.

## Workloads

- ffmpeg's FATE suite is not run; the ffmpeg profile is checked on a fixture set and, locally, on private media (WG-04).
- SPEC CPU runs use explicitly selected inputs and are not reportable results; the portable-mode exclusions (`fork`, glibc-specific behavior, prototypes that contradict the definition) are listed in the SPEC evidence (ADR-14).
