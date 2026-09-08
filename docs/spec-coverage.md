# Optimized SPEC Coverage

## Portable Failure Audit

The subsequent [portable limitation audit](spec-portable-limitations.md) rebuilt all **84 distinct portable failures** from the ordered inventory, fixed bounded string, byte-order, math, file, time, libc++ clock and kit-configuration gaps, and retained a per-benchmark cause with minimal independent reproducers. **525.x264_r, 625.x264_s and 765.roms_r now pass portable SPEC output validation; 81 remain failed.** ROMS's historical constructor failure was already fixed in the current runtime. The 17 earlier portable passes were not rerun, so the combined 20 distinct validated entries are not a clean final-toolchain suite pass. The audit distinguishes unfinished portable library support from deliberate ABI restrictions; most failures are not fundamental CIL limitations.

## Ordered Compatibility Follow-Up

The repair-first follow-up on 2026-09-07/08 uses [../scripts/spec-ordered.sh](../scripts/spec-ordered.sh). It processes one benchmark at a time in this order: 2017 integer rate; 2026 integer rate; 2026 FP rate; 2026 integer speed; 2026 FP speed; 2017 FP rate; 2017 integer speed; 2017 FP speed. Entries within each group are ascending, using the installed kit's benchmark sets, including their random-number checks. Rate runs use one instance. Speed uses the kit's four-thread setting and enables its OpenMP source paths where applicable, with an explicit system LLVM OpenMP runtime. Some speed entries use kit-managed parallel processes instead of worker threads.

Both ABIs are attempted for each entry. The progression gate is successful **system-ABI execution and SPEC output validation**; portable failures are retained and are not called passes. Set `SPEC_REQUIRED_ABIS=both` to require both before advancing. All results below are scalar O3, NativeAOT, test inputs, non-reportable. Licensed sources remain unchanged. A tested, explicitly selected upstream libstdc++ bitcode archive supplies system C++ library implementations; it is not a native C++ fallback and does not extend the portable libc++ profile.

| Group Validated | System | Portable | Scope |
| --- | ---: | ---: | --- |
| 2017 integer rate | 11 PASS | 5 PASS / 6 FAIL | Every installed entry in this group, including 999.specrand_ir |
| 2026 integer rate | 15 PASS | 1 PASS / 14 FAIL | Every installed entry in this group, including 999.specrand_r |
| 2026 FP rate | 13 PASS | 1 PASS / 12 FAIL | Every installed entry in this group, including 999.specrand_r |
| 2026 integer speed | 14 PASS | 14 FAIL | Every installed entry, with the four-thread harness setting and kit-defined concurrency exceptions |
| 2026 FP speed | 14 PASS | 14 FAIL | Every installed entry, with OpenMP and host DO CONCURRENT lowering enabled where applicable |
| 2017 FP rate | 14 PASS | 5 PASS / 9 FAIL | Every installed entry, including 997.specrand_fr |
| 2017 integer speed | 11 PASS | 4 PASS / 7 FAIL | Every installed entry, with the four-thread harness setting |
| 2017 FP speed | 11 PASS | 1 PASS / 10 FAIL | Every installed entry, including 996.specrand_fs, with the four-thread harness setting |

All eight requested groups completed system validation: **103 group entries, comprising 53 rate and 50 speed**. They cover all **101 distinct installed benchmarks**, 47 from CPU 2017 and 54 from CPU 2026. The shared CPU 2026 specrand checks appear in both corresponding integer/FP groups. Portable results are **17 PASS / 86 FAIL** across the group entries, or 17 PASS / 84 FAIL after deduplicating those shared checks. These evolving-toolchain results are not a clean rerun of every benchmark with one final compiler.

Speed concurrency is benchmark-specific: GCC and LLVM use kit-managed parallel processes, Cppcheck receives `-j 4`, and Ntest requires explicit OpenMP activation. The installed Gem5 speed metadata unconditionally suppresses threading, despite accepting a four-thread harness setting. Its source is not modified to manufacture a parallel run; any successful Gem5 result is execution/output validation only, not four-worker evidence.

Evidence is retained in the progress tables under:

