# Optimized SPEC Coverage

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

## Remaining Work

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