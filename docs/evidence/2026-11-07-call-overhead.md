# Evidence: the fixed cost of a translated call as NativeAOT (2026-11-07)

Host: AMD Ryzen AI MAX+ 395 (Zen 5), Linux 6.12.113, .NET 10.0.14 (ILCompiler 10.0.14), Debian clang 22.1.8. Branch `x264-perf` at `22a0f55`. The disassembly comes from `--aot-debug` builds (`objdump -d -M intel` of `__managedcode`), the profiles from the x264 evaluation's sampler (`artifacts/x264-perf/tools/libsampler.so`), and the RyuJIT and ILC sources quoted are the `dotnet/runtime` checkout in `artifacts/coreclr/runtime`. SPEC 520.omnetpp_r is relinked from the SPEC run's bitcode objects (`build_base_llvmnet_portable_O3_v1_c14_t1.0000`, the link line of its `make.out` with another `-o`) and runs its refrate workload (`-c General -r 0`); x264 is 525.x264_r's refrate workload 1 built by `artifacts/x264-perf/build.sh` at scalar and Vector128. Both toolchains of every comparison are packaged from this worktree (`scripts/package-toolchain.sh`), so their `LlvmNet.Runtime.dll` is byte-identical.

**Timing on a shared host.** Five other sessions ran gates, SPEC runs and benchmarks on the same 32 threads throughout (load average 10 to 75). The comparisons therefore run the executables being compared at the same time, each pinned to its own core, half on each CCD (`taskset`), rotating the cores every round, each run from a fresh copy of the executable in a fresh copy of the run directory (`pair.py` in the session's scratch space), and report user CPU time, since wall time there mostly measures how long a process waited for its core. Under that load an unchanged kernel (x264's `x264_pixel_satd_8x4`) differs by 5 to 8% between two simultaneous profiles, so differences under about 5% are not significant here, and the coordinator re-measures every item on a quiet host.

## PF-24: the variadic call area, static allocas and byval copies in frame slots

A function that made a variadic call anywhere reserved the call area on entry with `localloc` (`VarArgEmitter.Allocate`). RyuJIT gives a method with `localloc` a frame pointer, a stack probe (`test [rsp],esp`) before the allocation, and, as NativeAOT, a stack cookie written in the prolog and compared before every return (`mov rax,[__security_cookie]` ... `cmp [rbp-0x20],r8; je; call RhpFallbackFailFast`), and keeps the block's address in a callee-saved register for the whole method. A probe of the omnetpp pattern, `if (s->a < 0) fail("bad %d %f\n", x, 1.5); return s->b + x;`, compiled to 240 bytes with four saved registers, the probe and the cookie on its fast path.

The plan's premise that static allocas already had frame slots did not hold: every alloca was a `localloc`, and RyuJIT turns a constant `localloc` of at most 32 bytes outside a loop into a local (`importer.cpp`, `DEFAULT_MAX_LOCALLOC_TO_LOCAL_SIZE`) that it marks an unsafe buffer, which keeps the cookie (`lvIsUnsafeBuffer`, `setNeedsGSSecurityCookie`). So in omnetpp the cookie of `cGate::deliver` came as much from its three 8-byte `SimTime` temporaries as from its `cRuntimeError` throw, and 1,639 methods of the image had one.

What changed:

- **Frame slots.** The variadic call area, every static alloca (in the entry block, of a constant size up to 64 KiB) and every `byval` parameter's copy is a local of an explicit-layout value type of its size (`TypeSystem.Block`, rounded up to 16 bytes). A value type without `UnsafeValueTypeAttribute` is not an unsafe buffer, so the method gets neither the cookie nor `localloc`'s frame pointer and probe; the slots are part of the fixed frame (`sub rsp`). The methods keep `localsinit` off, as with `localloc`, so the slots are not zeroed.
- **Addresses at their uses.** A static alloca's or `byval` copy's address is `ldloca` at each use instead of a value in a local (`FunctionEmitter.SlotAddress`), so RyuJIT addresses the slot from the frame (`lea rdi,[rbp-0x58]`) instead of keeping a pointer in a register; alignments above 8 bytes, beyond what RyuJIT gives a local of 8 bytes or more on 64-bit targets, round the address up from a slot that is `alignment - 1` bytes larger. The variadic area's address is computed, 16-byte aligned (`va_arg` aligns the overflow area's pointer itself), at each variadic call (`VarArgEmitter.Pack`), on the error path.
- **What keeps `localloc`.** Dynamic allocas; static ones past 64 KiB; a function with a `musttail` call, which keeps the alloca arena as before (slots there, which would put address-taken locals in a method that makes a fast tail call, were not tried); and, under the JIT only, a function with a loop. RyuJIT does not replace a method with `localloc` on the stack (`compCanHavePatchpoints`: "OSR can't handle localloc"), so the JIT compiles such a method optimized at once; with frame slots it would run at tier 0 and be replaced on the stack, and on-stack-replacement code of a method without `localsinit` ran 2.6 times slower in lpbench (PF-04). NativeAOT has no on-stack replacement, so there every function takes slots.