- [../artifacts/spec-ordered/ordered-test.jbfL7Wfd/progress.tsv](../artifacts/spec-ordered/ordered-test.jbfL7Wfd/progress.tsv): Perl, GCC and mcf; stopped at 520.
- [../artifacts/spec-ordered/ordered-test.Br1jKyZQ/progress.tsv](../artifacts/spec-ordered/ordered-test.Br1jKyZQ/progress.tsv): repaired 520; stopped at 523.
- [../artifacts/spec-ordered/ordered-test.v6g9929q/progress.tsv](../artifacts/spec-ordered/ordered-test.v6g9929q/progress.tsv): repaired 523; stopped at 525.
- [../artifacts/spec-ordered/ordered-test.8dsWjEaQ/progress.tsv](../artifacts/spec-ordered/ordered-test.8dsWjEaQ/progress.tsv): 525 through 999; initial 2026 config rejection.
- [../artifacts/spec-ordered/ordered-test.OFEnrXXP/progress.tsv](../artifacts/spec-ordered/ordered-test.OFEnrXXP/progress.tsv): repaired 706; stopped at 707.
- [../artifacts/spec-ordered/ordered-test.YDzdl7AR/progress.tsv](../artifacts/spec-ordered/ordered-test.YDzdl7AR/progress.tsv): repaired 707; stopped at 708.
- [../artifacts/spec-ordered/ordered-test.LL6HHfSw/progress.tsv](../artifacts/spec-ordered/ordered-test.LL6HHfSw/progress.tsv): repaired 708; stopped at 710.
- [../artifacts/spec-ordered/ordered-test.cns40WN5/progress.tsv](../artifacts/spec-ordered/ordered-test.cns40WN5/progress.tsv): repaired 710; stopped at 714.
- [../artifacts/spec-ordered/ordered-test.bkYGIRKc/progress.tsv](../artifacts/spec-ordered/ordered-test.bkYGIRKc/progress.tsv): repaired 714 and 721; stopped at a 600-second LLVM timeout.
- [../artifacts/spec-ordered/2026-int-rate/723.llvm_r-system-validation.log](../artifacts/spec-ordered/2026-int-rate/723.llvm_r-system-validation.log): LLVM passed after adding shared-pointer and exact native CPU-query support. This focused retry reused unchanged-flag objects with SPEC's non-reportable `make_no_clobber` mode; harness execution and output validation completed in 408 seconds.
- [../artifacts/spec-ordered/ordered-test.mQZ5UYlG/progress.tsv](../artifacts/spec-ordered/ordered-test.mQZ5UYlG/progress.tsv): 727 and 729 passed; stopped at VPR.
- [../artifacts/spec-ordered/2026-int-rate/734.vpr_r-system-regex.log](../artifacts/spec-ordered/2026-int-rate/734.vpr_r-system-regex.log): VPR passed after vector-invoke legalization and upstream regex support, using the same explicit non-reportable build-reuse mode.
- [../artifacts/spec-ordered/2026-int-rate/735.gem5_r-system-filesystem.log](../artifacts/spec-ordered/2026-int-rate/735.gem5_r-system-filesystem.log): gem5 passed after namespace portability, exception-pointer, demangler and filesystem support.
- [../artifacts/spec-ordered/ordered-test.3xVaoF3y/progress.tsv](../artifacts/spec-ordered/ordered-test.3xVaoF3y/progress.tsv): Sealcrypto passed; stopped at NS-3.
- [../artifacts/spec-ordered/2026-int-rate/753.ns3_r-system-bswap.log](../artifacts/spec-ordered/2026-int-rate/753.ns3_r-system-bswap.log): NS-3 passed after native complex-helper resolution and 48-bit byte swaps.
- [../artifacts/spec-ordered/ordered-test.GiaEG6KN/progress.tsv](../artifacts/spec-ordered/ordered-test.GiaEG6KN/progress.tsv): zstd, specrand and Cactus passed; stopped at PALM.
- [../artifacts/spec-ordered/2026-fp-rate/722.palm_r-system-format-relink.log](../artifacts/spec-ordered/2026-fp-rate/722.palm_r-system-format-relink.log): PALM passed after the Fortran runtime additions and a fresh format-parser relink.
- [../artifacts/spec-ordered/ordered-test.lpHpH2Ai/progress.tsv](../artifacts/spec-ordered/ordered-test.lpHpH2Ai/progress.tsv): ASTC, OCIO, Gmsh and FlightDM passed; stopped at Fotonik3d.
- [../artifacts/spec-ordered/2026-fp-rate/749.fotonik3d_r-system-complex-io.log](../artifacts/spec-ordered/2026-fp-rate/749.fotonik3d_r-system-complex-io.log): Fotonik3d passed after derived pointer allocation and complex formatted I/O.
- [../artifacts/spec-ordered/2026-fp-rate/765.roms_r-system-tabs.log](../artifacts/spec-ordered/2026-fp-rate/765.roms_r-system-tabs.log): ROMS passed after array-constructor/TRANSFER support and record-relative format tabs.
- [../artifacts/spec-ordered/2026-fp-rate/766.femflow_r-system-extended.log](../artifacts/spec-ordered/2026-fp-rate/766.femflow_r-system-extended.log): Femflow passed in 852 seconds with an extended build budget; the earlier 600-second timeout remains incomplete evidence.
- [../artifacts/spec-ordered/ordered-test.HNqifHV6/progress.tsv](../artifacts/spec-ordered/ordered-test.HNqifHV6/progress.tsv): NEST passed; stopped at Marian.
- [../artifacts/spec-ordered/2026-fp-rate/772.marian_r-system-random-device.log](../artifacts/spec-ordered/2026-fp-rate/772.marian_r-system-random-device.log): Marian passed after upstream future/futex/random-device units, primary-exception initialization and terminate-handler support.
- [../artifacts/spec-ordered/2026-fp-rate/lbm-final/spec2026-harness-O3-v0-test.IzWYEIZo/results.tsv](../artifacts/spec-ordered/2026-fp-rate/lbm-final/spec2026-harness-O3-v0-test.IzWYEIZo/results.tsv) and [../artifacts/spec-ordered/2026-fp-rate/specrand-final/spec2026-harness-O3-v0-test.l07FWmhJ/results.tsv](../artifacts/spec-ordered/2026-fp-rate/specrand-final/spec2026-harness-O3-v0-test.l07FWmhJ/results.tsv): the last two FP-rate entries passed system validation; LBM also passed portable.
- [../artifacts/spec-ordered/2026-int-speed/xz/spec2026-harness-O3-v0-test.eRALLsHp/results.tsv](../artifacts/spec-ordered/2026-int-speed/xz/spec2026-harness-O3-v0-test.eRALLsHp/results.tsv): XZ passed at four threads, with its C++ thread source compiled without suppression macros.
- [../artifacts/spec-ordered/2026-int-speed/ntest-openmp/spec2026-harness-O3-v0-test.bHPl9hsS/results.tsv](../artifacts/spec-ordered/2026-int-speed/ntest-openmp/spec2026-harness-O3-v0-test.bHPl9hsS/results.tsv): Ntest passed after a fresh `-fopenmp -DSPEC_OPENMP` build. Its earlier configured-only pass was serial and is not parallel evidence. FLAC had also been attempted before this gap was discovered; both were revalidated in order under the corrected profile.
- [../artifacts/spec-ordered/ordered-test.j2XZEHkI/progress.tsv](../artifacts/spec-ordered/ordered-test.j2XZEHkI/progress.tsv): corrected-profile FLAC, GCC, LLVM, Cppcheck, ABC and VPR speed passed; stopped at Gem5's Linux macro collision. The existing rate portability flag now also applies to the speed variant.
- [../artifacts/spec-ordered/ordered-test.G51Wu5Sy/progress.tsv](../artifacts/spec-ordered/ordered-test.G51Wu5Sy/progress.tsv): Gem5 passed with the shared portability fix; its kit remains serial.
- [../artifacts/spec-ordered/2026-int-speed/838.diamond_s-system-cpuid.log](../artifacts/spec-ordered/2026-int-speed/838.diamond_s-system-cpuid.log): Diamond passed after comment-only assembly and Eigen's exact CPUID form were supported.
- [../artifacts/spec-ordered/2026-int-speed/846.minizinc_s-system-floor.log](../artifacts/spec-ordered/2026-int-speed/846.minizinc_s-system-floor.log): MiniZinc passed after exact Float80 floor support, without narrowing through double.
- [../artifacts/spec-ordered/ordered-test.MknQETxz/progress.tsv](../artifacts/spec-ordered/ordered-test.MknQETxz/progress.tsv), [../artifacts/spec-ordered/2026-int-speed/854.graph500_s-system-registers.log](../artifacts/spec-ordered/2026-int-speed/854.graph500_s-system-registers.log), and [../artifacts/spec-ordered/2026-int-speed/specrand-final/spec2026-harness-O3-v0-test.eVqax2vR/results.tsv](../artifacts/spec-ordered/2026-int-speed/specrand-final/spec2026-harness-O3-v0-test.eVqax2vR/results.tsv): NS-3, repaired Graph500 and specrand completed integer speed.
- [../artifacts/spec-ordered/ordered-test.rJqGQ0aj/progress.tsv](../artifacts/spec-ordered/ordered-test.rJqGQ0aj/progress.tsv): POT3D passed after empty namelist strings and list-directed character spacing were fixed, then rebuilt with host DO CONCURRENT lowering. Earlier serial-only diagnostic runs are not parallel evidence.
- [../artifacts/spec-ordered/803.sph_exa_s-system-atomics.log](../artifacts/spec-ordered/803.sph_exa_s-system-atomics.log): SPH-EXA passed after floating atomic add/sub support.
- [../artifacts/spec-ordered/ordered-test.v3UPFwTb/progress.tsv](../artifacts/spec-ordered/ordered-test.v3UPFwTb/progress.tsv): Cactus through specrand completed CPU 2026 FP speed, then CPU 2017 Bwaves passed both ABIs.
- [../artifacts/spec-ordered/507.cactuBSSN_r-system-stack-lengths.log](../artifacts/spec-ordered/507.cactuBSSN_r-system-stack-lengths.log) and [../artifacts/spec-ordered/ordered-test.A44Z7CAI/progress.tsv](../artifacts/spec-ordered/ordered-test.A44Z7CAI/progress.tsv): CPU 2017 Cactus and NAMD passed after system-ABI hidden-length compatibility.
- [../artifacts/spec-ordered/ordered-test.60EoMTDa/progress.tsv](../artifacts/spec-ordered/ordered-test.60EoMTDa/progress.tsv): Parest passed in C++03 mode, followed by POV-Ray and LBM; stopped at WRF.
- [../artifacts/spec-ordered/521.wrf_r-system-conversion.log](../artifacts/spec-ordered/521.wrf_r-system-conversion.log): WRF rate passed after NetCDF naming, Fortran runtime and large-method fixes, with explicitly recorded big-endian I/O and the invalid-call trap described below.
- [../artifacts/spec-ordered/526.blender_r-system-mixed-booleans.log](../artifacts/spec-ordered/526.blender_r-system-mixed-booleans.log): Blender passed with unsigned plain char and native-verified byte/boolean ABI conversions.
- [../artifacts/spec-ordered/527.cam4_r-system-large-data.log](../artifacts/spec-ordered/527.cam4_r-system-large-data.log): CAM4 passed after GNU89 C, NetCDF module/name settings, Fortran inquiries and chunked global initialization.
- [../artifacts/spec-ordered/ordered-test.TCG5x3tp/progress.tsv](../artifacts/spec-ordered/ordered-test.TCG5x3tp/progress.tsv): Imagick, NAB, Fotonik3d, ROMS and specrand completed CPU 2017 FP rate.
- [../artifacts/spec-ordered/ordered-test.ZwblIcjU/progress.tsv](../artifacts/spec-ordered/ordered-test.ZwblIcjU/progress.tsv), [../artifacts/spec-ordered/ordered-test.RjimWviQ/progress.tsv](../artifacts/spec-ordered/ordered-test.RjimWviQ/progress.tsv), and [../artifacts/spec-ordered/ordered-test.RunSpqyT/progress.tsv](../artifacts/spec-ordered/ordered-test.RunSpqyT/progress.tsv): Perl, GCC, MCF, OMNeT++ and Xalan speed passed after sharing the applicable rate portability settings.
- [../artifacts/spec-ordered/ordered-test.8LP9AD4k/progress.tsv](../artifacts/spec-ordered/ordered-test.8LP9AD4k/progress.tsv): x264 through specrand completed integer speed, followed by Bwaves, Cactus and LBM FP speed; stopped at WRF speed.
- [../artifacts/spec-ordered/ordered-test.u9Dk7tgM/progress.tsv](../artifacts/spec-ordered/ordered-test.u9Dk7tgM/progress.tsv): WRF speed passed a clean four-thread-profile build with the shared rate portability settings; stopped at CAM4.
- [../artifacts/spec-ordered/627.cam4_s-system-descriptor-stack.log](../artifacts/spec-ordered/627.cam4_s-system-descriptor-stack.log): CAM4 speed passed after adding Flang descriptor-stack operations.
- [../artifacts/spec-ordered/ordered-test.iM2C84Eq/progress.tsv](../artifacts/spec-ordered/ordered-test.iM2C84Eq/progress.tsv): clean POP2, Imagick, NAB, Fotonik3d, ROMS and specrand runs completed the final group. POP2 required NetCDF module/name settings, IOLENGTH/RECL, direct unformatted records, explicit exponent formatting and big-endian grid input. Only specrand passed portable in this final group.

