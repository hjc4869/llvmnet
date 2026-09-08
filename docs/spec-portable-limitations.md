# Portable SPEC Failure Audit

## Scope And Outcome

On 2026-09-08, all **84 distinct portable failures** from the ordered CPU 2017/2026 inventory were rebuilt and attempted with SPEC output validation. The baseline contains 101 distinct benchmarks: 17 portable PASS and 84 FAIL. Shared CPU 2026 random checks are counted once here, not once per integer/FP group.

After the repairs and focused retries: **3 PASS / 81 FAIL among those 84 previously failed benchmarks**. The new passes are **525.x264_r, 625.x264_s and 765.roms_r**. ROMS required no new implementation in this audit: its historical array-constructor failure was already fixed by the later system-led work, which also applies to portable execution. The other 17 previous passes were not rerun. Thus 20 distinct benchmarks have portable validation across the combined evidence, **not a clean 101-benchmark pass with one compiler**.

These are scalar O3, NativeAOT, test-input, non-reportable checks. Rate uses one instance; speed retains the four-thread kit profile, including actual OpenMP activation and host DO CONCURRENT lowering where applicable. Configured thread counts do not prove workers exist in serial or multiprocess kit entries. No licensed source was changed, no benchmark was declared passed on compilation alone, and no system library was silently substituted into portable output.

**Most failures are implementation gaps, not fundamental limits of CIL.** Incomplete libc++ localization/streams, C math, POSIX services and a missing OpenMP runtime could be implemented or ported. The architecture-related restrictions are narrower: the fixed dotnet64-v1 data contract, typed managed call signatures, and the prohibition on silently consuming native ABI libraries. Those distinctions are preserved below.

## Evidence And Reproduction

The baseline and retry audit retain one row per benchmark, the exact leading diagnostic, the result-table path, the make-log path, timestamp, threads and threading profile:

- [Historical baseline](../artifacts/tests/portable-spec/baseline.tsv), generated from all ordered result tables using their recorded start times, not file modification times.
- [Current audit](../artifacts/tests/portable-spec/current.tsv), selecting the latest retry per benchmark.
- Initial fresh retries: [32 CPU 2017 entries](../artifacts/spec-portable-audit/retry.Vmyt3WpR/results.tsv), [24 CPU 2026 rate entries](../artifacts/spec-portable-audit/retry.h27Lx61b/results.tsv), [28 CPU 2026 speed/random entries](../artifacts/spec-portable-audit/retry.yaC9sHXJ/results.tsv).
- Final focused retries: [x264 rate/speed](../artifacts/spec-portable-audit/retry.7h7wFoK9/results.tsv), [CPython](../artifacts/spec-portable-audit/retry.tpQHhnCY/results.tsv), [POV-Ray](../artifacts/spec-portable-audit/retry.w8MEt7oe/results.tsv).

Each retry directory contains a frozen runner/configuration, completion record, compiler/runtime/native-helper/libc++ hashes, a sysroot hash inventory, harness logs and copied make logs. Most retries used `artifacts/spec-portable-toolchain-final`; the last POV-Ray retry used the separate `artifacts/spec-portable-toolchain-cwd` package after adding directory APIs. Configuration fixes were frozen separately for each retry. Earlier unsuccessful retries remain evidence and are not overwritten.

From the repository root, with the licensed kits installed in their existing artifact locations:

```sh
node scripts/spec-portable-audit.mjs artifacts/spec-ordered > artifacts/tests/portable-spec/baseline.tsv
bash scripts/package-toolchain.sh artifacts/spec-portable-next
SPEC_TIMEOUT=900 bash scripts/spec-portable-check.sh artifacts/tests/portable-spec/baseline.tsv artifacts/spec-portable-next
node scripts/spec-portable-audit.mjs artifacts/spec-portable-audit > artifacts/tests/portable-spec/current.tsv
```

An optional final argument such as `'722.*'` selects one benchmark. The retry runner executes only failed baseline entries, uses clean builds, and returns success when **inventory collection completes**, even if benchmarks fail; inspect `results.tsv` and `completion.txt`. TIMEOUT is incomplete evidence, not a semantic failure or a pass. Never rebuild a package or edit a runner in use. The existing full-video prerequisite in the SPEC harness is retained.

## Repairs

