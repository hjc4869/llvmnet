# Runtime modes and ABI contracts

A program is built for one of two runtime modes, which decide what the compiled code is linked against and where it runs. The mode is chosen with `--runtime=portable` (the default) or `--runtime=system`, and it is a property of every object, archive, managed library and image: LLVM module flags and assembly metadata carry the ABI tag, a link with mismatched or untagged inputs fails, images of different modes cannot share one process, and an image refuses a `LlvmNet.Runtime.dll` from an incompatible llvmnet version ([dev/versioning.md](../dev/versioning.md)). `llvmnet --runtime=<mode> -x ir -c input.ll` compiles LLVM IR for a mode and records its tag.

| Mode | ABI tag | Runs on | C, C++ and Fortran libraries |
| --- | --- | --- | --- |
| `portable` (default) | `dotnet64-v2` | every supported .NET host ([deployment.md](deployment.md#supported-hosts)) | musl 1.2.5, libc++/libc++abi, LLVM's OpenMP runtime and flang-rt 22.1.8 from `sysroot/dotnet64-v2`, compiled into the program; system services from the host layer in `LlvmNet.Runtime` |
| `system` | `system-linux-x64-v1` | Linux x86-64 | the host's glibc and other native libraries through generated bridges; flang-rt built against glibc from `sysroot/system-linux-x64-v1` |

## Portable .NET ABI

```sh
llvmnet-clang --runtime=portable source.c -o portable.dll
llvmnet-clang++ --runtime=portable -std=c++17 source.cpp -o portable-cpp.dll
llvmnet-flang --runtime=portable source.f90 -o portable-fortran.dll
dotnet portable.dll
llvmnet-clang --runtime=portable --nativeaot source.c -o native-program
```

Tag: `dotnet64-v2`, the default mode. Sources compile with `-nostdinc` against the sysroot's headers: musl 1.2.5 for C and libc++ 22.1.8 for C++. Executables link musl, libc++, libc++abi, LLVM's OpenMP runtime (libomp) and flang-rt 22.1.8 and the compiler-rt builtins from the sysroot as bitcode, compiled into the program's CIL; `-fopenmp` needs no further option, and the default team size is the processor count .NET reports (`DOTNET_PROCESSOR_COUNT` overrides it) unless `OMP_NUM_THREADS` is set. `-nostdlib`, `-nodefaultlibs` and `-nostartfiles` leave them out; `-lc`, `-lm`, `-lpthread`, `-ldl`, `-lrt`, `-lc++` and the Fortran runtime names are accepted and need no archive. `--portable-sysroot <dir>` selects another sysroot.

The LLVM target is a fixed `x86_64-unknown-none` layout that defines the data ABI; it does not generate x86 instructions. Host OS and CPU macros (`__linux__`, `__unix__`, `__x86_64__`, `__SSE__` and similar) are not defined; programs see `__LLVMNET__`, `__LLVMNET_PORTABLE__` and `__LLVMNET_ABI_VERSION`. Options that would change the ABI (`-target`, `--sysroot`, `-m*`, `-fshort-wchar`, `-fshort-enums`, `-fpack-struct`) are rejected. `-funsigned-char` is accepted: it changes no layout, so objects built with either signedness link together and with the signed-`char` sysroot, as they do natively, and each side reads a `char` argument or result with its own signedness.

| Type or interface | `dotnet64-v2` |
| --- | --- |
| Byte order / address space | little-endian / 64-bit |
| `char`, `short`, `int`, `long`, `long long` | 8, 16, 32, 64, 64 bits; plain `char` signed unless `-funsigned-char` |
| `size_t`, `ptrdiff_t`, pointers | 64 bits |
| `int_fast16_t`, `int_fast32_t` and unsigned forms | 64 bits, as in glibc |
| `wchar_t` | signed 32-bit |
| `float`, `double` | IEEE binary32 / binary64 |
| `long double` | 80-bit extended precision in 16-byte storage, computed in software on every host |
| `__int128` | 128-bit integer storage and helpers |
| Varargs | software SysV-style register and overflow areas, independent of the host's calling convention |
| C library structures | musl 1.2.5 x86-64 layouts (`FILE`, `jmp_buf`, `struct stat`, pthread types, `sigset_t`) |
| C++ objects | Itanium layout and libc++ ABI version 1 (`std::__1`) |
| Fortran descriptors | Flang 22 CFI version 20240719 with byte strides |
| errno and error text | Linux numbering; musl's messages |
| System services | Linux x86-64 system-call semantics implemented over .NET ([dev/host-abi.md](../dev/host-abi.md)) |

[abi.md](abi.md) is the frozen contract in full, and `tests/abi-freeze.sh` keeps it from changing.

The same image runs on every supported host ([Supported hosts](deployment.md#supported-hosts)). Programs see Linux semantics and structure layouts on every host. Paths, file-system capabilities, time-zone data and available resources still depend on the host, and services .NET cannot provide (`fork`, `exec`, sockets, hard links and others) fail with the errors [dev/host-abi.md](../dev/host-abi.md) lists; [its last section](../dev/host-abi.md#hosts-other-than-linux) describes what Windows and macOS do differently.

Native libraries cannot be linked: `--system-library` is rejected, and a function the sysroot does not provide is a link error. Recompile dependencies in portable mode or call them through a managed interface. Portable libraries (`-shared`) do not link the C library, because musl's process state (thread pointer, heap, stdio) belongs to the program image; a library that calls the C library must be linked into the program as bitcode. Use scalar and pointer `CExport` signatures across assemblies; aggregate signatures are not yet a stable API.

## System ABI

```sh
llvmnet-clang --runtime=system -c source.c -o source.o
llvmnet-clang --runtime=system source.o -lm -pthread --nativeaot -o program
llvmnet-clang --runtime=system source.c --system-library /path/libexample.so -o program.dll
```

Tag: `system-linux-x64-v1`. This mode uses the native Linux x86-64 SysV ABI and the host's headers and libraries. C stdio, allocation, errno, math, file interfaces, native data and most pthread calls go to the real system libraries.

LLVM generates a native bridge for each imported call signature: CIL passes a byte argument record, and LLVM emits the native call, including varargs, register-classified structures, `byval` and `sret`. Native variadic functions called through pointers, such as `fcntl`, `open` and `snprintf`, use call-site-specific LLVM thunks. C callbacks passed to native code use unmanaged-callable CIL entry points; native calls into translated variadic or aggregate callbacks fail with a diagnostic. Native globals resolve through the native library that defines them.

JIT output includes the generated bridge as `lib<name>.llvmnet-host.so`. NativeAOT links the bridge statically, so the result is one executable; libraries given with `--system-library` stay dynamic dependencies.

Threads are native pthreads; an adapter wraps thread start and TLS destructors so callbacks run inside .NET's thread lifetime, including under NativeAOT. Cancellation, `pthread_exit`, recovery from hardware-fault signals, external native TLS globals and TLS destructors on threads created by native libraries are incomplete. C++ exceptions use the managed bridge, and C++ library code must be linked as bitcode (for example the optional libstdc++ archive below); native libstdc++ is not called. Fortran programs link flang-rt from `sysroot/system-linux-x64-v1`.

Child processes start through the host's `posix_spawn`, `system` and `popen`, whose child runs only C library code before it executes the program. A forked child instead goes on running translated code in a copy of the .NET runtime, and that needs a private copy of the code the runtime generated. Under the JIT, CoreCLR's W^X protection maps generated code from shared memory, so a child and its parent would write their compiled methods and stubs into the same pages; `fork`, `vfork`, `_Fork` and `forkpty` therefore fail with `ENOSYS` there, as `fork` does in portable mode. NativeAOT executables, which generate no code, and JIT runs with `DOTNET_EnableWriteXorExecute=0` call the host's `fork`; `vfork` behaves as `fork`, which POSIX permits, because its child would otherwise overwrite the frames the suspended parent returns through. The child still holds only the thread that forked, in a runtime whose other threads are gone, so keep it to what the child of a multithreaded program may do, at most `dup2`, `close` and similar calls before `exec` or `_exit`. SPEC's perlbench and cppcheck, which fork as NativeAOT executables, pass.

Inline assembly is rejected except for empty or comment-only `asm` statements without outputs, which become a memory barrier in both modes, and empty ones whose one register output is tied to an input (`asm("" : "+r"(x))`, `asm("" : "=r"(y) : "0"(x))`), which yield that input. System mode also accepts the x86 `int3` trap, which raises `SIGTRAP`, and the CPUID and XGETBV forms used by LLVM's and Eigen's host detection, which run in generated native thunks.

System mode compiles for native x86-64, so Clang predefines `__x86_64__`, `__SSE__`, `__SSE2__` and the other baseline feature macros, and libraries select their SSE code paths by them. Vector operations that Clang lowers to generic LLVM IR translate (scalarized, ADR-10), but x86 target intrinsics (`llvm.x86.*`) do not, apart from the spin-wait hint `_mm_pause` and the fences `_mm_mfence`, `_mm_lfence` and `_mm_sfence`. Compiling a unit that contains another one prints a warning naming the intrinsic and its function, and linking a program that reaches it fails with the same message; the link drops functions nothing references, so a library that merely contains such a path still links. Build the library's portable C path instead: `-U__SSE__ -U__SSE2__` selects it in libraries that test those macros, and others have their own switch. Whether system mode should stop predefining the SSE macros is an owner decision (SA-12; [measurements and recommendation](../evidence/2026-11-02-system-mode-survey.md#recommendation-on-the-macros)).

By default, a void call to a translated function with fewer arguments than its definition is a compile error. `--trap-missing-arguments` (both modes) instead compiles the call site to throw `InvalidProgramException` and reports each such call while compiling.

## Optional System C++ Library

[../../scripts/build-system-cxx.sh](../../scripts/build-system-cxx.sh) compiles selected, unmodified libstdc++ 16 sources to system-mode bitcode against the matching host headers. The archive provides streams, locales, containers, threads, futures, `random_device`, regular expressions, demangling and `std::filesystem`. Obtain the libstdc++ source of the host's GCC 16 release separately, for example the `libstdc++-v3`, `libiberty` and `include` directories of Debian's `gcc-16-source` package.

```sh
bash scripts/build-system-cxx.sh artifacts/vendor/gcc-16.2.0/libstdc++-v3
tests/run.sh system-cxx
bin/llvmnet --runtime=system program.cpp artifacts/system-cxx/libstdcxx-system.a -o program
```

`tests/system-cxx.sh` compares C++ fixtures with native output at `-O0` and `-O3` under JIT and NativeAOT. Keep the source notices and the GPL with the GCC Runtime Library Exception with any redistributed archive; the package script does not build or include it.

## Optional System OpenMP

System-mode OpenMP links LLVM's native C runtime; outlined regions stay translated CIL and enter through generated callback thunks. Stage the Debian headers and runtime without root privileges and run the tests:

```sh
bash scripts/install-system-openmp.sh
tests/run.sh openmp fortran-concurrent
```

The stage defaults to `artifacts/system-openmp` with package version `1:22.1.8-1+b2` (`OPENMP_DEB_VERSION`) and keeps the package hashes and the copyright file. `LLVMNET_OPENMP_PREFIX` selects another stage for the tests. Programs need the library on the loader path (`LD_LIBRARY_PATH` or a system installation); it is not bundled. Flang's `-fdo-concurrent-to-openmp=host`, which Flang 22 marks experimental, uses the same runtime. Portable mode has no OpenMP runtime, and device offload is not supported.