WRF's bundled NetCDF uses `SPEC_CASE_FLAG` for lowercase wrapper names, and its radiation tables require big-endian unformatted I/O. The rate retry reused unchanged objects with `FORT_CONVERT=BIG_ENDIAN`; clean builds use `-fconvert=big-endian`. One source call genuinely omits a required argument. WRF explicitly opts into `--trap-missing-arguments`, which diagnoses and traps that invalid void-call path rather than fabricating an argument. The validated test input did not execute it. This does not establish compatibility for inputs that reach the trapped path. NativeAOT link diagnostics are retained separately in [../artifacts/spec-ordered/521.wrf_r-conversion-relink.log](../artifacts/spec-ordered/521.wrf_r-conversion-relink.log).

Each referenced matrix retains compiler/runtime/native-helper hashes, the optional C++ archive hash, frozen scripts/configuration, output-validation logs, and make logs. Portable blockers include missing POSIX headers and incomplete libc++ stream/locale support. No new train/reference results or SPEC scores are claimed.

New matrices append `threading_profile` as column 12 and retain staged OpenMP header/library hashes. Summaries keep `kit-openmp-v1` (explicit OpenMP), `kit-openmp-v2` (also host DO CONCURRENT lowering), and older multi-thread-count rows with unspecified activation separate. Native-oracle C and Fortran fixtures check four worker identities, scheduling, reductions, barriers and concurrent loops at O0/O3 in JIT/NativeAOT. This is not portable OpenMP support, complete OpenMP conformance, or proof that serial kit entries create workers.