| ID | Repair | Benchmark Effect |
| --- | --- | --- |
| R1 | Add `strncat`, preserving destination identity, bounds and termination; declare existing `strcasecmp`/`strncasecmp` in portable `string.h`. | Removes x264's first failure and FLAC's first failure. FLAC then needs locale support. |
| R2 | Add byte-order-only `netinet/in.h` and `arpa/inet.h`, with `htons`, `ntohs`, `htonl`, `ntohl` implemented by LLVM byte swaps. | x264's RTP decoder builds; Perl advances to `sys/ioctl.h`. These headers do not provide sockets, address parsing or a network stack. |
| R3 | Portable x264 configuration explicitly includes `string.h` and `stdlib.h`. | Its bundled legacy getopt omits declarations without glibc's transitive headers and declares `getenv()` without a prototype. Correct standard declarations fix both problems; both x264 benchmarks pass SPEC validation. No fake `__GLIBC__` macro. |
| R4 | Add `atanh`/`atanhf` and `tgamma`/`tgammaf` declarations and managed exports. | PALM gets past both missing symbols. Rate next reaches A2; speed reaches A3. Gamma uses the existing MathNet implementation; this is not a claim of correctly rounded libm conformance for every input. |
| R5 | Add `ftruncate`, including checked descriptors, negative lengths, extension with zero bytes and unchanged file position after shrinking. | Gem5 advances to missing locale support. No native descriptor escape is added. |
| R6 | Add libc++ `system_clock::now`, `steady_clock::now` and `time_t` conversions using portable `clock_gettime`. | XZ speed advances to missing libc++ thread lifecycle support. |
| R7 | Add `difftime`, `getcwd` and `chdir`. `difftime` subtracts in Int128 before conversion; directory APIs use managed filesystem services. | POV-Ray advances through timing/directory errors and stops at `system`. Includes UTF-8 path bounds and allocated-buffer `getcwd(NULL, 0)`. |
| R8 | Portable CPython enables the kit's `SPEC_PTHREAD_STUBS_NEED_PTHREAD_TYPES` switch. | Its serial pthread-stub header now declares the types it owns; compilation advances to missing `locale.h`. This does not enable actual CPython workers or claim POSIX threads conformance. |
| R9 | Revalidate the current Fortran array-constructor implementation. | 765.roms_r passes clean portable SPEC execution/output checks; the old unresolved `_FortranAInitArrayConstructorVector` is stale. |

Positive regression command: `bash tests/portable-spec.sh`. It compares repaired C functions against native Clang at O3 with `-fno-builtin`, in system/portable JIT and NativeAOT, and checks portable clock JIT/NativeAOT against a native clock fixture. Portable generated images report **zero explicit P/Invoke imports**. Gamma domain/pole/overflow cases, signed zero, string boundaries, time overflow/cancellation, truncation offsets and directory errors are exercised. This is focused compatibility evidence, not exhaustive POSIX/libm conformance.

Minimal independent examples for the repaired causes follow. Compile each as C with `bin/llvmnet --runtime=portable -O0 -fno-builtin probe.c -o probe.dll`, then `dotnet probe.dll`; use a `.cpp` suffix for C++. The tracked [C regression](../tests/c/portable-spec-functions.c) and [clock regression](../tests/cpp/portable-spec-clock.cpp) execute these operations with additional checks.

R1, bounded concatenation:

```c
#include <string.h>
int main(void) { char text[4] = "a"; return strcmp(strncat(text, "bc", 1), "ab"); }
```

R1, the separate FLAC declaration gap:

```c
#include <string.h>
int main(void) { return strncasecmp("A", "a", 1); }
```

R2, network byte order on little-endian dotnet64-v1:

```c
#include <netinet/in.h>
int main(void) { return ntohl(0x01020304) != 0x04030201; }
```

R3, missing header/prototype configuration:

```c
extern char *getenv();
int main(void) { return getenv("PATH") == 0; }
```

The fuller independent [legacy header probe](../tests/c/portable-spec-getopt-header.c) also calls `strcmp` with only `stdio.h` included. Compile it with `-include string.h -include stdlib.h`, matching the scoped SPEC flag. Do not enable glibc macros or suppress implicit-declaration diagnostics to hide the issue.

R4, inverse hyperbolic and gamma exports (each call independently exposes its former missing symbol):

```c
#include <math.h>
int main(void) { return fabs(atanh(0.5) - 0.5493061443340548) > 1e-12; }
```

```c
#include <math.h>
int main(void) { return fabs(tgamma(5.0) - 24.0) > 1e-12; }
```

R5, file truncation:

```c
#include <stdio.h>
#include <unistd.h>
int main(void) { FILE *file = fopen("probe.tmp", "w+"); if (!file) return 1;
    int result = ftruncate(fileno(file), 2); fclose(file); remove("probe.tmp"); return result; }
```

R6, libc++ clock linkage:

```cpp
#include <chrono>
int main() { return std::chrono::system_clock::now().time_since_epoch().count() == 0; }
```

R7, time and directory entry points:

```c
#include <time.h>
int main(void) { return difftime(2, 1) != 1; }
```

