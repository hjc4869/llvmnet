# Evidence: configure probes and the CMake platform (TC-07, 2026-11-02)

Build host: Linux x86-64 (Debian 13), .NET SDK 10.0.403, LLVM/Clang 22.1.8, CMake 4.3.4, Ninja 1.13.2, Meson 1.12.1, Autoconf 2.73 with libtool 2.6.2 (LibRaw's `autoreconf`). The [library survey](2026-11-02-library-survey.md) found that configure probes gave other answers than a native build in every build system. This change implements the owner proposals in [decisions.md](../plan/decisions.md#proposed-for-the-owner-2026-11-02-library-survey) and the M9 configure-probe entry as defaults:

- **Calls through a prototype without parameters.** A call that passes no arguments through a prototype without parameters and contradicts the definition, in its parameters or its return type, compiles to a run-time trap. The trap throws `InvalidProgramException` with the diagnostic, and the link prints `llvmnet: warning: emitting runtime trap: ...` (`-w` silences it). This covers translated definitions and the functions the runtime implements in managed code: `setjmp`, `longjmp`, and system mode's adapters such as `pthread_create`. Every other contradiction stays a compile error ([dev/calling-convention.md](../dev/calling-convention.md#direct-calls)).
- **CMake probes link and run.** `CMAKE_TRY_COMPILE_TARGET_TYPE STATIC_LIBRARY` is gone, and `try_run` runs the launcher through `env`. In try_compile projects the toolchain copies the managed image's `INFO:` strings into the launcher, so `check_type_size`, CMake's ABI check and `FindOpenMP` read them.
- **The CMake platform `LlvmNet`** (`cmake/Platform/LlvmNet.cmake`) replaces `Generic`. `UNIX` is set without the host's search paths, and `SHARED` and `MODULE` targets become bitcode archives under their Linux names that hold the members of their static dependencies (`cmake/llvmnet-rules.cmake`). C++20 module scanning is off.
- **`-DLLVMNET_RUNTIME=portable|system`** puts the mode into the compiler command (`CMAKE_C_COMPILER` is a list), so it reaches probes and directories that reset `CMAKE_C_FLAGS`.

Scratch scripts for the comparisons below ran this commit's `bin/` and `cmake/` against the survey's sources (`artifacts/oss`, read-only) and wrote only to a scratch directory.

## The survey's reproducers

`artifacts/oss/results/*-repro/` (gitignored), with this commit's toolchain:

| Reproducer | Before | After | Native |
| --- | --- | --- | --- |
| `libraw-repro/autoconf-check-lib`: `char lib_mem_src(void)` against `void (ptr, ptr, i64)` in an archive | error, exit 1 | links, exit 0, one warning | exit 0 |
| same, `gmtime_r` (`ptr (ptr, ptr)`) | error, exit 1 | links, exit 0, one warning | exit 0 |
| `lcms2-repro/check-function-exists`: `gmtime_r`, a nonexistent function, `gmtime_s`, a nonexistent `libm` function, `strlcpy` | 1 1 1 1 1 | 1, empty, empty, empty, 1 | 1, empty, empty, empty, 1 |
| `libjxl-repro/cmake-probes`: `check_type_size(size_t)`; brotli's Emscripten test | `TRUE` with size `''`; `1` | `TRUE` with size `8`; empty | `TRUE` with size `8`; empty |

A probe program that runs its call stops: `./conftest` prints `Unhandled exception. System.InvalidProgramException: Invalid direct call through a prototype without parameters: main -> __gmtime_r: i8 () versus ptr (ptr, ptr)` and exits with status 134.

## Libraries configured against native

Native is `clang-22` with the same options. Probe results are the `HAVE_*`, `SIZEOF_*`, `*_FOUND` and other cache entries, or for autotools and ffmpeg the `checking` lines and `config.h`:

| Library | Build system and options | Result | Configure time (native) |
| --- | --- | --- | --- |
| zlib 1.3.2 | CMake, upstream defaults (`ZLIB_BUILD_SHARED=ON`), tests on | configures; all 9 probe entries equal native | 21.2 s (0.6 s) |
| libxml2 2.15.3 | CMake, the survey's options without `-DIconv_IS_BUILT_IN=TRUE` | configures, iconv found built in; one difference, `getentropy`: libxml2 looks for it in `<sys/random.h>`, where musl does not declare it (musl declares it in `<unistd.h>`), so a native musl build gets the same answer | 23.7 s (0.8 s) |
| libjpeg-turbo 3.1.3 | CMake, defaults (shared and static) with `WITH_SIMD=OFF` | configures; probe entries equal native, except that the project skips its `RIGHT_SHIFT_IS_UNSIGNED` run test when cross-compiling and sets the same answer, 0 | 27.8 s (1.1 s) |
| LibRaw 0.22.2 | autotools, the survey's `jpeg` variant without the cache seed `ac_cv_lib_jpeg_jpeg_mem_src=yes` | detects JPEG (`-DUSE_JPEG -DUSE_JPEG8`, as native). Other differences: autoconf's C23 test fails because musl lacks `nullptr_t` (LC-32), so C11 is used; libtool prints `rm: cannot remove 'conftest.llvmnet': Is a directory`, with the same results | 25.3 s (2.7 s) |
| ffmpeg 9.0.2 | the FATE harness's options, configure unpatched | every `check_func` result equals native: `mkstemp`, `mmap`, `isatty` and the other functions the unpatched probe used to miss are found. The `config.h` differences are portable-mode facts: no x86 intrinsics or inline assembly (`HAVE_MM_EMPTY`, `HAVE_EBP_AVAILABLE`), no Linux kernel headers (`asm/types.h`, `linux/perf_event.h`, `sys/soundcard.h`, V4L2), and musl's `ioctl(int, int, ...)` (`HAVE_IOCTL_POSIX`) | 2 min 35 s (9.3 s) |
| a Meson project | `has_function` for 6 functions, with and without a prefix; `has_header_symbol`, `sizeof`, `links`, `run`, `find_library('m')` | all 17 results equal native | |

llvm-test-suite 22.1.8 configured with the old and the new toolchain file in portable mode: identical compile and link flags. Two probe results changed. `re_comp` is no longer found, because musl does not have it, so `Prolangs-C/archie-client` and `Prolangs-C/plot2fig` are no longer built in portable mode. Both were classified `exclusion-prototype` failures, so the portable count goes from 2,049 to 2,047 tests, and the rate without exclusions is unchanged. `FindOpenMP` now reads the OpenMP version (5.1, `202011`).

## Shared libraries built and run

A program links a `SHARED` target's archive by its path (`libz.so.1.3.2`), which needs the linker to recognize archives by content. TC-19 does that in commit `d4895c9` (branch `claude/infallible-yonath-0f316d`, not merged here). With that commit's three source files applied to a scratch copy of this tree:

| Library | Configuration | Build | Tests |
| --- | --- | --- | --- |
| zlib | upstream defaults: `libz.so.1.3.2` (archive, with `libz.so.1` and `libz.so`) beside `libz.a` | everything except `infcover`, which zlib compiles with `-coverage` (documented limitation) | 17 of 18 run serially; `zlib_coverage` fails for the same reason. Native: 18 of 18 |
| libxml2 | defaults (`BUILD_SHARED_LIBS=ON`), zlib from the survey's portable prefix | complete; `libxml2.so.16.1.3` holds zlib's members, which libxml2 links `PRIVATE` | 22 of 22 |
| libjpeg-turbo | defaults (`ENABLE_SHARED=ON ENABLE_STATIC=ON`), `WITH_SIMD=OFF` | complete; `libjpeg.so.62.4.0` and `libturbojpeg.so.0.4.0` beside `libjpeg.a` and `libturbojpeg.a`, without the survey's duplicate-rule error | 662 of 662 (the survey's static-only build: 331) |