The Fortran additions cover intrinsic namelist input/output, quoted native interchange, logical input, empty record reads, ENDFILE, conversion defaults, environment and array/model inquiries, default derived-component I/O and cleanup, restricted intrinsic derived assignment, PACK/RESHAPE/TRANSFER, deferred-length characters, dynamic array constructors and descriptor stacks. RECL limits output records; IOLENGTH counts transferred components without derived-type padding. Direct unformatted transfers use checked one-based REC offsets without sequential record markers, preserving byte conversion and array strides. Tested E/ES/G editing supports explicit exponent widths. Focused tests compare both ABI modes against native Flang in JIT and NativeAOT, including exact seeded random sequences and unformatted file bytes. This is not complete Fortran conformance: final procedures, defined I/O/assignment, assignment of owned components, parameterized components, formatted direct access, segmented records, value stacks and several editing modes remain unsupported.

WRF exposed methods with about 100,000 locals; large functions now reuse block-local scalars and retain cross-block values in typed per-call arrays. A 70,000-live-value fixture checks cyclic PHIs and normal/unwind results. CAM4 exposed the CLR initialized-data field limit; large globals now copy bounded chunks into the original contiguous allocation before applying relocations. Both changes pass native-oracle JIT/NativeAOT checks in both ABIs.

The subsequent full-video check also passed: all 1,150 frames of the supplied 4K HDR video matched after a fresh system-ABI relink. Evidence: [../artifacts/spec-ordered-full-video.log](../artifacts/spec-ordered-full-video.log), with native/CIL frame records under `artifacts/tests/spec-ordered-video/`.