**Code.** The probe's `deliver` is 208 bytes, no cookie, probe or `localloc`, and the area's address is computed only on the throwing path (`lea rbx,[rbp-0x179]; and rbx,-16`); the frame pointer stays, because RyuJIT on Unix x64 keeps one in every method of more than three blocks (`rpMustCreateEBPFrame`, `ETW_EBP_FRAMED`). In omnetpp the methods with a cookie went from 1,639 to 156, all of them in the .NET class library except musl's `getcwd`, whose buffer is a variable-length array; the executable is 185 KB smaller (8,281,584 to 8,096,960 bytes). In x264 at Vector128, `x264_me_search_ref`, `refine_subpel` and `x264_slicetype_mb_cost` lose their cookies and their `localloc` prologs (three probes and allocations in `x264_me_search_ref`) and shrink by 960, 368 and 672 bytes.

**Correctness.** [tests/c/frame-slots.c](../../tests/c/frame-slots.c) (`tests/frame-slots.sh`) checks allocas of every alignment from 1 to 64 bytes, a zero-size alloca's address, an array per recursion level, an inlined callee's array in a loop, an address kept across `setjmp` and `longjmp`, a 100,000-byte array past the limit, a `byval` struct changed by each recursion level and a variadic call with a `long double` on an error path, against native under the JIT at `-O0` and `-O2` and as NativeAOT; [tests/c/varargs-aggregates.c](../../tests/c/varargs-aggregates.c) adds a variadic call in a loop (`localloc` under the JIT, a slot as NativeAOT) whose `long double` follows fifteen integers in the overflow area, so its 16-byte alignment holds only from an aligned area. Both, `varargs-callbacks`, `native-varargs-pointers`, `musttail`, `nonlocal-jumps`, `stack`, `thread-stack`, `fortran-stack` and `optimization-levels` pass, and so does the core gate.

