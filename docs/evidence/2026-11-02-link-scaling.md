# Evidence: bitcode link scaling (TC-20), main from archives and archives under any name (TC-19) (2026-11-02)

Build host: Linux x86-64 (Debian 13, kernel 6.12), AMD Ryzen AI MAX+ 395 (16 cores, 32 threads), 124 GB RAM, .NET SDK 10.0.403, LLVM/Clang/Flang 22.1.8. The open-source library survey of 2026-11-01/02 (18 libraries built with llvmnet, kept outside the repository under `artifacts/oss/`) found three problems in the driver's in-process bitcode link (`native/linker.cpp`): link time and memory that grow super-linearly with a C++ program (TC-20), `main` in an archive member not being linked, and archives not named `*.a` being parsed as textual IR (both TC-19). This page records the cause, the fix and the timings before and after. "Before" is commit `a5ab005`; "after" is the commit that adds this page. Both were built in the same worktree against the same sysroot, and each pair of numbers was taken one after the other (the cjxl link-and-translate pair side by side) on a workstation that other sessions were also using (load average about 5 to 10), so single numbers carry a few percent of noise.

## Cause

The linker read every archive member in full (`parseBitcodeFile`) and linked each input with the static `llvm::Linker::linkModules`, which builds a new `IRMover` per call. An `IRMover` indexes every struct type of the destination module when it is constructed (`TypeFinder::run` over every global, instruction and metadata node), so each input cost time proportional to everything linked before it, and a link of N inputs cost O(N × program size).

Timing instrumentation of a scratch copy of the old linker on libjxl's `cjxl` (one 7.5 MB object, `cjxl-r.o` from the survey's `libjxl-repro/link-scaling/`, linked against the portable sysroot with `-emit-llvm`; `perf` is not available on this host):

| Phase | Time (s) |
| --- | ---: |
| Reading all 1,520 sysroot archive members in full | 1.14 |
| Linking the object | 0.24 |
| Linking the 362 needed members, about 63 ms each | 22.80 |
| Rescanning the archives (6 passes) | 0.02 |
| Verifying and writing the module | 0.63 |

The same copy with one `llvm::Linker` for the whole link linked the 362 members in 0.10 s.

## Fix

- One `llvm::Linker` serves the whole link, as `llvm-link` and LLVM's LTO use one `IRMover`; its struct-type index then grows with the module instead of being rebuilt per input.
- Objects and archive members are read lazily (`getLazyIRModule`, `getLazyBitcodeModule`): a function body is read only when its definition is linked, so members that are never needed and the copies of inline functions that every C++ object carries cost little more than their symbol tables. A lazily read module gets the `UpgradeDebugInfo` step the reader applies to a module read in full, as `llvm-link` does.
- The record of which file defined each symbol, used to name both files of a duplicate definition, keeps an index into a file list instead of a copy of the path per symbol.
- The archive rescanning keeps its order and rules, so the same members are linked in the same order as before.

The translated output is unchanged: the linked module differs only in which of several structurally identical struct types a global is declared with (the persistent `IRMover` merges isomorphic types the per-input ones kept apart), and the translator maps aggregates by layout. Every `program.dll` below is byte-identical before and after.

## Timings (TC-20)

`llvmnet-clang++` from this worktree (`LLVMNET_ROOT` set to it), wall time and peak RSS from `/usr/bin/time`, each in a `systemd-run --user --scope -p MemoryMax=24G` scope. The inputs are the survey's build trees: libjxl's `cjxl-r.o`; Skia's DM and nanobench from `artifacts/oss/build/skia-portable` (the objects and archives `ninja -t inputs <exe>` lists, linked with `-O3 -emit-llvm` as `artifacts/oss/scripts/skia-link-split.sh` does, then the bitcode translated with `-O3`); HarfBuzz's `hb-shape-fuzzer` with the link command meson wrote; ANGLE's `angle_unittests` through the survey's compiler wrapper (`artifacts/oss/scripts/angle-cc.sh`).