```c
#include <unistd.h>
int main(void) { char path[4096]; return getcwd(path, sizeof(path)) == 0 || chdir(path); }
```

R8, a target-selected serial stub must supply its own types:

```c
#include <sys/types.h>
#if !defined(SPEC) || defined(SPEC_PTHREAD_STUBS_NEED_PTHREAD_TYPES)
typedef struct { void *storage; } pthread_mutex_t;
#endif
pthread_mutex_t mutex;
int main(void) { return 0; }
```

This reduced example fails with `-DSPEC` and succeeds with `-DSPEC -DSPEC_PTHREAD_STUBS_NEED_PTHREAD_TYPES`. It models the kit's type-selection condition, not an absence of portable pthread support: including the portable `pthread.h` directly already supplies real managed-thread types. The test is available as `-DPROBE=7` in the [C cause fixture](../tests/c/portable-spec-missing.c).

R9, the existing dynamic-constructor regression is [array-constructor.f90](../tests/fortran/array-constructor.f90). The reduced [nested constructor](../tests/fortran/portable-spec-constructor.f90) below is also checked in JIT/NativeAOT against native Flang. Its bitcode is inspected to require an actual call to `_FortranAInitArrayConstructorVector`; a simple fixed-trip constructor could bypass that helper:

```fortran
program constructor
  integer, volatile :: count = 3
  integer :: index, outer
  integer, allocatable :: values(:)
  values = [([(index, index=1,outer)], outer=1,count)]
  if (any(values /= [1,1,2,1,2,3])) stop 1
end program
```

## Remaining Cause Catalog

Run `bash tests/portable-spec-causes.sh` to verify the current expected rejections. Every check matches its intended diagnostic; an unexpected success fails the check and means this document needs updating. Expected-rejection checks are **not** SPEC passes. The following standalone code is independent of licensed sources. The C/C++ forms also exist as numbered probes in [portable-spec-missing.c](../tests/c/portable-spec-missing.c) and [portable-spec-missing.cpp](../tests/cpp/portable-spec-missing.cpp).

### H: Missing Headers And Platform Services

The smallest reproducer is a single include. A shared [header fixture](../tests/c/portable-spec-header.c) permits all variants without copying code:

```sh
bin/llvmnet --runtime=portable -c '-DPROBE_HEADER=<sys/ioctl.h>' tests/c/portable-spec-header.c -o /tmp/header.o
```

| ID | Substitute Header | Current Limitation |
| --- | --- | --- |
| H1 | `sys/ioctl.h` | Device/terminal request definitions and operations are absent. Native request numbers and layouts are not a portable implementation. |
| H2 | `sys/param.h` | GCC's host-oriented platform constants/header contract is absent. Supplying selected constants may be small, but the failed include is not evidence that GCC's remaining POSIX requirements are satisfied. |
| H3 | `complex.h` | No portable C complex-library header; complex arithmetic lowering alone does not supply the C complex library, including L6. |
| H4 | `locale.h` | No portable C locale API. This also blocks building a complete libc++ locale/stream implementation; a dummy C-locale facade is not equivalent to that implementation. |
| H5 | `malloc.h` | No host-specific allocation-extension header. Standard allocation exists in `stdlib.h`; host allocation inquiries are separate APIs. |
| H6 | `linux/limits.h` | Xalan selected Linux-specific platform code, but portable mode intentionally does not expose a Linux sysroot. Platform selection and required services need a portable port, not unrestricted host includes. |
| H7 | `sys/resource.h` | Resource-usage/limit structures and calls are absent. Native `rusage` layouts and process-limit policy need an explicit portable contract. |
| H8 | `machine/endian.h` | LLVM's host-feature selection falls into an endian-header branch absent from the generic portable target. Byte order is known, but importing a host platform profile would also assert unrelated OS facilities. |
| H9 | `omp.h` | No portable OpenMP headers/runtime. Supplying declarations alone only advances to A3. |

These are implementation/porting gaps, not evidence that managed execution cannot support these benchmarks. The audit does not enumerate failures hidden behind them. Fake platform macros, empty success stubs, or host struct declarations would make the diagnostic disappear without establishing compatible behavior.

### L1-L3: Libc++ Streams

The portable [libc++ configuration](../sysroot/dotnet64-v1/cxx-config/__config_site) sets `_LIBCPP_HAS_LOCALIZATION=0`. Stream definitions guarded by this setting are unavailable even though upstream headers are installed. Enabling the macro alone does not supply locale facets, globals, stream buffers, formatting, file adapters or their out-of-line implementations.

L1, output/input streams, C++ probe 1:

```cpp
#include <iostream>
int main() { std::cout << 1 << std::endl; }
```

L2, string streams, C++ probe 2:

```cpp
#include <sstream>
int main() { std::istringstream input("1"); int value; input >> value; return value != 1; }
```

