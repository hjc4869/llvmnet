# Evidence: PF-16, the portable mmap emulation for many small mappings (2026-11-02)

Host as in the [library survey](2026-11-02-library-survey.md): AMD Ryzen AI MAX+ 395 (16 cores, 32 threads), 124 GB, Linux 6.12.113 with transparent huge pages `always`, .NET 10.0.14, clang 22.1.8. Other sessions ran gates and NativeAOT publishes on the host throughout (load average 3 to 24), so every comparison alternates the builds it compares (`scripts/ab-time.py`), and times are medians of 6 to 12 runs unless marked as minima. The programs are the survey's JIT builds (`artifacts/oss`): the same `program.dll` is run with the old `LlvmNet.Runtime.dll` (commit `e77a3e0`) and with the new one, so only the runtime differs. The microbenchmarks are `artifacts/oss/micro/pages.c` (200,000 one-page anonymous mappings, each touched once, then unmapped), `pages2.c` (the same with timings, unmapped in ascending and then in descending order) and `alloc.c` (8 million small `malloc` nodes kept alive, then freed).

## The change

`src/LlvmNet.Runtime/Host/HostMemory.cs` emulates `mmap`, `munmap`, `mremap`, `msync` and `madvise` for musl in portable mode with .NET APIs only. Before, every mapping was its own `NativeMemory.AllocZeroed(size + 4096)` block, aligned up to a page, kept in a `SortedList<nint, Mapping>`. Now:

- **Slabs.** Mappings of up to 32 pages take pages from 32 MB slabs. 128 KB is mallocng's threshold for mapping an allocation by itself, so slabs hold its groups, and allocations above the threshold keep a block of their own as before. A slab is one `AllocZeroed` call of 32 MB and a page, more than the 32 MB above which glibc's `malloc` always maps a block directly, so a slab is lazily zeroed memory that `free` returns to the system. A one-page mapping costs one page.
- **Free spans.** Free slab pages form spans with boundary tags, kept in exact-length lists for 1 to 32 pages and one list for longer spans. `mmap` takes the shortest list that fits, then any longer span, then a new slab. `munmap` merges the freed pages with free neighbours. A bitmap records the pages handed out since the slab was allocated, and only those are cleared before reuse, so pages no mapping has used never become resident. An empty slab is freed when another slab is already empty.
- **Page tables.** Every slab and block records the mapping of each of its pages, and the regions are a sorted array searched by binary search. `mmap` and `munmap` cost a binary search plus the pages they touch, in any order. Each `mmap` still gets a `Mapping`, so `StackSizeFor` (thread stack sizes) sees the length musl mapped.
- **Semantics.** These are unchanged:
  - file mappings are copies: private ones read the file once, shared writable ones write back on `msync` and `munmap`;
  - the budget (RLIMIT_AS, RLIMIT_DATA, the memory .NET reports) and ENOMEM.

  Four details now follow Linux more closely:
  - `mremap` grows an anonymous mapping in place into free slab pages that follow it, with or without `MREMAP_MAYMOVE`, and works on any part of a mapping;
  - `mremap` with an old length of 0 returns EINVAL;
  - `MAP_FIXED` replaces only pages that one private mapping still maps (before, it also wrote into pages that had already been unmapped);
  - a new file mapping is filled before another thread's `munmap` can reach it.

A review of the change found that a growth of 2^44 bytes or more wrapped the page count passed to the in-place growth. It is checked first now (`HostMemoryTests.RemapBeyondThePageCountFailsWithoutTouchingTheMapping`).

## The four measured causes

1. **`brk` reports no heap.** Not a cause, and left as it is. mallocng takes every group from `mmap`, and uses `brk` only for its metadata areas, one page per about 100 groups (`alloc_meta` in musl 1.2.5). For the libxml2 DOM parse, portable mode makes 158,637 `mmap` calls, 146,244 of them for one page. The native musl build, whose `brk` works (2,786 calls), makes 158,628 and 146,244 (strace). With one page per small mapping there is nothing left for a heap to save.
2. **The alignment page.** Gone for small mappings. Blocks keep it, at most 1/33 of a block.
3. **The sorted table.** Replaced by the page tables (tables below).
4. **`MADV_DONTNEED` keeps pages resident.** Unchanged, and not on mallocng's path: musl 1.2.5 builds mallocng with `USE_MADV_FREE 0` and never calls `madvise`. A program that discards pages itself still gets them cleared rather than released, because releasing pages inside a block needs an operating-system call (`madvise`, `VirtualFree`), and the host layer uses .NET APIs only. Memory returns to the system when a block is unmapped or a slab empties.

## The JIT's call-counting delay

