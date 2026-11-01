# Evidence: open-source library survey (2026-11-01 to 2026-11-02)

Build host: Linux x86-64 (Debian 13, kernel 6.12), AMD Ryzen AI MAX+ 395 (16 cores, 32 threads), 124 GB RAM, .NET SDK 10.0.403 with runtime 10.0.14, LLVM/Clang/Flang 22.1.8. The toolchain is commit `a5ab005`, packaged with `scripts/package-toolchain.sh` into a private prefix so that gate runs in the checkout could not change it during the survey. Every build, test and benchmark ran in a memory-capped user scope (`systemd-run --user --scope -p MemoryMax=10G..24G -p MemorySwapMax=0`), up to four libraries at a time, on a host shared with other work (load average 5 to 15). Timings are the minimum of 5 or more runs.

The survey built 18 widely used libraries in portable mode with their own build systems, using `cmake/llvmnet.cmake`, `meson/llvmnet.ini`, autotools or GN. It ran each library's own test suite under the JIT and compared outputs with a native `clang-22` build of the same configuration (`-O2 -fno-vectorize -fno-slp-vectorize`, assembly and SIMD off on both sides). Benchmarks compare native, the JIT and NativeAOT executables published from the same bitcode.

- **Round one:** the third-party libraries of the LightPlayer-Packages repository.
- **Round two:** the native dependencies of its LightStudio.AvaloniaStatic package: Skia and the SkiaSharp C API, ANGLE, and Skia's pinned third-party libraries.

The scripts, per-library notes and reproducers are not in the repository: they live in the build host's `artifacts/oss/` (`REPORT.md`, `results/<library>.md`, `results/*-repro/`, `scripts/`). Moving the strongest suites into `tests/` is part of WG-07 ([proposed for the owner](../plan/decisions.md#proposed-for-the-owner-2026-11-02-library-survey)), and each defect below is to get its own regression test with its fix.

## Results

No output differed from native except where the native build itself is nondeterministic or its C library differs. The exceptions:

- 6 Skia golden images differ by at most 5/255 in at most 10 pixels, because musl's `libm` rounds the last bit differently from glibc's (LC-32).
- An unmodified Skia DM run overflows the stack (BE-44).

"Time" is NativeAOT / JIT elapsed time relative to native; throughput figures say so.

### Round one: LightPlayer-Packages

| Library | Build system | Changes from a native build | Upstream tests in portable mode | Outputs compared with native | Time |
| --- | --- | --- | --- | --- | --- |
| zlib 1.3.2 | CMake | static only; `ninja -k 0` (the coverage test always uses `-coverage`) | 15 of 16 (coverage test not built) | compressed streams identical | compress 1.1–2.1x / 1.1–2.3x; inflate 1.05x / 1.6x |
| libxml2 2.15.3 | CMake | `-DIconv_IS_BUILT_IN=TRUE` | 22 of 22; runtest 3,880 of 3,904 sub-tests, 24 skipped by design (musl iconv, `__linux__`) | identical | streaming parse 1.2x / 1.6x of a native musl build; DOM parse 4.7x / 4.4x (PF-16) |
| libjpeg-turbo 3.1.3 | CMake | SIMD off; `check_type_size` results in the cache; static only | 331 of 331 | 226 test output files identical | throughput 0.71x / 0.64x (compress), 0.78x / 0.77x (decompress) |
| jxrlib 2019.10.9 | Makefile | two `-Wno-error` options (native too) | no suite; 62 round trips | identical | 1.27–1.40x / 1.53–2.36x |
| Little CMS 2.19.1 | CMake | none | testcms 157 of 157; plug-in test beds | 17 tool runs identical | throughput 0.74x / 0.83x over 27 transforms |
| LibRaw 0.22.2 | autotools | `autoreconf` on a copy; `ac_cv_lib_jpeg_jpeg_mem_src=yes` | no suite | 153 sample-program outputs identical | demosaic 1.35–2.4x / 1.6–2.6x |
| Opus 1.6.1 | Meson | DNN placeholder files (native too) | 15 of 15, float and fixed point | 140 conformance-vector decodes identical | encode 1.53x / 1.54x; decode 1.35x / 1.55x |
| dav1d 1.5.4 | Meson | none | 6 of 6 (checkasm needs assembly) | 171 conformance decodes identical | 1080p decode 1.38–1.63x / 1.46–1.63x; threads scale as native |
| libjxl 0.12.0 | CMake | Highway `EMU128`; dependencies built from source; `main` in an archive (TC-19) | 5,897 of 5,897 that run without the test data (native: same) | 64 encodes and decodes identical | 2.1–2.5x / 2.2–5.4x |