The implementation adds managed nonlocal jumps and dynamic stack restoration, per-thread globals and C++ TLS cleanup, fixed/variadic/aggregate callback routing, native variadic pointers, persisted aggregate call signatures, C++ exception filters/helpers, and missing scalar intrinsics. Focused native-oracle checks pass in both ABIs where applicable, including four-thread TLS and system C++ workers in JIT/NativeAOT. An earlier full dual-ABI compiler/runtime/deployment gate passed with log [../artifacts/spec-ordered-toolchain-regression.log](../artifacts/spec-ordered-toolchain-regression.log). Freshly relinked ffmpeg decoders also passed all 96 threaded JIT/NativeAOT hash, draining and early-teardown cases in both ABIs, with zero generated native imports in the portable CIL image; log [../artifacts/spec-ordered-media-regression.log](../artifacts/spec-ordered-media-regression.log). These media checks precede the later Fortran additions.

The later regression run was canceled during a portable Fortran NativeAOT build, not reported as a pass. The interrupted Fortran suite then passed completely; log [../artifacts/spec-ordered-fortran-focused.log](../artifacts/spec-ordered-fortran-focused.log). All remaining core, driver, ABI, portable C++, modes and toolchain checks passed individually in `artifacts/spec-ordered-check-*.log`. The self-contained package and isolated deployment check also passed; logs [../artifacts/spec-ordered-deployment-build.log](../artifacts/spec-ordered-deployment-build.log) and [../artifacts/spec-ordered-deployment-test.log](../artifacts/spec-ordered-deployment-test.log). A subsequent focused namelist check verifies scratch-file cleanup in both ABIs and execution modes. This is a resumed set of checks, not an uninterrupted full-gate log. See [toolchain.md](toolchain.md) for reproduction and remaining boundary restrictions.

After the future/terminate additions, the current core gate passed through the namelist, unformatted and derived-I/O tests before another terminal interruption during inquiry; retained log [../artifacts/spec-ordered-openmp-toolchain-regression.log](../artifacts/spec-ordered-openmp-toolchain-regression.log). Inquiry and every remaining core/driver/ABI/mode/package check then passed in separate `artifacts/spec-ordered-openmp-*.log` runs. Current isolated deployment passed in [../artifacts/spec-ordered-openmp-deployment-test.log](../artifacts/spec-ordered-openmp-deployment-test.log). The optional system C++ and OpenMP fixtures were validated separately; the combined interrupted log itself is not a full-gate pass.

Subsequent Float80 floor, floating atomics, comment/CPU-query assembly, integer-slot ABI compatibility and Fortran I/O changes each passed focused native-oracle tests. The affected IR, aggregate-callback, Fortran, namelist, formatted-I/O, system-ABI and driver suites also passed with retained `artifacts/spec-ordered-fp-*.log` evidence. This does not imply that the earlier deployment or full-media logs used every later change.

### Final Verification

All 53 components in the current core runner passed in sequential batches, with affected Fortran I/O checks rerun after the final direct-record changes. Individual logs are retained as `artifacts/spec-ordered-final-<test>.log`. The driver, system ABI, portable C/C++, modes and relocated-toolchain checks also passed. A fresh self-contained distribution with bundled LLVM passed isolated C/C++/Fortran compilation without host compiler or dotnet executables on PATH: [package build](../artifacts/spec-ordered-final-deployment-build.log), [deployment test](../artifacts/spec-ordered-final-deployment-test.log). This is a completed set of component checks, not an uninterrupted invocation of the full runner.

The explicit upstream [system C++ archive](../artifacts/spec-ordered-final-system-cxx.log), [four-worker OpenMP](../artifacts/spec-ordered-final-openmp.log) and [Fortran DO CONCURRENT](../artifacts/spec-ordered-final-fortran-concurrent.log) fixtures passed with the current compiler/runtime. Both ffmpeg decoder ABIs were freshly relinked; all 96 two/four-thread JIT/NativeAOT hash, drain and early-teardown cases passed in [the final media log](../artifacts/spec-ordered-final-media.log). Portable decoder inspection again reported zero generated native imports.