**Speed.** User CPU seconds, medians of three rounds, base against PF-24 (wall times on this host mostly measure descheduling): x264 scalar 43.39 and 44.93 s, Vector128 26.37 and 25.20 s, with single runs from 21.97 to 47.46 s, every run writing the expected `BuckBunny_New.264`; omnetpp 909 and 877 s, three times its unloaded 388 s, with single runs from 816 to 1,131 s, and outputs that match SPEC's `General-0.sca` (`specdiff` with the run's tolerances). The differences, in both directions, are within the noise; the x264 functions that changed (`x264_me_search_ref`, `refine_subpel`, `x264_slicetype_mb_cost`) hold 3 to 4% of its time. The cumulative measurement of all four items is in [Measurements](#measurements).

## PF-26: no type initializer on `Program` as NativeAOT

`Program`'s type initializer allocates and fills every global (and runs the ABI check, extension registrations and, in system mode, the C++ constructors). ILC preinitializes a class only when it can run its type initializer at build time, which this one, calling the runtime and allocating native memory, never allows, and `Program` is not `beforefieldinit`. So every method of `Program`, which is where translated functions live, began with a check that the initializer had run (`lea rax,[__NONGCSTATICSprogram_Program]; cmp qword [rax-8],0; jne` to `__GetNonGCStaticBase_program_Program`), also when it reads no global, and a method that reads globals checked again before reads on paths the first check does not dominate. The call to the helper on the cold path also kept values in callee-saved registers across it: the probe's `int plain(struct S *s, int x) { return s->b + x; }` was 64 bytes with two saved registers and an aligned frame.

What changed: in a NativeAOT image (`--nativeaot`, `--aot-project`), the initializer's body is an ordinary method, `__llvmnet_initialize`, that `__Entry` calls first, where the type initializer used to run (before `ProcessRuntime.Initialize`), and `Program` has no type initializer, so ILC counts it initialized from the start and reaches its statics at their fixed address. NativeAOT builds only executables, and nothing reaches translated code before the entry point: there is no module initializer, `__Startup.Main` only checks the ABI first, and threads, signal handlers and callbacks start from translated code. The address fields lose `initonly` there: ILC reads an `initonly` field as a constant only for a class it preinitialized (`CorInfoImpl.getStaticFieldContent`, `PreinitializationManager.IsPreinitialized`, which requires a type initializer), so the flag would gain nothing, and the fields are written outside a type initializer. The JIT keeps the type initializer and `initonly`, which JP-04 needs to read the addresses as constants in optimized code, compiled after the initializer has run, and whose optimized code has no check.

**Code.** The probe's `plain` is `mov eax,esi; add eax,[rdi+4]; ret`, as native code has it; `deliver`'s fast path is `push rbp; sub rsp; lea rbp; cmp; jl; mov; add; add rsp; pop rbp; ret`, without the check and the three saved registers its slow path needed. In omnetpp the calls of the helper go from 10,087 to none, and the checks (`cmpq $0x0,-0x8(%reg)`) from 11,046 to 959, which are in the class library's own classes; `cGate::deliver` is 672 bytes with five saved registers (736 and six after PF-24). In x264 the checks go from 1,478 to 831, the class library's.

**Correctness.** NativeAOT runs are part of most core tests (C, C++ with static constructors, thread-local globals, system mode, native extensions, kernel packages); the core gate passes. The timing is in [Measurements](#measurements).

## PF-27: indirect calls without the fat-pointer test

As NativeAOT, an `ldftn` of a shared generic method yields a fat function pointer, the address of a descriptor holding the code and the instantiation argument with bit 1 set, so ILC marks every managed `calli` a fat-pointer candidate (`CORINFO_SIGFLAG_FAT_CALL`, `Compilation.IsFatPointerCandidate`) and RyuJIT tests the target before the call: `test al,2; je`, and on the other path the instantiation argument loaded from the descriptor and `call [rax-2]`, with the arguments set up twice. Every C function-pointer call and C++ virtual call of translated code is such a `calli`. ILC makes two exceptions: unmanaged calls, and a `calli` in a method of a class named `RawCalliHelper`, which is how the class library calls targets that cannot be fat (`System.Runtime.RawCalliHelper`, `AggressiveInlining` methods that contain only the `calli`).

An unmanaged `calli` would need a GC transition, or with `SuppressGCTransition` would leave the call site without GC information while managed code runs in the callee, so the translated form keeps its managed `calli`. What changed: in a NativeAOT image, `CilCompiler.ManagedCalli` calls an `AggressiveInlining` method of one per signature in a class `LlvmNet.RawCalliHelper`, which pushes the target last and makes the `calli`, as the inline call did; RyuJIT inlines it and the `calli` keeps the stub's exception. It is safe because translated code only calls addresses of methods that are not generic: `ldftn` of translated and runtime methods, the managed targets `SystemAbi.ResolveCallback` returns for them, and the stubs native extensions return from `Resolve`, which `ext-gen.py` generates as static methods of a non-generic `Api` class; a hand-written extension must do the same ([dev/host-abi.md](../dev/host-abi.md)). Native addresses go through the unmanaged `calli` as before. Two cases keep the inline `calli`: `musttail` calls (`tail.` does not cross a stub), and methods of more than 512 IL locals, into which RyuJIT may not inline (it stops at about 920 locals with its own temporaries), where a stub would be a call more. Should a later ILC drop the name check, the stubs test the target again and cost nothing else.

**Code.** The probe `long t = 0; for (i = 0; i < 200000000; i++) t += table[i & 1](i, argc);` loads the target and calls it, `mov rax,[rdi+rsi*8]; mov edi,r14d; mov esi,ebx; call rax`, where it had `test al,0x2; je` and the fat path's `mov rdi,[rax+0x6]` and `call [rax-0x2]`; no stub is left uninlined in [tests/c/indirect-signatures.c](../../tests/c/indirect-signatures.c)'s `main` (1,456 bytes before, 1,072 after). In omnetpp the translated methods' tests of bit 1 (`test $0x2,%reg`) go from 7,612 to 18, which are the C code's own (libc++abi's `__flags & __diamond_shaped_mask`, `__cxa_guard_acquire`'s guard byte), and the executable with symbols shrinks by 223 KB (14,849,016 to 14,625,832 bytes); `cGate::deliver` is 544 bytes (736 at the base, native 264). x264's go from 753 to 24.

**Correctness.** [tests/c/indirect-signatures.c](../../tests/c/indirect-signatures.c) (`tests/indirect-signatures.sh`) calls through pointers that `volatile` keeps indirect at `-O2`: no arguments, two `int`s through a table in a loop, a `{ long, double }` and a 40-byte struct returned, a 16-byte vector, a `long double` and an `__int128` with an `int` (signatures the compiler patches into the `calli` after emission), nine `long`s (three on the stack) and a pointer result, and `qsort`'s callback from musl; it matches native in both modes under the JIT and as NativeAOT, and as NativeAOT with `--check-indirect-calls`. The core gate passes. The timing is in [Measurements](#measurements).

## PF-28: x264's motion search

The four functions, profiled with the sampler over workload 1 (`--aot-debug` builds; each llvmnet profile taken at the same time as a native one, on another core), in seconds:

| Function | native | base | ratio | native | PF-24 to PF-27 | ratio |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Vector128 `get_ref` | 2.03 | 2.87 | 1.41 | 2.26 | 2.73 | 1.21 |
| Vector128 `refine_subpel` | 0.36 | 0.65 | 1.81 | 0.39 | 0.56 | 1.44 |
| Vector128 `x264_me_search_ref` | 0.49 | 0.83 | 1.69 | 0.53 | 0.79 | 1.49 |
| Vector128 `x264_slicetype_mb_cost` | 0.66 | 0.97 | 1.47 | 0.70 | 0.79 | 1.13 |
| Vector128, whole program | 22.17 | 26.80 | 1.21 | 23.88 | 26.04 | 1.09 |
| scalar `get_ref` | 4.41 | 5.10 | 1.16 | 4.67 | 5.05 | 1.08 |
| scalar `refine_subpel` | 0.47 | 0.91 | 1.94 | 0.50 | 0.69 | 1.38 |
| scalar `x264_me_search_ref` | 0.65 | 0.97 | 1.49 | 0.66 | 0.97 | 1.47 |
| scalar `x264_slicetype_mb_cost` | 0.76 | 1.13 | 1.49 | 0.81 | 0.95 | 1.17 |
| scalar, whole program | 41.84 | 47.36 | 1.13 | 43.38 | 47.43 | 1.09 |

(The host's load slows both sides alike within a pair, but not between pairs; the whole program took 22 to 24 s natively at Vector128 against 12.4 s on a quiet host.)

What the annotations showed, and where it went:

- **Prologs (PF-24).** `x264_me_search_ref` began with the stack cookie, three `localloc`s each with a probe (`test [rsp],esp; sub rsp; lea; add; and`), and stored each alloca's address in the frame (`mov [rbp-0x80],r9` ... seven of them); `refine_subpel` (a 1,152-byte buffer and nine small arrays) and `x264_slicetype_mb_cost` had cookies. All are frame slots addressed from the frame now.
- **The static base (PF-26).** Each began with the check, and `x264_me_search_ref` kept the static base's address in two frame slots (`mov [rbp-0x550],rax; mov rax,[rbp-0x550]; mov [rbp-0x558],rax`) to reload it after calls. NativeAOT now addresses the statics at their fixed place.
- **Indirect calls (PF-27).** x264 calls its pixel functions through tables (`h->pixf.fpelcmp[i_pixel]`, `h->mc.get_ref`): `refine_subpel` has 49 indirect call sites at Vector128, each a `test r12b,2; je` and two copies of the argument setup, one for each path. They are plain `call r12` now.
- **Register allocation (left).** `x264_me_search_ref` is 3,304 LLVM instructions in 440 blocks (the diamond, hexagon and exhaustive searches inlined); after BE-33's sharing it has 125 IL locals, all of which RyuJIT tracks, and 6,611 local references. A third of its 7,445 instructions address the frame, and a fifth of its samples are on stack loads and stores. In `get_ref`'s averaging loop (`pixel_avg_wxh`, one 8-byte `pavgb` per row) RyuJIT reloads a value and stores it back unchanged (`mov r14d,[rbp-0xa0]; mov [rbp-0xa0],r14d`) and stores unchanged stack-passed arguments back into their incoming slots (`mov [rbp+0x18],r14d`, `i_height`) on every row: PF-20's pattern, which this adds a second program to. `refine_subpel`'s calls load their targets through field addresses that LLVM hoisted to the entry block (`getelementptr i8, ptr %0, i64 C`) and llvmnet keeps in locals, which RyuJIT spills (`mov r12,[rbp-0x1a0]; mov r12,[r12]`, then `call r12`, where native code calls through the field, `call [r13+0x7f70]`): PF-23's pattern.

## Measurements

The four stages, base (`22a0f55`), PF-24 (`e73b2d6`), PF-26 (`dc7575a`) and PF-27 (`2cda422`; PF-28 changes no code), packaged from this worktree with `SourceRevisionId=ladder`, so all four carry the same version and byte-identical `LlvmNet.Runtime.dll`, and measured together: every round runs the four executables at once on cores 2, 10, 6 and 14 (x264) or 4, 12, 7 and 15 (omnetpp), two per CCD, rotating them, four rounds, user CPU seconds.

**Per call.** A probe that calls each pattern 200 million times from a loop, as NativeAOT at `-O2` (native: clang-22 `-O2`); the five builds run one after another on one core, nine rounds, median user time per call:

```c
__attribute__((noinline)) int checked(struct S *s, int x) { if (__builtin_expect(s->a < 0, 0)) fail("bad %d\n", x); return s->b + x; }
__attribute__((noinline)) int global(struct S *s, int x) { return s->b + table_global[x & 7]; }
__attribute__((noinline)) int local(struct S *s, int x) { int v[2] = { s->b, x }; return sum2(v); }
int (*volatile ops[2])(int, int) = { add, sub };   /* t += ops[i & 1](i, argc) */
```

| ns per call | native | base | PF-24 | PF-26 | PF-27 |
| --- | ---: | ---: | ---: | ---: | ---: |
| `checked` (variadic call on an error path) | 0.92 | 1.93 | 1.83 | 1.60 | 1.59 |
| `global` (reads a global) | 0.93 | 0.94 | 0.95 | 0.92 | 0.90 |
| `local` (an 8-byte local array) | 3.60 | 1.87 | 1.83 | 1.83 | 1.82 |
| indirect call through a table | 1.36 | 1.62 | 1.59 | 1.59 | 1.37 |

PF-24 and PF-26 take `checked` from 2.1 to 1.7 times native; what is left is the frame RyuJIT sets up on the fast path (`push rbp; sub rsp,0x170; lea rbp`), which native code does only on the throwing path. PF-27 makes the indirect call as cheap as native code's. `global`'s loop hides the check (its branch predicts, and the loop is the cost), and `local`'s 8-byte array was already one of the small `localloc`s RyuJIT turns into a local; native code's 3.60 is clang's stores of the array followed at once by `sum2`'s loads of it.

Under the JIT, where only PF-24 applies, the same probe as a JIT image (seven rounds, user seconds for the 200 million calls with start-up) takes 0.543 s at the base and 0.462 s with PF-24 for `checked`, and 0.732 and 0.628 s for `local`: neither function has a loop, so both lose `localloc` there too.

**x264** (workload 1, all 32 runs wrote the expected `BuckBunny_New.264`):

| User seconds | base | PF-24 | PF-26 | PF-27 |
| --- | ---: | ---: | ---: | ---: |
| scalar, median | 45.13 | 46.28 | 45.23 | 46.36 |
| scalar, runs | 41.3 to 49.6 | 41.1 to 48.0 | 43.4 to 48.2 | 42.1 to 48.4 |
| Vector128, median | 23.41 | 23.87 | 23.75 | 23.34 |
| Vector128, runs | 21.9 to 25.5 | 23.1 to 24.3 | 19.3 to 24.8 | 18.7 to 24.9 |

The single runs spread by 15 to 25%, and the medians differ by less than 3%: the host cannot resolve what the four changes are worth to x264. The profiles in PF-28 put it at about 3%: the four functions, a fifth of x264's time, went from 1.50 to 1.26 times native at Vector128 together and from 1.29 to 1.15 scalar.

**omnetpp** (refrate `-c General -r 0`, all 16 runs match SPEC's `General-0.sca`):

| User seconds | base | PF-24 | PF-26 | PF-27 |
| --- | ---: | ---: | ---: | ---: |
| median | 740 | 736 | 701 | 720 |
| runs | 634 to 852 | 649 to 840 | 595 to 830 | 619 to 814 |
| against base in the same round | | 0.986, 0.971, 0.973, 1.079 | 0.974, 0.943, 0.953, 0.939 | 0.955, 1.001, 0.927, 0.987 |
| median of those | | 0.979 | 0.948 | 0.971 |

The host got quieter during the run (the rounds took 852, 812, 668 and 634 s of base's time), so each stage is compared with the base of its own round. With PF-24 and PF-26 omnetpp takes about 5% less time, in all four rounds, against the profile's 31% of samples in methods that paid for the variadic area and every method's static-base check; PF-27 is not distinguishable from PF-26 here (0.97 to 1.06 of it by round), although each indirect call costs native code's time in the probe. The unloaded measurement of the merged branch is the coordinator's.