Without TC-19, these configurations configure and build their libraries, and the links of programs against the shared archives fail (`libz.so.1.3.2:1:2: error: expected integer`).

## Tests

- `tests/configure-probes.sh` (core) links the probes of autoconf (`AC_CHECK_FUNC`/`AC_CHECK_LIB`, also Meson's `has_function`), ffmpeg (`check_func`) and CMake (`CheckFunctionExists.c`) for C library functions, a variadic function, `setjmp` and `longjmp`, system-mode adapters, a function in a bitcode archive and two functions that exist nowhere. Each of the 26 probes must link with llvmnet exactly when it links with `clang-22`, in both modes. It also checks that four of them trap when run, that `-w` silences the warning, and that a contradicting call through a prototype with parameters is still rejected.
- `tests/cmake-toolchain.sh` (toolchain) configures a project with 13 probe results natively and in both modes and requires equal results. It checks `UNIX`, a directory that resets `CMAKE_C_FLAGS` (the program reports the mode `LLVMNET_RUNTIME` selected), and a `SHARED` and a `STATIC` library with one output name: the shared one an archive under `libtwice.so.1.2.3` with its links and the member of its `PRIVATE` dependency, the static one linked into a program that runs.
- `tests/ir.sh`: the rejected fixture `used-void-return.ll` calls through a prototype with a parameter, since its old zero-argument call is now the probe shape.

The gate, `tests/run.sh --tag core --tag toolchain --exclude-tag slow` in a 24 GB scope, passed 95 of 95 tests in 742 s, `configure-probes` in 64 s and `cmake-toolchain` in 87 s.

## What this does not show

- **621.wrf_s.** Its call to `omp_get_thread_num` through an implicit interface (`float ()` against `i32 ()`, a listed SPEC exclusion) has the probe shape, so it now compiles to a trap instead of failing the build. The benchmark has not been rerun, so it is unknown whether its workload reaches the call; it stays listed. PALM's four-argument `open` is still a compile error.
- **FATE was not rerun.** `tests/ffmpeg-fate.sh` no longer patches configure's probe. Its portable configure gives the same `config.h` and `ffbuild/config.mak` as the patched one did, so the build FATE tests is the same.
- **`-l<name>` finds only `lib<name>.a`.** A project that installs only a shared library is found by CMake's `find_library` (`.so` first, as natively), but not by `-l` from a `.pc` file.
- **CI has no Meson or autotools.** The Meson and autotools results above come from local runs; the gate tests the probe programs those tools write.