The freshly relinked system decoder also matched native hashes for all **1,150 frames** of the supplied 4K HDR video. Evidence: [../artifacts/spec-ordered-final-video.log](../artifacts/spec-ordered-final-video.log); complete frame records remain in [../artifacts/ffmpeg-browser-system/native-frames.txt](../artifacts/ffmpeg-browser-system/native-frames.txt) and [../artifacts/ffmpeg-browser-system/cil-frames.txt](../artifacts/ffmpeg-browser-system/cil-frames.txt).

## Earlier Complete Inventories

Development evidence from 2026-09-07 on Linux x86-64, Clang/Flang 22.1.8 and .NET 10.0.11. Every installed entry includes a separate system and portable ABI attempt, including shared-source speed variants and the random-number checks. The inventories are one-copy, one-thread, non-reportable NativeAOT runs with **test inputs only**; separate train/reference checks are identified below. These are not SPEC scores, complete train/reference validation, or proof of parallel speed support. The kits add automatic thread/OpenMP suppression to some builds.

The configuration uses `-O3`, C++14 for CPU 2017 and C++17 for CPU 2026. Vectorization is off in the main inventory and tested separately below. The first diagnostic inventory allowed 120 seconds per build-plus-run; follow-up inventories allow 600 seconds and focused retries generally 300 seconds. A timeout is incomplete evidence, not a semantic failure. The wrapper now sets `SPEC_STACK_KB=unlimited`: bwaves exhausted an 8 MiB stack, then passed without recompiling its algorithm after raising the limit.

PASS requires successful harness execution **and SPEC output validation**. FAIL includes build rejection, runtime errors and output mismatches. No failure is counted as a pass merely because a binary was produced. Each local matrix directory contains result rows, logs, configuration and (for newer runs) compiler/runtime hashes and snapshots of make logs. Licensed sources, inputs, outputs and raw diagnostics stay under ignored artifacts.

| Scalar O3 Inventory | PASS | FAIL | TIMEOUT | Total ABI Cases |
| --- | ---: | ---: | ---: | ---: |
| CPU 2017 | 38 | 54 | 2 | 94 |
| CPU 2026, including final retries | 10 | 95 | 3 | 108 |
| Total | 48 | 149 | 5 | 202 |

## CPU 2017

All 47 installed entries were checked in both ABIs with the final compiler in a clean full pass. Results: **38 PASS / 54 FAIL / 2 TIMEOUT across 94 cases**, with no missing ABI rows. System: 20 PASS; portable: 18 PASS. The system WRF variants exceeded the 300-second diagnostic budget. Failed and timed-out entries still require implementation/configuration work and revalidation.

| Benchmark | System | Portable |
| --- | --- | --- |
| 500.perlbench_r | FAIL | FAIL |
| 502.gcc_r | FAIL | FAIL |
| 503.bwaves_r | PASS | PASS |
| 505.mcf_r | PASS | PASS |
| 507.cactuBSSN_r | FAIL | FAIL |
| 508.namd_r | FAIL | FAIL |
| 510.parest_r | FAIL | FAIL |
| 511.povray_r | FAIL | FAIL |
| 519.lbm_r | PASS | PASS |
| 520.omnetpp_r | FAIL | FAIL |
| 521.wrf_r | TIMEOUT | FAIL |
| 523.xalancbmk_r | FAIL | FAIL |
| 525.x264_r | FAIL | FAIL |
| 526.blender_r | FAIL | FAIL |
| 527.cam4_r | FAIL | FAIL |
| 531.deepsjeng_r | PASS | PASS |
| 538.imagick_r | PASS | FAIL |
| 541.leela_r | FAIL | FAIL |
| 544.nab_r | PASS | PASS |
| 548.exchange2_r | PASS | PASS |
| 549.fotonik3d_r | FAIL | FAIL |
| 554.roms_r | FAIL | FAIL |
| 557.xz_r | PASS | PASS |
| 600.perlbench_s | FAIL | FAIL |
| 602.gcc_s | FAIL | FAIL |
| 603.bwaves_s | PASS | PASS |
| 605.mcf_s | PASS | PASS |
| 607.cactuBSSN_s | FAIL | FAIL |
| 619.lbm_s | PASS | PASS |
| 620.omnetpp_s | FAIL | FAIL |
| 621.wrf_s | TIMEOUT | FAIL |
| 623.xalancbmk_s | FAIL | FAIL |
| 625.x264_s | FAIL | FAIL |
| 627.cam4_s | FAIL | FAIL |
| 628.pop2_s | FAIL | FAIL |
| 631.deepsjeng_s | PASS | PASS |
| 638.imagick_s | PASS | FAIL |
| 641.leela_s | FAIL | FAIL |
| 644.nab_s | PASS | PASS |
| 648.exchange2_s | PASS | PASS |
| 649.fotonik3d_s | FAIL | FAIL |
| 654.roms_s | FAIL | FAIL |
| 657.xz_s | PASS | PASS |
| 996.specrand_fs | PASS | PASS |
| 997.specrand_fr | PASS | PASS |
| 998.specrand_is | PASS | PASS |
| 999.specrand_ir | PASS | PASS |