The first version of the allocator made xmllint's streaming parse (`--stream`) 6 to 13% slower on Linux, Windows and macOS, although its 2,548 `mmap` and 2,541 `munmap` calls cost the same 8 ms as before. The extra time was spread over the parser and `memmove`.

The cause is tiered compilation. .NET starts promoting hot methods from Tier0 to optimized code only after 100 ms, checked on 100 ms ticks, in which no method has been called for the first time, whether it is jitted or ReadyToRun code. The first block freed (`ReleaseBlockPages`, `RemoveRegion`, `List<nint>`, `NativeMemory.Free`) and the first block above 1 MB (`Array.Fill` of its bitmap, which instantiates `Vector<ulong>`) restarted that delay at 198 and 294 ms. So the first optimized method appeared at 397 to 496 ms instead of 297 ms (EventPipe `MethodLoadVerbose` events in 4 runs of each build), and the program's hot code ran unoptimized 100 to 200 ms longer.

With `DOTNET_TC_CallCountingDelayMs=0` both runtimes took the same time (0.957 and 0.967 s). The released code avoids the late first calls:
- slab and block pages are released by one method, compiled on the first `munmap`;
- blocks track unmapped pages in a bitmap that starts zeroed, so there is no `Array.Fill`;
- slab pages are cleared with `NativeMemory.Fill`, which `memset` calls from the start;
- `NativeMemory.Free` is called once at start-up.

The first optimized method now appears at 297 to 298 ms (6 runs), as with the old runtime. Any code path that a run reaches for the first time late can cost this delay, so a runtime path added later should keep its rare branches inside methods that run early.

## Measurements on Linux x86-64

`pages2.c`, seconds as printed by the program, minimum of 3 rounds (map: the first loop of 200,000 `mmap` calls and touches):

| live mappings | native map / ascending / descending | before | after |
| --- | --- | --- | --- |
| 50,000 | 0.050 / 0.062 / 0.039 | 0.045 / 0.200 / 0.014 | 0.017 / 0.008 / 0.007 |
| 100,000 | 0.097 / 0.125 / 0.087 | 0.087 / 0.775 / 0.031 | 0.031 / 0.015 / 0.014 |
| 200,000 | 0.189 / 0.279 / 0.176 | 0.187 / 3.269 / 0.062 | 0.068 / 0.034 / 0.032 |

Unmapping is linear in both orders, and every phase is faster than the kernel's.

Whole programs, median seconds and peak RSS (GNU time):

| | native glibc | native musl | system mode | portable before | portable after |
| --- | --- | --- | --- | --- | --- |
| `pages` 200,000 mappings | 0.472 s, 782 MB | | | 3.569 s, 1,648 MB | 0.145 s, 837 MB |
| `alloc` 8 million nodes | 0.283 s | 0.672 s | | 1.070 s, 767 MB | 0.920 s, 700 MB |
| xmllint DOM parse, 100 MB | 0.775 s, 787 MB | 1.324 s, 810 MB | 1.483 s, 837 MB | 3.405 s, 1,511 MB | 1.251 s, 882 MB |
| xmllint streaming parse | 0.546 s | 0.692 s | 1.252 s, 110 MB | 1.102 s, 114 MB | 1.130 s, 112 MB |

One-page mappings now cost one page each: 837 MB for 200,000, against 782 MB natively. The difference is mostly the .NET runtime and its generated code (about 28 MB, PF-11), the `Mapping` objects (about 13 MB) and the page tables (16 bytes a page).