L3, file streams, C++ probe 3:

```cpp
#include <fstream>
int main() { std::ifstream input("probe.txt"); return !input; }
```

Compile with `bin/llvmnet --runtime=portable -std=c++17 -O0 -DPROBE=1 tests/cpp/portable-spec-missing.cpp -o /tmp/probe.dll`, substituting 2/3 as needed. The expected errors are invalid stream operands/missing `endl` or undefined stream templates. This is a substantial **library implementation gap**, not a typed-CIL or threading prohibition. The system libstdc++ archive is built for a different ABI/library and cannot stand in for portable libc++.

### L4-L6: Extended And Complex Math

L4, even a bare libc++ complex include references undeclared `atan2l` (C++ probe 4):

```cpp
#include <complex>
int main() { return 0; }
```

L5, a direct extended-math call (C probe 4):

```c
#include <math.h>
long double probe(long double value) { return hypotl(value, value); }
int main(void) { return probe(1) < 1; }
```

Cactus also reports `powl`, `atan2l`, and other long-double operations; Gmsh additionally reports undeclared `fabsl`. Some frontend-lowered operations, including C++ `std::fabs(long double)`, already work. Do not describe all long-double operations as absent. dotnet64-v1 specifies Float80 long double in a 16-byte slot: implementing its transcendental library is work, but narrowing to `double` or forcing `-mlong-double-64` would violate the contract and lose precision/range.

L6, bypass the missing C header to isolate the unresolved complex exponential (C probe 5):

```c
extern double _Complex cexp(double _Complex);
int main(void) { return __real__ cexp(1.0) < 2.0; }
```

Use `-O0 -fno-builtin` to retain the call. This reproduces Fotonik3d's missing `cexp` export rather than H3. A portable implementation must preserve the complex calling convention and special values, signed zero, infinities and overflow; a naive `exp(real) * (cos(imag) + i*sin(imag))` is not a complete replacement. These math gaps are not inherent CIL impossibilities.

### L7: Libc++ Thread Lifecycle

```cpp
#include <thread>
int main() { std::thread worker([] {}); worker.join(); }
```

C++ probe 5 at O0 reports unresolved `_ZNSt3__16thread4joinEv`; XZ's optimized constructor reaches `_ZNSt3__115__thread_structC1Ev` first. The common cause is missing libc++ out-of-line thread lifecycle/TLS support, not lack of managed workers. The existing portable pthread implementation and its tests are separate. A complete repair must handle joining, detaching, cleanup and thread-exit state, not just add one mangled symbol returning success.

### P1-P3: Process And File Services

P1, shell execution (C probe 1):

```c
#include <stdlib.h>
int main(void) { return system(0); }
```

`system` is undeclared and unimplemented. The no-command probe is enough to expose the API; benchmarks may call it with commands. Supporting it requires explicit shell selection/availability, quoting, exit-status and signal semantics across hosts. It is a platform-service gap, not permission to silently execute benchmark shellouts through a native libc import. Returning zero would falsely report command success.

P2, descriptor replacement (C probe 2):

```c
#include <unistd.h>
int main(void) { return dup2(1, 2); }
```

P3, replacing a stream (C probe 3):

```c
#include <stdio.h>
int main(void) { return freopen("probe.txt", "w", stdout) == 0; }
```

These are missing APIs, not architectural impossibilities. Implementing them properly involves shared open-file descriptions, descriptor replacement, `FILE *` identity, buffering, ownership and standard streams. `fopen` plus `close`, or a new descriptor number, is not automatically equivalent. WRF also has the separately documented invalid missing-argument source call described in [SPEC coverage](spec-coverage.md); passing its present header blockers would not remove that boundary.

### A1: Fixed Plain-Char Contract

```c
int main(void) { return 0; }
```

Compile with `bin/llvmnet --runtime=portable -funsigned-char -DPROBE=0 tests/c/portable-spec-missing.c -o /tmp/char.dll`. The driver rejects the option before translation: it cannot change the versioned portable ABI. Blender requires unsigned plain char under the currently validated kit profile. Silently dropping the flag changes its behavior; accepting it changes a shared data contract. An explicit alternate ABI or source-compatible port is needed. This is a deliberate **dotnet64-v1 restriction**, not an intrinsic inability of CIL to represent unsigned bytes.

### A2: Typed Calls And Varargs

PALM declares a fixed four-argument `open` boundary, including a trailing length, whereas the portable runtime implements the declared C variadic API. C probe 9 reproduces the exact LLVM shape:

```c
extern int open(const char *, int, int, long);
int main(void) { return open("probe.txt", 0, 0, 9); }
```