### Round two: AvaloniaStatic's native dependencies

Sources: the synced SkiaSharp v4.153.1 tree (Skia and its `third_party/externals`) and ANGLE chromium/7922, built out of tree.

| Library | Build system | Changes from a native build | Upstream tests in portable mode | Outputs compared with native | Time |
| --- | --- | --- | --- | --- | --- |
| expat 2.8.1 | CMake | static only | runtests 4,788 of 4,788 checks | 5,176 output files identical | 100 MB parse 1.44x / 2.4x |
| libpng 1.6.58 | CMake | `SHARED` targets built as static | 37 of 37 | 468 tool runs identical | 8K decode 2.1x / 2.1x (PF-15) |
| brotli 1.2.0 | CMake | `BROTLI_EMSCRIPTEN=OFF` in the cache | 28 of 28, plus 45 compatibility decodes | 1,217 streams identical | quality 5: 2.0x / 2.3x; decode 1.42x / 2.1x |
| wuffs 0.3.3 | single file | own harness | 179 files, 493 frame checksums | identical | GIF 1.15x / 1.09x; PNG 1.8–1.9x (PF-15) |
| FreeType 2.14.3 | CMake | `-DUNIX=ON` | own harness: 1,307 fonts, 4.69 million glyph renders in 6 modes | identical | 1.66x / 1.89x |
| HarfBuzz 14.2.1 | Meson | GLib stand-in for hb-shape/hb-subset; four `HAVE_FT_*` macros set by hand | meson 72 of 72; shaping 7,525; subset 2,072 of 2,072 | identical counts | shaping 1.45–2.23x / 1.64–2.54x |
| libwebp 1.6.0 | CMake | none | 441 tool runs | 824 output files identical | encode 1.22–1.46x / 1.18–1.61x; decode 1.25x / 1.8–2.1x |
| Skia (SkiaSharp 4.153.1) | GN | `target_cpu="generic"`, `-DSK_BUILD_FOR_UNIX`, fontconfig and GL off, `-rdynamic` removed, two musl shims for the tools | DM unit tests 2,197 of 2,197 with `musttail` off in Skia, otherwise a stack overflow after 2,017 (BE-44); 985 of 991 golden images identical | rendering harness identical | 1080p UI frame 2.97x / 2.17x; nanobench geometric mean 2.33x / 2.55x |
| ANGLE chromium/7922 | GN (Chromium) | clang wrapper dropping Chromium-specific, `-m` and `--target` flags; `-D__linux__`; header shims; one patched file | angle_unittests 5,989 of 5,999 (native 5,998; the 9 need fork or page faults) | 1,923 shader translations identical | translator 2.1x / 3.7–5.3x |

In system mode, zlib, libxml2 and ANGLE were also checked:

- **zlib:** 15 of 16 tests pass.
- **libxml2:** all 3,904 runtest sub-tests pass; the DOM parse takes 1.47 s and 839 MB.
- **ANGLE:** a headless EGL/GLES program linked against `libGLESv2_static` renders on the host GPU (Mesa) through ANGLE's GL backend, with the same pixels as native, under the JIT and NativeAOT. Portable mode runs everything except the host GL libraries, which it cannot load (documented).

C++ exceptions, `setjmp`/`longjmp` in C, `std::thread`, pthreads and OpenMP behaved as native in every library that uses them.

## Findings and plan items