The DOM parse is 2.7 times faster with 42% less memory. It is now faster than system mode (glibc's `malloc` through the bridges) and than native musl, and uses 45 MB more than system mode.

The streaming parse is unchanged. This run measured 1.102 and 1.130 s at load 20; a 10-round A/B at load 5 measured 1.070 s before and 1.066 s after. Outputs (`--format` digest, XPath count) are identical to native for both runtimes.

ffmpeg's decode harness (`artifacts/ffmpeg-browser-portable/decode.dll`), with frame hashes identical to native for both runtimes. It is unchanged:

| decode | before | after |
| --- | --- | --- |
| HEVC 1080p, 1 thread | 3.553 s | 3.494 s |
| HEVC, 4 threads | 1.607 s | 1.616 s |
| H.264 1080p, 1 thread | 3.596 s | 3.473 s |
| H.264, 4 threads | 1.540 s | 1.480 s |

The 4-thread HEVC decode used 155 MB before and 154 MB after.

## Windows and macOS

The same programs, old and new runtime, with the document copied to each host. Minimum of 3 rounds; outputs identical to native on both hosts.

| | Windows x64 (.NET 10.0.10) before | after | macOS ARM64 (.NET 10.0.14) before | after |
| --- | --- | --- | --- | --- |
| DOM parse | 4.118 s, 1,511 MB | 1.387 s, 870 MB | 3.660 s, 1,541 MB | 0.726 s, 947 MB |
| streaming parse | 1.201 s, 100 MB | 1.154 s, 97 MB | 0.656 s, 139 MB | 0.661 s, 150 MB |
| `pages` 200,000 | 4.272 s | 0.230 s | 4.722 s, 1,655 MB | 0.107 s, 860 MB |
| `pages2` 200,000, ascending unmap | 3.636 s | 0.057 s | 4.553 s | 0.020 s |
| `alloc` 8 million | 1.331 s | 1.172 s | 0.838 s, 720 MB | 0.687 s, 721 MB |

Memory is the peak working set on Windows and the maximum resident set on macOS. On Windows the old runtime was also slow to unmap in descending order (1.96 s), the new one takes 0.034 s.

On Linux ARM64 (`mcr.microsoft.com/dotnet/runtime:10.0` under qemu), `pages2` and `alloc` give the same output with both runtimes; times under emulation are not meaningful.

## Checks

- **Unit tests.** `tests/LlvmNet.UnitTests/HostMemoryTests.cs`, 9 tests, through the system-call entry point musl uses:
  - one-page mappings are distinct and zeroed, and reused pages are zeroed again;
  - unmapping 200,000 mappings in ascending order costs at most 8 times as much as 50,000, plus 0.5 s;
  - empty slabs are freed, except one;
  - partial `munmap` of slab and block mappings, `MADV_DONTNEED`, and EFAULT after unmapping;
  - `MAP_FIXED` and `MAP_FIXED_NOREPLACE`;
  - `mremap` growing in place, moving and shrinking;
  - oversized `mremap`;
  - invalid arguments.

  Against the old implementation, three tests fail:
  - the linear-cost test (0.52 s for 50,000, 7.1 s for 200,000);
  - in-place growth;
  - `MAP_FIXED` over an unmapped hole.

  All 139 unit tests pass.
- **Gates.** `tests/run.sh --tag core --tag toolchain --exclude-tag slow` passed 94 of 94 in 717 s, in a `MemoryMax=24G` scope.
- **libc-test.** `tests/libc-conformance.sh`, in a `MemoryMax=24G` scope: functional 60 of 67, regression 59 of 67, math 192 of 195, api 74 of 74. These are the same 18 failing tests as the last run in the main checkout: `fork`, `vfork` and `exec`, System V IPC, sockets, `pthread_cancel`, the floating-point environment, `sem_open` and `sem_close`, `sigaltstack`, `exp10l` and `pow10l`, and `%La` of an invalid `long double`.
- **Benchmarks.** `scripts/benchmark.py --group micro --group suite --group startup` over 53 benchmarks (`artifacts/bench/pf16-after`). The ffmpeg group needs integration builds this worktree does not have, and ffmpeg is measured above.
  - **Geometric means.** 0.943 of the baseline under the JIT and 0.945 as NativeAOT, host-normalized.
  - **Allocation-heavy programs.** Alternating runs of the same bitcode, built with the old and the new runtime:

    | program | JIT before | JIT after | NativeAOT before | NativeAOT after |
    | --- | --- | --- | --- | --- |
    | olden-health | 0.277 s | 0.191 s | 0.172 s | 0.093 s |
    | olden-treeadd | 0.188 s | 0.154 s | 0.094 s | 0.050 s |
    | olden-voronoi | 0.488 s | 0.472 s | 0.203 s | 0.193 s |
  - **The one flag.** During a load peak (load average above 20), the harness flagged sha as NativeAOT at 1.30 times its baseline, a 13 ms run. It does not reproduce:
    - built as NativeAOT with the old and with the new runtime, the same bitcode takes 11.2 and 11.4 ms (median of 60 alternating runs, fastest 9.2 ms for both);
    - `--only sha` measures 1.05.
  - **Others above 1.10.** lpbench (1.13 under the JIT, 1.17 as NativeAOT), susan and jpeg as NativeAOT (1.19 and 1.11). All are under the 1.25 threshold, and with the old runtime they take the same time in alternating runs:
    - susan: 17 and 17 ms;
    - jpeg: 4 and 4 ms;
    - lpbench: 1.34 and 1.36 s under the JIT, 1.26 and 1.22 s as NativeAOT.

## What this does not show

- **`MADV_DONTNEED` and pages freed inside a slab.** Both stay resident until the slab empties: the host layer cannot release part of a block without an operating-system call.
- **macOS streaming parse.** Its maximum resident set grew by 11 MB (139 to 150 MB), which was not investigated. Windows and Linux did not grow.
- **The libraries' own suites.** They were not rerun with the new runtime. libxml2's outputs, ffmpeg's frame hashes and the gates' programs were compared instead.