With `--runtime=portable -O0 -fno-builtin`, this fails with `Runtime ABI mismatch for open: i32 (ptr, i32, i32, i64)`. Native SysV calls can happen to tolerate extra arguments and pass a mode in a register; a typed CIL call does not have that implicit ABI. The portable `open` implementation takes an explicit managed varargs frame. A correct bridge needs the actual prototype, argument packing and a justified treatment of the extra length; merely deleting arguments or reinterpreting an integer as a frame pointer is unsafe. This is a **boundary-contract restriction**, not proof PALM cannot run after an explicit ABI-correct adapter.

### A3: OpenMP Runtime Boundary

A header-free OpenMP program isolates the runtime requirement (C probe 6):

```c
int main(void) {
    int count = 0;
    #pragma omp parallel reduction(+:count)
    count += 1;
    return count < 1;
}
```

Compile with `--runtime=portable -O0 -fopenmp`; translation reports unresolved `__kmpc_fork_call`. Flang speed workloads often reach `__kmpc_global_thread_num` first. These are compiler runtime calls, not ordinary missing arithmetic operations.

The smallest native-boundary probe is the empty A1 program with `--runtime=portable --system-library libomp.so.5`; the driver rejects it with `--system-library requires --runtime=system`. System OpenMP uses native worker callbacks, TLS and runtime structures which cannot be silently mixed with the portable ABI. A portable OpenMP runtime or deliberately specified managed adapter is required. CIL threads are not fundamentally incapable of OpenMP semantics. Disabling OpenMP or stubbing `__kmpc_*` would only create a different serial workload, not fix the speed benchmark.

## CPU 2017, One Benchmark At A Time

Every benchmark name links to its latest retained harness log; the corresponding result table and exact make-log path are in the [current audit](../artifacts/tests/portable-spec/current.tsv). Errors in parallel make output can appear in a different order, so a row lists the controlling cause family and corroborated adjacent blockers rather than claiming a unique total order of source diagnostics. A failed row is not a claim that all downstream causes are known.