| Finding | Item | Measured | Reproducer (`artifacts/oss/results/`) |
| --- | --- | --- | --- |
| `musttail` calls are translated as ordinary calls, without a diagnostic | [BE-44](../plan/01-compiler-backend.md) | a chain that runs in constant stack natively overflows (JIT "Stack overflow", NativeAOT SIGSEGV); Skia's SkSL raster pipeline aborts DM | `skia-repro/musttail` |
| Configure probes give wrong answers: autoconf `AC_CHECK_FUNC`/`AC_CHECK_LIB` and Meson `has_function` say "no" to every function, because their `char f(void)` call contradicts the definition; CMake probes never link, so `check_function_exists` and `check_c_source_compiles` say "yes" to anything | [TC-07](../plan/06-driver-and-toolchain.md) | LibRaw silently lost JPEG support, lcms2 its mutexes, HarfBuzz FreeType's variable fonts (28 shaping failures); brotli concluded the compiler was Emscripten | `libraw-repro/autoconf-check-lib`, `lcms2-repro/check-function-exists`, `libjxl-repro/cmake-probes` |
| `CMAKE_SYSTEM_NAME Generic` leaves `UNIX` unset and makes `SHARED` targets a configure error | [TC-07](../plan/06-driver-and-toolchain.md) | `FindIconv` fails (libxml2); libpng names its archive differently from its `.pc` file; zlib's default configuration fails; libjpeg-turbo's shared and static targets collide | `zlib.md`, `libxml2.md` |
| `main` in an archive member is not linked; an archive not named `*.a` is parsed as textual IR, contrary to [driver.md](../user/driver.md#link-inputs) | [TC-19](../plan/06-driver-and-toolchain.md) | all 61 libjxl gtest binaries; Chromium-style `lib*.so` archives | `libjxl-repro/main-in-archive`, `angle-repro/archive-any-name` |
| The bitcode link grows super-linearly for large C++ programs | [TC-20](../plan/06-driver-and-toolchain.md) | cjxl 26 s against the sysroot (`llvm-link-22`: 1.0 s); Skia DM 371 s and 2.35 GB, 272 s of it the program's own objects (`llvm-link-22`: 10.7 s); `angle_unittests` 100 s; HarfBuzz's 59 links 460 s against 3 s | `libjxl-repro/link-scaling` |
| Launcher programs see `argv[0]` = `<name>.llvmnet/program.dll`, also through a symlink | [TC-21](../plan/06-driver-and-toolchain.md) | usage texts, multi-call programs | `libjpeg-turbo-repro/argv0` |
| Portable mode still predefines `__x86_64`, `__k8`, `__k8__`, `__SEG_FS`, `__SEG_GS`, contrary to [modes.md](../user/modes.md) | [TC-22](../plan/06-driver-and-toolchain.md) | code that tests `__x86_64` selects x86 paths | `jxrlib-repro/host-macros` |
| Trap-mode `-fsanitize=...` is rejected although it needs no runtime | [TC-23](../plan/06-driver-and-toolchain.md) | Chromium's ANGLE build passes it | `angle-repro/trap-sanitizers` |
| `setjmp` inside a C++ `try` (`invoke @setjmp`) is rejected | [BE-45](../plan/01-compiler-backend.md) | LibRaw's `unpack_thumb` | `libraw-repro/setjmp-invoke` |
| `--diagnose-limits` misses functions the JIT compiles as MinOpts | [BE-46](../plan/01-compiler-backend.md) | `hb_ot_tags_from_script_and_language` (52.9 KB of IL, 1,406 locals): no threshold reported, 4x slower | `harfbuzz-repro/diagnose-limits-minopts` |
| `select` and `llvm.abs` become branches that RyuJIT keeps | [PF-15](../plan/08-performance.md) | Paeth loop 5.7x native on random data, 3.4x on smooth data (Clang: 7 `cmov`; JIT: 13 conditional jumps, 1 `cmov`); the whole PNG decode gap in libpng and wuffs | `libpng-repro/paeth-select` |
| The portable `mmap` emulation: a `SortedList` table makes `munmap` quadratic, each mapping allocates an extra page to align, `brk` is off, `MADV_DONTNEED` keeps pages resident | [PF-16](../plan/08-performance.md) | 200k one-page mappings: native 0.46 s / 783 MB, JIT 3.3 s / 1,649 MB; unmapping 50k/100k/200k in ascending order: 0.20/0.77/3.01 s; libxml2 DOM parse 3.49 s / 1.5 GB portable, 1.47 s / 839 MB system, 0.80 s / 787 MB native | `artifacts/oss/micro/pages2.c` |
| System mode: `fork()` + `exec` loses the child's redirected descriptors and hangs the parent | [SA-10](../plan/05-system-abi.md) | ANGLE's test runner aborts with "stack smashing detected" | `angle-repro/system-fork-exec` |
| System mode: the optional libstdc++ archive has no `src/c++20` members | [SA-11](../plan/05-system-abi.md) | C++20 `std::stringstream::str()` unresolved | `angle-repro/system-cxx20-sstream` |
| System mode predefines `__SSE2__`, so x86 intrinsic paths fail at the final link | [SA-12](../plan/05-system-abi.md) | "Unsupported intrinsic: llvm.x86.sse2.psad.bw" | `angle-repro/system-x86-intrinsics` |
| musl differences that libraries see | [LC-32](../plan/02-portable-c-library.md) | iconv has no EBCDIC/IBM-1141 and no EUC-JP 0x8F (24 libxml2 sub-tests); C23 `nullptr_t` missing; `libm` last-bit differences (6 Skia images) | `libraw-repro/c23-nullptr_t`, `skia-repro/libm` |
| Hot functions over RyuJIT's limits run as MinOpts under the JIT; NativeAOT is unaffected | [PF-07](../plan/08-performance.md), [BE-26](../plan/01-compiler-backend.md) | libjpeg-turbo `encode_one_block` (404 KB of IL, 14,248 locals), dav1d `decode_b`, 22 functions in cjxl, FreeType's hinting interpreters, ANGLE's lexer and parser, libpng `png_do_read_transformations`, brotli `BrotliSplitBlock` | `libjpeg-turbo-repro/diagnose-limits.sh` |
| Generic vector code is scalarized | [PF-09](../plan/08-performance.md) | Skia's 8-lane blur and mask kernels 30–80x native; the shipped Skia (SIMD and its low-precision pipeline) draws the UI frame in 6.95 ms against 52.7 ms for NativeAOT | `skia.md` |

Further observations:

- **Configure is slow.** Each probe that links costs 0.6 to 1.7 s (dav1d 43 s against 6 s, LibRaw 21.7 s against 2.7 s).
- **Process-per-case suites are slow under the JIT.** libjxl's ctest takes 16 min 48 s against 1 min 53 s natively, because a large program takes 0.3 to 1.5 s of warm-up.
- **Compiling to bitcode is fast.** It costs 0.65 to 1.25 times `clang -O2`.
- **NativeAOT's SSE2 baseline costs float-heavy code 3 to 8%** against `--aot-instruction-set=x86-64-v3`.
- **NativeAOT can lose to the JIT.** It is slower on Skia's raster pipeline (2.97x against 2.17x), only partly reproduced.
- **Allocation is not slow, but live memory is.** PF-11 found `malloc` faster than glibc's on the allocation microbenchmark. Allocation-heavy code with many live objects is a different pattern: 8 million live nodes take 0.29 s with glibc, 0.70 s with musl natively, 0.77 s as NativeAOT, and the mapping table (PF-16) dominates once mallocng's page-sized groups accumulate.

## What the survey does not show

- The scripts are not in the repository and did not run on CI.
- The libraries' own suites cover their features unevenly. jxrlib, LibRaw, FreeType and wuffs have no upstream suite, and the harnesses written for them cover decoding and rendering, not every API.
- Benchmarks ran on a shared host. Ratios are reliable to about 10%, absolute times less so.