| Link | Before (s) | Before peak (MB) | After (s) | After peak (MB) | Translated output |
| --- | ---: | ---: | ---: | ---: | --- |
| cjxl, load only (`-nodefaultlibs -nostartfiles -emit-llvm`) | 0.96 | 268 | 0.97 | 274 | |
| cjxl with the sysroot (`-emit-llvm`) | 24.56 | 942 | 1.69 | 437 | |
| cjxl, link and translate (`-O2`) | 30.88 | 949 | 5.99 | 492 | identical (`bc89f72f…`) |
| `llvm-link-22 cjxl-r.o`, for scale | 0.92 | 263 | | | |
| Skia DM bitcode link (2,267 objects, 20 archives, 136 MB of bitcode) | 419.57 | 2,352 | 22.57 | 1,382 | |
| Skia DM translation of that bitcode | 26.90 | 1,997 | 22.42 | 1,500 | identical (`23e02dd4…`) |
| Skia nanobench bitcode link (1,919 objects, 19 archives) | 281.54 | 2,090 | 8.26 | 1,120 | |
| Skia nanobench translation of that bitcode | 19.37 | 1,585 | 16.00 | 1,149 | identical (`d404bf2f…`) |
| HarfBuzz `hb-shape-fuzzer` (`-emit-llvm`) | 13.68 | 866 | 1.05 | 334 | |
| HarfBuzz `hb-shape-fuzzer`, link and translate | 15.72 | 889 | 3.42 | 365 | identical (`cc472d44…`) |
| ANGLE `angle_unittests` (`-emit-llvm`) | 100.30 | 1,420 | 5.85 | 807 | |
| ANGLE `angle_unittests`, link and translate | 119.85 | 1,521 | 21.07 | 968 | identical (`ef13d2b8…`) |

The "before" link and translation of `angle_unittests` overlapped the start of the gate run below, so it is the noisiest number here; its `-emit-llvm` link did not.

The same instrumentation on the new linker splits Skia DM's 17.4 s link (another run) into 11.2 s for the 2,267 objects (`llvm-link-22` of the objects alone took 10.7 s in the survey), 2.6 s for the 438 archive members linked out of 4,326 read, 0.14 s for the six rescans, and 2.9 s to verify and write the module. A large C++ link now costs about what translating the program costs, and no phase grows faster than the input.

## Main from an archive and archives under any name (TC-19)

- An archive member that defines `main` (gtest_main, Catch2's main) was never linked: crt1.o references `main` natively, but llvmnet's start code calls it from the CIL entry point, so nothing in the module referenced it ("No main function found"). An executable's link now treats its entry point (`main`, or the Fortran main program `_QQmain`) as referenced while nothing defines it; a library and a `-r` link have no entry point. Since an executable can now take everything from archives, the "at least one explicit object as the link root" check applies to libraries and `-r` links only.
- An archive named other than `*.a` (`libx.so`, `x.lib`, also through `-l:libx.so`) was parsed as textual LLVM IR ("expected integer"). The linker recognizes an archive by its content (`identify_magic`, thin archives included), and the driver classifies `.o` and `.a` inputs by content like every other name.

The survey's reproducers pass with this build: `libjxl-repro/main-in-archive/run.sh` links (exit 0) and `angle-repro/archive-any-name/run.sh` reports "links and runs" for `./libx.a`, `./libobj.so`, `./libx.so`, `./libx.lib` and `-L. -l:libx.so`. ANGLE's `angle_unittests` above was linked with `libfeature_support.so` passed as it is, without the survey wrapper's rename to `*.llvmnet.a`.

Tests: `tests/link-inputs.sh` (both modes) links archives named `.so`, `.lib` and `.o`, a bitcode object named `.a`, a thin archive and `-l:libexample.so`, a `--whole-archive` archive named `.lib`, `main` from an archive member with the program's object, an executable from archives alone, keeps an archive's `main` out when the program defines one, and checks that `-r` does not extract `main`; `tests/fortran.sh` links a Fortran main program from an archive; the unit test `FilesThatNameNoLanguageAreLinkedByTheirContent` covers thin archives and a bitcode object named `.a`.

## Gate

The pull-request gate on this change, `systemd-run --user --scope -p MemoryMax=24G -p MemorySwapMax=0 tests/run.sh --tag core --tag toolchain --exclude-tag slow`: 94 passed, 0 failed, 0 skipped in 379 s. Because `Driver.cs` is one of the sysroot's inputs, the run first rebuilt the portable and system sysroots, which links them with the new linker.