| Benchmark / Evidence | Result | Cause / Disposition |
| --- | --- | --- |
| [500.perlbench_r](../artifacts/spec-portable-audit/retry.Vmyt3WpR/500.perlbench_r-portable.log) | FAIL | R2 fixed; now H1 (`sys/ioctl.h`). |
| [502.gcc_r](../artifacts/spec-portable-audit/retry.Vmyt3WpR/502.gcc_r-portable.log) | FAIL | H2 (`sys/param.h`). |
| [507.cactuBSSN_r](../artifacts/spec-portable-audit/retry.Vmyt3WpR/507.cactuBSSN_r-portable.log) | FAIL | H3 and P3: complex header and `freopen`. |
| [508.namd_r](../artifacts/spec-portable-audit/retry.Vmyt3WpR/508.namd_r-portable.log) | FAIL | L1: stream output operators. |
| [510.parest_r](../artifacts/spec-portable-audit/retry.Vmyt3WpR/510.parest_r-portable.log) | FAIL | L4: libc++ complex header requires `atan2l`; existing C++03 portability setting retained. |
| [511.povray_r](../artifacts/spec-portable-audit/retry.w8MEt7oe/511.povray_r-portable.log) | FAIL | R7 fixed; now P1 (`system`, surfaced through libc++ `cstdlib`). |
| [520.omnetpp_r](../artifacts/spec-portable-audit/retry.Vmyt3WpR/520.omnetpp_r-portable.log) | FAIL | H4 and L1: locale and stream support. |
| [521.wrf_r](../artifacts/spec-portable-audit/retry.Vmyt3WpR/521.wrf_r-portable.log) | FAIL | P2 and H5: `dup2`, `malloc.h`; later invalid-call restriction remains unvalidated in portable. |
| [523.xalancbmk_r](../artifacts/spec-portable-audit/retry.Vmyt3WpR/523.xalancbmk_r-portable.log) | FAIL | H6: Linux platform header/selection mismatch. |
| [525.x264_r](../artifacts/spec-portable-audit/retry.7h7wFoK9/525.x264_r-portable.log) | PASS | R1-R3; clean SPEC execution and output validation. |
| [526.blender_r](../artifacts/spec-portable-audit/retry.Vmyt3WpR/526.blender_r-portable.log) | FAIL | A1: required unsigned-char profile conflicts with fixed portable ABI. |
| [527.cam4_r](../artifacts/spec-portable-audit/retry.Vmyt3WpR/527.cam4_r-portable.log) | FAIL | H7: `sys/resource.h`. |
| [538.imagick_r](../artifacts/spec-portable-audit/retry.Vmyt3WpR/538.imagick_r-portable.log) | FAIL | H4: locale API. |
| [541.leela_r](../artifacts/spec-portable-audit/retry.Vmyt3WpR/541.leela_r-portable.log) | FAIL | L2/L1: string streams and output. |
| [549.fotonik3d_r](../artifacts/spec-portable-audit/retry.Vmyt3WpR/549.fotonik3d_r-portable.log) | FAIL | L6: `cexp`. |
| [600.perlbench_s](../artifacts/spec-portable-audit/retry.Vmyt3WpR/600.perlbench_s-portable.log) | FAIL | R2 fixed; now H1. |
| [602.gcc_s](../artifacts/spec-portable-audit/retry.Vmyt3WpR/602.gcc_s-portable.log) | FAIL | H2; kit process concurrency does not fix missing headers. |
| [603.bwaves_s](../artifacts/spec-portable-audit/retry.Vmyt3WpR/603.bwaves_s-portable.log) | FAIL | A3: `__kmpc_global_thread_num`. |
| [607.cactuBSSN_s](../artifacts/spec-portable-audit/retry.Vmyt3WpR/607.cactuBSSN_s-portable.log) | FAIL | H3/P3 before OpenMP can be validated. |
| [619.lbm_s](../artifacts/spec-portable-audit/retry.Vmyt3WpR/619.lbm_s-portable.log) | FAIL | H9/A3: OpenMP header/runtime. |
| [620.omnetpp_s](../artifacts/spec-portable-audit/retry.Vmyt3WpR/620.omnetpp_s-portable.log) | FAIL | H4/L1. |
| [621.wrf_s](../artifacts/spec-portable-audit/retry.Vmyt3WpR/621.wrf_s-portable.log) | FAIL | P2/H5; no portable OpenMP or invalid-call pass inferred. |
| [623.xalancbmk_s](../artifacts/spec-portable-audit/retry.Vmyt3WpR/623.xalancbmk_s-portable.log) | FAIL | H6: Linux platform header/selection. |
| [625.x264_s](../artifacts/spec-portable-audit/retry.7h7wFoK9/625.x264_s-portable.log) | PASS | R1-R3; clean SPEC output validation under the four-thread kit setting. |
| [627.cam4_s](../artifacts/spec-portable-audit/retry.Vmyt3WpR/627.cam4_s-portable.log) | FAIL | H7 before parallel execution. |
| [628.pop2_s](../artifacts/spec-portable-audit/retry.Vmyt3WpR/628.pop2_s-portable.log) | FAIL | H9/A3. |
| [638.imagick_s](../artifacts/spec-portable-audit/retry.Vmyt3WpR/638.imagick_s-portable.log) | FAIL | H4. |
| [641.leela_s](../artifacts/spec-portable-audit/retry.Vmyt3WpR/641.leela_s-portable.log) | FAIL | L2/L3: string/file streams. |
| [644.nab_s](../artifacts/spec-portable-audit/retry.Vmyt3WpR/644.nab_s-portable.log) | FAIL | H9/A3. |
| [649.fotonik3d_s](../artifacts/spec-portable-audit/retry.Vmyt3WpR/649.fotonik3d_s-portable.log) | FAIL | A3 first; L6 remains a shared-source requirement. |
| [654.roms_s](../artifacts/spec-portable-audit/retry.Vmyt3WpR/654.roms_s-portable.log) | FAIL | A3. |
| [657.xz_s](../artifacts/spec-portable-audit/retry.Vmyt3WpR/657.xz_s-portable.log) | FAIL | H9/A3; distinct from CPU 2026's C++-thread XZ variant. |

## CPU 2026, One Benchmark At A Time