Table evidence: [../artifacts/spec-matrix/spec2017-harness-O3-v0-test.LNfdBMkG/results.tsv](../artifacts/spec-matrix/spec2017-harness-O3-v0-test.LNfdBMkG/results.tsv), using the compiler/runtime hashes in that run's environment record. This complete pass supersedes the earlier inventory whose final bookkeeping was disrupted by editing its running script. Future runs execute frozen script/configuration snapshots and write a completion record; missing completion records must not imply a completed run.

## CPU 2026

The completed C++17 inventory and final-toolchain zstd/nab retries cover all **54 entries / 108 ABI cases**: **10 PASS / 95 FAIL / 3 TIMEOUT**, with no missing ABI rows. The full pass used a fixed intermediate compiler and a 600-second diagnostic budget; the two targeted retries used the final compiler and 300 seconds. Other entries are not implied to have been rerun with every subsequent fix. The earlier C++14, 120-second inventory remains available as historical evidence.

| Benchmark | System | Portable |
| --- | --- | --- |
| 706.stockfish_r | FAIL | FAIL |
| 707.ntest_r | FAIL | FAIL |
| 708.sqlite_r | FAIL | FAIL |
| 709.cactus_r | FAIL | FAIL |
| 710.omnetpp_r | FAIL | FAIL |
| 714.cpython_r | FAIL | FAIL |
| 721.gcc_r | FAIL | FAIL |
| 722.palm_r | FAIL | FAIL |
| 723.llvm_r | TIMEOUT | FAIL |
| 727.cppcheck_r | FAIL | FAIL |
| 729.abc_r | FAIL | FAIL |
| 731.astcenc_r | FAIL | FAIL |
| 734.vpr_r | FAIL | FAIL |
| 735.gem5_r | FAIL | FAIL |
| 736.ocio_r | FAIL | FAIL |
| 737.gmsh_r | FAIL | FAIL |
| 748.flightdm_r | FAIL | FAIL |
| 749.fotonik3d_r | FAIL | FAIL |
| 750.sealcrypto_r | FAIL | FAIL |
| 753.ns3_r | FAIL | FAIL |
| 765.roms_r | FAIL | FAIL |
| 766.femflow_r | TIMEOUT | FAIL |
| 767.nest_r | FAIL | FAIL |
| 772.marian_r | FAIL | FAIL |
| 777.zstd_r | PASS | PASS |
| 782.lbm_r | PASS | PASS |
| 800.pot3d_s | FAIL | FAIL |
| 801.xz_s | FAIL | FAIL |
| 803.sph_exa_s | FAIL | FAIL |
| 807.ntest_s | FAIL | FAIL |
| 809.cactus_s | FAIL | FAIL |
| 811.tealeaf_s | FAIL | FAIL |
| 816.nab_s | PASS | PASS |
| 817.flac_s | FAIL | FAIL |
| 820.cloverleaf_s | FAIL | FAIL |
| 821.gcc_s | FAIL | FAIL |
| 822.palm_s | FAIL | FAIL |
| 823.llvm_s | TIMEOUT | FAIL |
| 827.cppcheck_s | FAIL | FAIL |
| 829.abc_s | FAIL | FAIL |
| 834.vpr_s | FAIL | FAIL |
| 835.gem5_s | FAIL | FAIL |
| 838.diamond_s | FAIL | FAIL |
| 846.minizinc_s | FAIL | FAIL |
| 849.fotonik3d_s | FAIL | FAIL |
| 853.ns3_s | FAIL | FAIL |
| 854.graph500_s | PASS | FAIL |
| 857.namd_s | PASS | FAIL |
| 865.roms_s | FAIL | FAIL |
| 867.nest_s | FAIL | FAIL |
| 872.marian_s | FAIL | FAIL |
| 881.neutron_s | PASS | PASS |
| 998.specrand_s | FAIL | FAIL |
| 999.specrand_r | FAIL | FAIL |

Table evidence: [../artifacts/spec-matrix/spec2026-harness-O3-v0-test.xRPqCNjA/results.tsv](../artifacts/spec-matrix/spec2026-harness-O3-v0-test.xRPqCNjA/results.tsv). The initial C++14 inventory is retained under `spec2026-harness-O3-v0-test.zLG00EI7`. Do not turn a TIMEOUT into a PASS based on compilation alone.

Final-toolchain retries: [../artifacts/spec-matrix/spec2026-harness-O3-v0-test.lvmFfrFi/results.tsv](../artifacts/spec-matrix/spec2026-harness-O3-v0-test.lvmFfrFi/results.tsv) for zstd and [../artifacts/spec-matrix/spec2026-harness-O3-v0-test.G2cPbtac/results.tsv](../artifacts/spec-matrix/spec2026-harness-O3-v0-test.G2cPbtac/results.tsv) for nab. Both passed output validation in both ABIs.

## Vectorized Frontend Profile