| Benchmark / Evidence | Result | Cause / Disposition |
| --- | --- | --- |
| [706.stockfish_r](../artifacts/spec-portable-audit/retry.h27Lx61b/706.stockfish_r-portable.log) | FAIL | L1: incomplete input streams. |
| [707.ntest_r](../artifacts/spec-portable-audit/retry.h27Lx61b/707.ntest_r-portable.log) | FAIL | L1. |
| [708.sqlite_r](../artifacts/spec-portable-audit/retry.h27Lx61b/708.sqlite_r-portable.log) | FAIL | H1: `sys/ioctl.h`. |
| [709.cactus_r](../artifacts/spec-portable-audit/retry.h27Lx61b/709.cactus_r-portable.log) | FAIL | L5: `hypotl`, `powl`, `atan2l` and related extended math. |
| [710.omnetpp_r](../artifacts/spec-portable-audit/retry.h27Lx61b/710.omnetpp_r-portable.log) | FAIL | L1. |
| [714.cpython_r](../artifacts/spec-portable-audit/retry.tpQHhnCY/714.cpython_r-portable.log) | FAIL | R8 fixed; now H4 in file utilities. |
| [721.gcc_r](../artifacts/spec-portable-audit/retry.h27Lx61b/721.gcc_r-portable.log) | FAIL | H2. |
| [722.palm_r](../artifacts/spec-portable-audit/retry.h27Lx61b/722.palm_r-portable.log) | FAIL | R4 fixed; now A2: fixed four-argument `open` versus portable varargs frame. |
| [723.llvm_r](../artifacts/spec-portable-audit/retry.h27Lx61b/723.llvm_r-portable.log) | FAIL | H8: host endian-header selection. |
| [727.cppcheck_r](../artifacts/spec-portable-audit/retry.h27Lx61b/727.cppcheck_r-portable.log) | FAIL | L1: `endl` and streams. |
| [729.abc_r](../artifacts/spec-portable-audit/retry.h27Lx61b/729.abc_r-portable.log) | FAIL | P1: shell execution. |
| [731.astcenc_r](../artifacts/spec-portable-audit/retry.h27Lx61b/731.astcenc_r-portable.log) | FAIL | L3/L1: file/input and output streams. |
| [734.vpr_r](../artifacts/spec-portable-audit/retry.h27Lx61b/734.vpr_r-portable.log) | FAIL | L1. |
| [735.gem5_r](../artifacts/spec-portable-audit/retry.h27Lx61b/735.gem5_r-portable.log) | FAIL | R5 fixed; now H4 in embedded CPython. |
| [736.ocio_r](../artifacts/spec-portable-audit/retry.h27Lx61b/736.ocio_r-portable.log) | FAIL | L1. |
| [737.gmsh_r](../artifacts/spec-portable-audit/retry.h27Lx61b/737.gmsh_r-portable.log) | FAIL | L4/L5 and L1: complex include, `fabsl`, streams. |
| [748.flightdm_r](../artifacts/spec-portable-audit/retry.h27Lx61b/748.flightdm_r-portable.log) | FAIL | L2/L1. |
| [749.fotonik3d_r](../artifacts/spec-portable-audit/retry.h27Lx61b/749.fotonik3d_r-portable.log) | FAIL | L6: `cexp`. |
| [750.sealcrypto_r](../artifacts/spec-portable-audit/retry.h27Lx61b/750.sealcrypto_r-portable.log) | FAIL | L1: incomplete output stream/ios types. |
| [753.ns3_r](../artifacts/spec-portable-audit/retry.h27Lx61b/753.ns3_r-portable.log) | FAIL | L1: input/output streams. |
| [765.roms_r](../artifacts/spec-portable-audit/retry.h27Lx61b/765.roms_r-portable.log) | PASS | R9: stale constructor blocker; clean current portable output validation. |
| [766.femflow_r](../artifacts/spec-portable-audit/retry.h27Lx61b/766.femflow_r-portable.log) | FAIL | L4/L1. |
| [767.nest_r](../artifacts/spec-portable-audit/retry.h27Lx61b/767.nest_r-portable.log) | FAIL | L1: incomplete stream definitions cause ambiguous operators. |
| [772.marian_r](../artifacts/spec-portable-audit/retry.h27Lx61b/772.marian_r-portable.log) | FAIL | L2: YAML string streams. |
| [800.pot3d_s](../artifacts/spec-portable-audit/retry.yaC9sHXJ/800.pot3d_s-portable.log) | FAIL | A3: host DO CONCURRENT lowers to OpenMP runtime calls. |
| [801.xz_s](../artifacts/spec-portable-audit/retry.yaC9sHXJ/801.xz_s-portable.log) | FAIL | R6 fixed; now L7: libc++ `__thread_struct` construction. |
| [803.sph_exa_s](../artifacts/spec-portable-audit/retry.yaC9sHXJ/803.sph_exa_s-portable.log) | FAIL | H9/A3. |
| [807.ntest_s](../artifacts/spec-portable-audit/retry.yaC9sHXJ/807.ntest_s-portable.log) | FAIL | L1 first; explicit OpenMP profile retained, not replaced by serial execution. |
| [809.cactus_s](../artifacts/spec-portable-audit/retry.yaC9sHXJ/809.cactus_s-portable.log) | FAIL | L5 before parallel execution. |
| [811.tealeaf_s](../artifacts/spec-portable-audit/retry.yaC9sHXJ/811.tealeaf_s-portable.log) | FAIL | H9/A3. |
| [816.nab_s](../artifacts/spec-portable-audit/retry.yaC9sHXJ/816.nab_s-portable.log) | FAIL | H9/A3. |
| [817.flac_s](../artifacts/spec-portable-audit/retry.yaC9sHXJ/817.flac_s-portable.log) | FAIL | R1 declaration fixed; now H4. |
| [820.cloverleaf_s](../artifacts/spec-portable-audit/retry.yaC9sHXJ/820.cloverleaf_s-portable.log) | FAIL | A3: Fortran OpenMP runtime. |
| [821.gcc_s](../artifacts/spec-portable-audit/retry.yaC9sHXJ/821.gcc_s-portable.log) | FAIL | H2; kit multiprocess execution does not remove library requirements. |
| [822.palm_s](../artifacts/spec-portable-audit/retry.yaC9sHXJ/822.palm_s-portable.log) | FAIL | R4 fixed; now A3 before the rate variant's A2 boundary is validated. |
| [823.llvm_s](../artifacts/spec-portable-audit/retry.yaC9sHXJ/823.llvm_s-portable.log) | FAIL | H8. |
| [827.cppcheck_s](../artifacts/spec-portable-audit/retry.yaC9sHXJ/827.cppcheck_s-portable.log) | FAIL | L1; kit `-j 4` setting retained. |
| [829.abc_s](../artifacts/spec-portable-audit/retry.yaC9sHXJ/829.abc_s-portable.log) | FAIL | P1. |
| [834.vpr_s](../artifacts/spec-portable-audit/retry.yaC9sHXJ/834.vpr_s-portable.log) | FAIL | L1. |
| [835.gem5_s](../artifacts/spec-portable-audit/retry.yaC9sHXJ/835.gem5_s-portable.log) | FAIL | R5 fixed; now H4. Kit still suppresses workers unconditionally. |
| [838.diamond_s](../artifacts/spec-portable-audit/retry.yaC9sHXJ/838.diamond_s-portable.log) | FAIL | L1 before any later CPU-query restrictions are tested. |
| [846.minizinc_s](../artifacts/spec-portable-audit/retry.yaC9sHXJ/846.minizinc_s-portable.log) | FAIL | L2: output string streams and formatting. |
| [849.fotonik3d_s](../artifacts/spec-portable-audit/retry.yaC9sHXJ/849.fotonik3d_s-portable.log) | FAIL | A3 first; L6 remains a shared-source requirement. |
| [853.ns3_s](../artifacts/spec-portable-audit/retry.yaC9sHXJ/853.ns3_s-portable.log) | FAIL | L1. |
| [854.graph500_s](../artifacts/spec-portable-audit/retry.yaC9sHXJ/854.graph500_s-portable.log) | FAIL | H9/A3; system register-width concessions are not portable evidence. |
| [857.namd_s](../artifacts/spec-portable-audit/retry.yaC9sHXJ/857.namd_s-portable.log) | FAIL | H9 and L1. |
| [865.roms_s](../artifacts/spec-portable-audit/retry.yaC9sHXJ/865.roms_s-portable.log) | FAIL | A3, unlike the rate pass. |
| [867.nest_s](../artifacts/spec-portable-audit/retry.yaC9sHXJ/867.nest_s-portable.log) | FAIL | L1. |
| [872.marian_s](../artifacts/spec-portable-audit/retry.yaC9sHXJ/872.marian_s-portable.log) | FAIL | L2. |
| [881.neutron_s](../artifacts/spec-portable-audit/retry.yaC9sHXJ/881.neutron_s-portable.log) | FAIL | H9/A3. |
| [998.specrand_s](../artifacts/spec-portable-audit/retry.yaC9sHXJ/998.specrand_s-portable.log) | FAIL | L1; one distinct benchmark despite two group appearances. |
| [999.specrand_r](../artifacts/spec-portable-audit/retry.yaC9sHXJ/999.specrand_r-portable.log) | FAIL | L1; one distinct benchmark despite two group appearances. |

## Verification And Next Work

The [positive repair gate](../artifacts/tests/portable-spec/functions.log), [expected-cause gate](../artifacts/tests/portable-spec/causes.log), and nearby [math](../artifacts/tests/portable-spec/math.log), [I/O](../artifacts/tests/portable-spec/regression-io.log), [POSIX](../artifacts/tests/portable-spec/regression-posix.log), [time](../artifacts/tests/portable-spec/regression-time.log) and [portable C++](../artifacts/tests/portable-spec/regression-portable-cpp.log) suites passed. The positive repair gate is registered in [toolchain-all.sh](../tests/toolchain-all.sh). The entire core/deployment/media gate was **not** rerun for this audit; earlier full-video/deployment evidence is not relabeled as using these changes.

The largest remaining project is a real portable libc++ locale/stream runtime. Next are a deliberate POSIX compatibility profile, complex/Float80 math and portable OpenMP plus libc++ thread lifecycle support. Treat A1/A2 as explicit ABI design work. Continue requiring native-oracle tests, real worker evidence for parallel claims and SPEC output validation after each repair; do not turn the remaining 81 failures into passes by changing the workload, borrowing the host ABI, or providing success stubs.