`-O3 -fvectorize -fslp-vectorize` passed SPEC validation in both ABIs for 503/603 bwaves, 505/605 mcf, 519/619 lbm, 531/631 deepsjeng, 544/644 nab, 548/648 exchange2 and 557/657 xz: **28 cases**. Evidence directories are `spec2017-harness-O3-v1-test.Y8drDGSv`, `spec2017-harness-O3-v1-test.bGMcHM2a`, `spec2017-harness-O3-v1-test.RPfy2cMN`, `spec2017-harness-O3-v1-test.BGlYUQOz`, `spec2017-harness-O3-v1-test.MizYGjkQ`, `spec2017-harness-O3-v1-test.BfVroeHi` and `spec2017-harness-O3-v1-test.G5rJBkKr`.

This is compatibility with vectorized LLVM IR using reduction expansion and scalarization, **not hardware SIMD acceleration**. Portable exchange2 exposed an i80 mask population count; the wide-count regression and actual workload passed after implementing bit-width-aware counts. No generic `Vector<T>.Count` assumption or architecture-specific intrinsic substitution is used.

System 538/638 ImageMagick also passes this profile, while portable ImageMagick still fails on missing runtime/header support (`spec2017-harness-O3-v1-test.GFjOTtUh`). In total, the CPU 2017 vector-profile test attempts are **30 PASS / 2 FAIL across 32 cases**. Separate vectorized **train** runs passed in both ABIs for 505.mcf_r (`spec2017-harness-O3-v1-train.ul2zluue`) and 557.xz_r (`spec2017-harness-O3-v1-train.Qu1Enj9p`). Vectorized 505.mcf_r **reference** input also passed both ABIs (`spec2017-harness-O3-v1-ref.q7RpIYUK`). These do not imply train/reference coverage for other entries.

CPU 2026 777.zstd_r, 782.lbm_r, 816.nab_s and 881.neutron_s also passed vectorized test inputs under both ABIs: **8 PASS / 0 FAIL**. Evidence directories are `spec2026-harness-O3-v1-test.VkEmU9x8`, `spec2026-harness-O3-v1-test.n5txv8Ki`, `spec2026-harness-O3-v1-test.Lxi4HSpq` and `spec2026-harness-O3-v1-test.4g64OO1w`. Total explicitly vectorized test coverage across both suites is **38 PASS / 2 FAIL across 40 cases**.

## Earlier Inventory Blockers

The following list describes the earlier inventories above, not the current toolchain. Many system-led repairs also resolved portable compiler/runtime features. For current portable failures and checked PoCs, use the [portable audit](spec-portable-limitations.md).

- C++ library coverage is the largest group: iostream/locale/file-stream exports, standard-library globals, and unsupported native C++ boundaries. CPU 2026 also needs the correct C++ dialect; C++17 removes the earlier language errors but does not provide missing runtime features.
- C semantics still need TLS globals, dynamic stack save/restore, nonlocal jumps, variadic callbacks and additional library operations. Povray now gets past `llvm.frexp` and stops at a variadic callback.
- Fotonik3d now gets past INDEX, REPEAT, ADJUSTL, BACKSPACE, pointer allocation, temporary assignment, SPREAD, hidden character lengths and `llvm.powi`. The latest system relink reaches unformatted I/O; portable also needs complex `cexp` support. ROMS gets past pointer bounds and `llvm.tanh`, then reaches `llvm.stacksave`.
- Portable headers/runtime remain incomplete. The iteration adds libgen, utime, tokenizer declarations/implementations and permission masks; those do not imply general POSIX or C++ conformance.
- Benchmark-specific portability settings, Fortran module dependencies and source-version/compiler compatibility still need individual review. Licensed benchmark sources were not patched to hide errors.
- Validate train/reference inputs, preserve output checks, and implement real parallel/OpenMP behavior before claiming complete suites or publishing performance results.

## Reproduction

The final dual-ABI compiler/runtime/deployment regression gate passed; its retained log is [../artifacts/spec-iteration-toolchain.log](../artifacts/spec-iteration-toolchain.log). A freshly relinked CIL ffmpeg decoder also matched the native decoder for all **1,150 frames** of the supplied 4K HDR video. The latter evidence is in `artifacts/tests/spec-video/native-frames.txt` and `cil-frames.txt`; the earlier full-video gate remains intact. Frozen-runner checks passed on both real SPEC versions after the snapshot change.

```sh
bash scripts/package-toolchain.sh artifacts/spec-toolchain-final
SPEC_TIMEOUT=600 bash scripts/spec-matrix.sh artifacts/spec2017-harness artifacts/spec-toolchain-final test
SPEC_TIMEOUT=600 bash scripts/spec-matrix.sh artifacts/spec2026-harness artifacts/spec-toolchain-final test
SPEC_VECTORIZE=1 bash scripts/spec-matrix.sh artifacts/spec2017-harness artifacts/spec-toolchain-final test '*.exchange2_*'
bash tests/toolchain-all.sh
```

Use [../scripts/spec-summary.sh](../scripts/spec-summary.sh) to combine compatible result tables in oldest-to-newest order. It reports missing ABI rows rather than implying untested coverage. See [toolchain.md](toolchain.md) for settings and artifact layout.