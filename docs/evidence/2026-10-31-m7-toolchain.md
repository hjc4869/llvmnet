# Evidence: M7 toolchain and distribution (2026-10-31)

Build host: Linux x86-64 (Debian 13), .NET SDK 10.0.403, LLVM/Clang/Flang 22.1.8. Every number below was produced by a command in this repository, named with it. One section per M7 item, in the order the items were closed.

## SA-01: `--native-import` removed

The explicit scalar P/Invoke path of system mode is gone: the `NativeImport` record, the import table in `CompilerOptions`, the `DefinePInvokeMethod` emission in `CilCompiler.ResolveFunction`, the two fixtures `tests/c/native-import.c` and `tests/c/native-aggregate.c`, and the `tests/link.sh` and `tests/modes.sh` cases that exercised them. `--system-library` is the one native path; `tests/system-abi.sh` covers scalar, aggregate, variadic and callback signatures through it.

```
$ bin/llvmnet --runtime=system tests/c/smoke.c --native-import 'abs=libc.so.6!abs' -o x.dll
llvmnet: error: option '--native-import' is not accepted: it was removed (ADR-04, SA-01); --runtime=system resolves every native function through generated bridges, and --system-library <shared-library> adds a library.
(exit status 2)
```

`grep -rn 'native-import\|NativeImport' src tests` finds only the rejection and its two tests (`tests/modes.sh`, `tests/LlvmNet.UnitTests/DriverTests.cs`). Gates after the change: `tests/run.sh --tag core --tag toolchain --exclude-tag slow -j 8` 83 passed, 0 failed in 507 s; `dotnet test tests/LlvmNet.UnitTests -c Release` 86 passed; `dotnet format whitespace llvmnet.slnx --verify-no-changes` and `shellcheck -S error` clean.

## TC-01: table-driven driver options

The driver's options are one table (`src/LlvmNet.Compiler/DriverOptions.cs`, 97 rows plus 47 for `-Wl,` options) that says for each option how it is spelled (flag, separate value, joined value, comma-joined) and what the driver does with it in each runtime mode: handled, passed to the frontend, accepted without effect (with the reason `-v` prints), or rejected (with the reason the error prints). The frontends' own option spellings (1,316 names from `clang-22 --help` and `flang-22 --help`) are generated into `FrontendOptions.cs` by `scripts/frontend-options.py`, so a value given as the next argument is never taken for an input and a joined spelling such as `-object-file-name=x` is never read as `-o`. `--help` and the option tables of `docs/user/driver.md` are generated from the table; `tests/driver-reference.sh` fails and regenerates the page when it is out of date.

`tests/driver-options.sh` (`tests/driver/options.py`) feeds every option the frontends list to `llvmnet --runtime=<mode> <option> [value] -c <source>` in both modes, with a value chosen from the help line's placeholder, and classifies the result. 2,970 runs in 39 s with 8 jobs, no failure:

| Frontend, mode | Options | Works | Frontend's own error | Not accepted, with reason | Other driver error |
| --- | ---: | ---: | ---: | ---: | ---: |
| Clang, portable | 1,260 | 776 | 222 | 246 | 16 |
| Clang, system | 1,260 | 879 | 353 | 12 | 16 |
| Flang, portable | 225 | 154 | 29 | 38 | 4 |
| Flang, system | 225 | 164 | 54 | 3 | 4 |

"Works" is exit 0 with the object written (or an option that stops before one on purpose, such as `-E` or `-###`). "Frontend's own error" is Clang's or Flang's diagnostic for an option that does not apply or a placeholder value it does not accept (`-std=1`), passed through with its exit status. "Not accepted" is the driver's own rejection: in portable mode the 234 `-m<option>` spellings and the target, sysroot and layout options that would change the ABI, in both modes the native linker, assembler, sanitizer and profiling options. The 16 other driver errors are the options that make the frontend write something other than bitcode (`--analyze`, `-emit-ast`, `--precompile`, `-extract-api`, `-fmodule-header`) or nothing at all (`-fdriver-only`, `-help`, `-print-targets` and the other `-print-` queries when combined with an input), each reported as "the frontend's output smoke.o is not LLVM bitcode" or "the frontend did not write smoke.o". A `FAIL` class (an internal error, a stack trace, a timeout, exit 0 without output, or a value mistaken for an input) is empty; the first sweep found one real bug, `-march=` and the other Clang-listed `-m` spellings bypassing the portable-mode rule because the generated spelling was a longer match than the `-m` rule, fixed by letting a rejection rule cover the spellings under it.

```
$ bin/llvmnet --runtime=portable -m32 -c tests/c/smoke.c
llvmnet: error: option '-m32' is not accepted in portable mode: -m<option> selects a CPU, instruction set or native ABI detail, and it would change the versioned portable ABI (dotnet64-v2), whose target, data layout and headers are fixed; use --runtime=system for a native target; -mllvm, -mmlir, -mtune= and the -module... options are the exceptions.
$ bin/llvmnet -v -fPIC -Wl,-rpath,/opt -static -c tests/c/smoke.c -o x.o
llvmnet: note: option '-fPIC' has no effect: position independence is a property of native code; CIL has none.
llvmnet: note: option '-static' has no effect: a portable program is always one statically linked image.
llvmnet: note: option '-Wl,-rpath' has no effect: there is no native loader path to record.
clang-22 "-O1" "-fno-vectorize" "-fno-slp-vectorize" "--target=x86_64-unknown-none" ...
```

New with the table: `-###` prints the frontend commands and stops; `--driver-mode=g++|flang|gcc` selects the frontend; `-x` applies per input, so `-x c++ a.c -x c b.c` compiles each as named; `-l:<file>`; `-Wl,--whole-archive` and `-Xlinker`; `-nostdlib++`; `-c` on `.o` inputs; and `-mllvm` is accepted in portable mode. Gates: `tests/run.sh --tag core --tag toolchain --exclude-tag slow -j 8` 85 passed, 0 failed in 525 s (the two new tests included); `dotnet test tests/LlvmNet.UnitTests -c Release` 108 passed (the table's rows are checked for help text, reasons and unique names, and 21 rejections and parses are checked directly); format and shellcheck clean.

## TC-02: link inputs

`tests/link-inputs.sh` (core tag, both modes) exercises every link input the item names, with fixtures under `tests/c/link/`:

- `-c` on a `.ll`, a `.bc` and a `.o` input produces an object for the mode (IR is compiled and tagged, an object keeps its tag); an object of the other mode is rejected by `-c` with the same `ABI mismatch` as by a link.
- `-l:example.a` finds the archive by its exact name; a missing `-lmissing` names the `-L` directories searched.
- `libneeds.a` needs `provided()` from `libprovides.a`, which is named before it: the link succeeds, because archives are rescanned until no member resolves a symbol, regardless of order (GNU ld would need `--start-group`).
- An archive whose only member nothing references stays out; with `-Wl,--whole-archive` (or `-Xlinker --whole-archive`) its constructor runs.
- A duplicate definition names both files, for objects and archive members:

```
llvmnet: error: duplicate definition of 'calculate' in dup.o; it is already defined in library.o
llvmnet: error: duplicate definition of 'calculate' in libdup.a(dup.o); it is already defined in library.o
```

- `-r main.o -l:example.a` writes a relocatable object holding the needed member and nothing from the runtime libraries (before this change a portable `-r` also linked `crt1.o` and the sysroot archives); `-r -Wl,--whole-archive -lctor` writes every member; `-r -lctor` alone fails asking for a link root.
- `-nostdlib` with a freestanding `main` exits 42 (the entry point calls `main` directly), `-nodefaultlibs` with the sysroot's `libc.a` and builtins named explicitly makes an ordinary program again, without them the link fails with `Unresolved function`, and `-nostartfiles` alone runs a `main` that only returns.

The rules are written down in [user/driver.md](../user/driver.md#link-inputs). The test passes in both modes (18 s).

## TC-03: optimization levels

ADR-11's default, `-O1` without automatic vectorization, and its difference from Clang's `-O0` are documented in [user/driver.md](../user/driver.md#defaults). `tests/optimization-levels.sh` checks with `-###` that the frontend receives `-O1 -fno-vectorize -fno-slp-vectorize` by default, `-O3` without the `-fno-` options when `-fvectorize -fslp-vectorize` is given, and the user's `-O0` after the default. It then compiles six fixtures (`c/core.c`, `c/intrinsics.c`, `c/byval.c`, `c/unaligned.c`, `cpp/core.cpp`, `fortran/core.f90`) at `-O0`, `-O1`, `-O2`, `-O3`, `-Os`, `-Oz` and `-O3 -fvectorize -fslp-vectorize` (Flang: `-O0` to `-O3`) in both modes: 80 images, each with stdout and exit status identical to the native build at the same level, and each `main` carrying `optnone` at `-O0` and not at `-O3` (Clang's marks; Flang sets none). Every method of the 24 `-O0` and `-O3` images JIT-compiles (`LlvmNet.Checks jit-all`: 1,610 methods, 0 failures), so unoptimized output, with its many locals and allocas, is verifiable in the FD-15 sense. 166 s.

## TC-04: language and codegen options

`tests/language-options.sh` (both modes):

- `-g`: the diagnostic for computed goto names the file and line (`tests/c/computed-goto.c:2: error: ...` with `note: in function 'main'`); without `-g` the note says to compile with `-g` for a location.
- Language defaults per alias equal the frontend's own (`tests/c/dialect.c` prints `c 201710` under `llvmnet-clang` and `c++ 201703` under `llvmnet-clang++` or `--driver-mode=g++`, as `clang-22` and `clang++-22` do); `-std=c11` and `-std=c++20` override.
- `-fno-exceptions -fno-rtti`: `tests/cpp/no-eh.cpp` (virtual dispatch, templates, `std::vector`, `std::unique_ptr`) matches native output under the JIT and NativeAOT, with no `invoke` or `landingpad` in the program's functions and no `type_info` for its classes.
- `-fvisibility=hidden`: a managed library built from `tests/c/visibility.c` exports `exported_function exported_user` (`LlvmNet.Checks inspect` now lists the exports); a client resolves them through `--cil-reference` and fails to resolve `hidden_function`. Without the option every external definition is exported and the static one never is. Before this change a library exported every function, static ones included.
- `-msse4.2` is rejected in portable mode with `option '-msse4.2' is not accepted in portable mode: ...` and accepted in system mode.

45 s.

## TC-05: diagnostics pipeline

`tests/diagnostics.sh` compiles `tests/c/undeclared.c` with `clang-22` and with `llvmnet` under six option sets (none, `-fcolor-diagnostics`, `-fno-color-diagnostics`, `-fdiagnostics-format=msvc`, `-fdiagnostics-format=vi -fno-show-column`, `-ferror-limit=1 -Wall -Wextra`) in both modes: stderr is identical byte for byte, including the ANSI color sequences, and the exit status is the frontend's. A broken Fortran source gives the same result with `flang-22`. llvmnet's own errors use Clang's form (`file:line: error: ...`, then `llvmnet: note: ...` lines) and, with `-fcolor-diagnostics` or on a terminal, Clang's colors (`Diagnostics.cs`: bold location, bold red `error:`, bold message); `-fno-color-diagnostics` turns them off. A usage error is one `llvmnet: error:` line with exit status 2, a link error exits 1 naming the unresolved symbol, and a stack trace appears only with `LLVMNET_TRACE=1`. The exit codes are documented in [user/driver.md](../user/driver.md#exit-status).

## TC-12: NativeAOT provisioning

`scripts/nativeaot-feed.sh artifacts/tests/aot-offline/feed linux-x64` fills a folder feed with the six packages a publish restores (ILCompiler 10.0.14 and its linux-x64 package, the NETCore and NativeAOT runtime packs, the ILLink tasks, the ASP.NET runtime pack the SDK's restore graph includes), from the user's package cache or NuGet. `tests/aot-offline.sh` then publishes `tests/c/core.c` with `LLVMNET_NUGET_FEED` set to that feed and `NUGET_PACKAGES` set to an empty directory: the publish succeeds from the feed alone (the generated project's `nuget.config` clears every other source), and `--aot-project` records the feed too. The prerequisite check (`Toolchain.CheckNativeAotPrerequisites`) runs before `dotnet publish` and reports each missing piece in one line, checked by the test with a stand-in `dotnet` that has only the runtime, an empty feed, a `PATH` without a C compiler, and a `win-x64` target:

```
llvmnet: error: NativeAOT prerequisite missing: the .NET 10 SDK. '.../runtime-only-dotnet --list-sdks' reports no SDK (only a runtime is installed); install the .NET 10 SDK, not just the runtime.
llvmnet: error: NativeAOT prerequisite missing: the offline feed .../empty-feed has no microsoft.dotnet.ilcompiler package; fill it with "scripts/nativeaot-feed.sh .../empty-feed linux-x64" on a host with access to NuGet.
llvmnet: error: NativeAOT prerequisite missing: a C compiler and linker for linux-x64. Install clang (Debian and Ubuntu: apt-get install clang zlib1g-dev); the SDK runs 'clang' or 'gcc' from PATH, or the one named by CppCompilerAndLinker.
llvmnet: error: NativeAOT prerequisite missing: win-x64 executables cannot be published from linux-x64. Write the project with --aot-project <dir> and run "dotnet publish NativeHost.csproj -c Release -r win-x64" on a Windows host.
```

The check also probes the native linker with a one-line program (`-lz` included, `--target=aarch64-linux-gnu` for the cross target) and names zlib or the cross sysroot when that fails. 14 s with the packages cached.

## Gate for TC-02 to TC-05 and TC-12

The five items were tested together in one run of the working tree holding all of them: `tests/run.sh --tag core --tag toolchain --exclude-tag slow -j 8` 90 passed, 0 failed in 596 s (the five new tests included; `link-inputs` 18 s, `optimization-levels` 191 s, `language-options` 53 s, `diagnostics` 3 s, `aot-offline` 14 s); `dotnet test tests/LlvmNet.UnitTests -c Release` 108 passed; `dotnet format whitespace llvmnet.slnx --verify-no-changes` and `shellcheck -S error` clean. The items were then committed one by one.

## TC-13: bundle size

The bundled toolchain (`scripts/package-toolchain.sh <prefix> --self-contained --bundle-llvm`) already held only the tools the item names (`clang-22`, `clang++-22`, `flang-22`, `opt-22`, `llvm-ar-22`, `llvm-nm-22`, `llvm-ranlib-22`), the libraries they load (`libLLVM.so.22.1`, `libclang-cpp.so.22.1`, `libMLIR.so.22.1`), Clang's resource directory and Flang's `.mod` files; Debian's binaries are already stripped. The one large surplus was the resource directory: its `lib/` holds 77 MB of sanitizer and profiling runtimes, which llvmnet rejects at the driver (`-fsanitize=`, `-fprofile-instr-generate` are not accepted, TC-01). The script now copies only its `include/`, strips the copied binaries and the LLVM bridge (`--strip-unneeded`) and prints the installed size. Measured with `du -sm` on the package the deployment test built before the change (`artifacts/tests/deployment/toolchain`) and on the one `scripts/release-artifacts.sh` built after it:

| Part | Before (MB) | After (MB) |
| --- | ---: | ---: |
| `lib/llvmnet` (the compiler with its own .NET runtime) | 80 | 80 |
| `libexec/llvm/bin` | 49 | 49 |
| `libexec/llvm/lib` without the resource directory | 307 | 307 |
| `libexec/llvm/lib/clang/22` (the resource directory) | 93 | 16 |
| `sysroot` (the two sysroots and the SIMD128 header) | 81 | 81 |
| `bin`, `share`, `libexec/llvm/include` | 3 | 3 |
| Whole bundled installation | 609 | 533 |
| Thin installation (no LLVM) | | 161 |

Compressed with `xz -6` for the release archives: the bundled package is 100,432,388 bytes (96 MiB) and the thin one 33,406,532 bytes (32 MiB). What remains is the LLVM libraries themselves: `libLLVM` (144 MB), `libMLIR` (104 MB, Flang's dependency) and `libclang-cpp` (61 MB), which cannot be trimmed without rebuilding LLVM with fewer components, and the compiler's .NET runtime (80 MB), which `--self-contained` includes so that the package needs no `dotnet` to compile.

## TC-11: release artifacts

`scripts/release-artifacts.sh artifacts/m7-release` on commit `3692436` (a development build, so the version carries the commit) produced:

```
01971137ef541ca2585e4685c0aa97a27472fd702e7a8ec46193ec6688bb1bee  llvmnet-0.3.0-dev+36924368816e-linux-x64-llvm.tar.xz   (100,432,388 bytes)
3708a868109b8fd1e5424527d3f6f298498081c8ba621e7451d30867ef9067e3  llvmnet-0.3.0-dev+36924368816e-linux-x64.tar.xz        (33,406,532 bytes)
3bf19bd96265854c0d55c32c97b6733917ebd0c208c14a07c9087505157eabfc  llvmnet.0.3.0-dev.nupkg                                (19,464,427 bytes)
```

The dotnet tool package holds the compiler, `LlvmNet.Runtime.dll`, the LLVM bridge and the three sysroots (1,931 files, 80 MB unpacked); `dotnet tool install --tool-path <dir> llvmnet --add-source artifacts/m7-release --version 0.3.0-dev` installed it, and the installed `llvmnet` compiled and ran `tests/c/core.c` and, with `--driver-mode=g++`, `tests/cpp/core.cpp`.

The artifacts are built to be a function of their contents: `tar --sort=name --owner=0 --group=0 --numeric-owner --mtime=@$SOURCE_DATE_EPOCH` (the commit time) with the atime and ctime headers dropped, and `xz -T0 --block-size=32MiB -6`, so the thread count does not change the stream; the .NET build is deterministic and the version string comes from the commit; the tool package, whose generated entries NuGet stamps with the build time, is rewritten as a zip with the commit time and a fixed entry order. `tests/release.sh` builds the artifacts twice and compares `SHA256SUMS`: with version `0.0.0-test+36924368816e`, both builds gave

```
6fd23e5c1df226f41f6c3c19e9f7e53956192d9fc88643e29f9169819c5d3c26  llvmnet-0.0.0-test+36924368816e-linux-x64-llvm.tar.xz   (96 MiB)
e0487f3dbfb65afe75b2cde5f494a752adde5098853910882ff5a4cf3e9ef808  llvmnet-0.0.0-test+36924368816e-linux-x64.tar.xz        (32 MiB)
9f0915adba5300402a5d5a77093e45c36c7e6653e4fd50fffa51814879ae9016  llvmnet.0.0.0-test.nupkg                                (18 MiB)
```

in 67 s, and the tool installed from the first build compiled `tests/c/core.c` and `tests/cpp/core.cpp`. Two earlier runs of the test found what was not reproducible: a page under `docs/` edited while the test ran (the package copies the documentation, so a release builds from a committed tree), and NuGet's entry timestamps, fixed as described.

`.github/workflows/release.yml` runs on a `v*` tag in the same Debian 13 container as the PR gate: the unit tests, the core and toolchain tags with the full package and deployment test, `scripts/release-artifacts.sh` twice with a comparison of the sums, then `cosign sign-blob --yes --bundle` on every file (Sigstore's keyless signing with the workflow's GitHub OIDC identity, no key to keep), `cosign verify-blob` against the repository's workflow identity, and `gh release create` with the artifacts, the Sigstore bundles and the CHANGELOG section as the notes. **Blocked:** a signature and a release need the workflow to run on GitHub, which the repository has not reached (FD-19); the workflow was validated as far as it can be here (the same commands, without the signing and release steps). Publishing the tool package to nuget.org and the choice of keyless signing over a minisign key are [proposed for the owner](../plan/decisions.md#proposed-for-the-owner-2026-10-31).

Gate for TC-13 and TC-11: `tests/run.sh --tag core --tag toolchain --exclude-tag slow -j 8` 90 passed in 474 s; the one failure was `tests/limitations.sh`, the TC-17 test already in the working tree whose runtime part was not yet applied (its `--print-host-limitations` option did not exist). `tests/run.sh deployment release`: both passed (12 s and 67 s). Unit tests 108 passed; format and shellcheck clean.

## TC-17: limitations page

`docs/user/limitations.md` is generated by `scripts/limitations.py` from two pieces of code and fixed text: the rejected rows of the support matrix (`docs/user/support-matrix.md`, itself generated from probes, BE-02: 28 of the 212 probed constructs are rejected, each with its diagnostic) and the runtime's new table of the services it refuses by name, `HostSyscalls.Unavailable`, printed by `llvmnet --print-host-limitations` (16 rows: process creation and replacement, process groups, tracing and namespaces, affinity, remote process memory, sockets, hard links, device files and FIFOs, extended attributes, inotify, splice, epoll, the eventfd family, System V and POSIX message-queue IPC, interval timers; each with its errno and the reason). The dispatcher consults the same table before its switch, so the errno a program gets and the page come from one place; before, `fork`, `link`, `mknod` and `setsid` were cases in the switch and sockets, epoll and the rest fell into the unnamed `-ENOSYS` default. `tests/limitations.sh` regenerates the page and fails when the committed one differs, and runs `tests/c/unavailable.c`, which checks that `fork`, `socket`, `epoll_create1` and `shmget` fail with `ENOSYS` and `link`, `setsid` and `mkfifo` with `EPERM`. The prose lists in `docs/user/limitations.md` are replaced by a pointer to the page. `tests/host.sh` (188 checks against native) still passes with the dispatcher change. Gate: `tests/run.sh --tag core --tag toolchain --exclude-tag slow -j 8` 91 passed, 0 failed in 533 s (the new `limitations` test included); unit tests 108 passed; format and shellcheck clean.

## TC-16: documentation restructure

`docs/` now has three parts, and `README.md` is written for users:

| Directory | Pages |
| --- | --- |
| `docs/user/` | `getting-started.md`, `driver.md` (tables generated from the option table), `modes.md` (the runtime modes and ABI contracts, from the former `toolchain.md`), `linking.md`, `deployment.md` (output forms, supported hosts, NativeAOT and provisioning, the package layout, release packages and their verification), `limitations.md` (generated), `troubleshooting.md`, `support-matrix.md` (generated), `simd128.md` (moved) |
| `docs/dev/` | `architecture.md` (moved), `host-abi.md`, `calling-convention.md`, `cxx-eh-bridge.md`, `versioning.md`, `artifacts.md`, `testing.md` (the runner, tags, oracles, nightly suites), `adding-a-host-function.md`, `release-process.md`, `spec.md` (the former `spec-coverage.md` and `spec-portable-limitations.md` merged with the SPEC configuration section of `toolchain.md`), `ffmpeg.md` (moved from `ffmpeg-threading.md`), `video-benchmark.md` (moved from `video-simd128-performance.md`) |
| `docs/evidence/` | the dated records, unchanged |

`docs/toolchain.md`, `docs/status.md` and `docs/roadmap.md` are removed: their content is in the user pages, the generated limitations page and the plan. The moves keep history (`git mv`). Every reference was rewritten: the plan files, the evidence files, `tests/run.sh` (a prerequisite message), `tests/portable-spec-causes.sh`, the comment in `Toolchain.cs`, `CHANGELOG.md` and `CONTRIBUTING.md`. A link checker over every `.md` file in the repository (relative targets resolved against the file's directory; 0 broken links after the change, from 2 before, which were `driver.md`'s forward references to the pages that now exist). `tests/run.sh --no-build driver-reference limitations support-matrix portable-spec-causes`, the tests that read the documentation, pass. `README.md` keeps its claims tied to evidence: the status paragraph points at `docs/evidence/` and `docs/user/limitations.md` rather than stating numbers. Gate: `tests/run.sh --tag core --tag toolchain --exclude-tag slow -j 8` 91 passed, 0 failed in 472 s; unit tests 108 passed; format and shellcheck clean.

## M7 close-out

Every item the M7 exit names is done: TC-01 to TC-05, TC-11 to TC-13, TC-16, TC-17 and SA-01, each with its test in the gate and a section above. The exit's "signed packages produced by CI from a tag" is implemented in `.github/workflows/release.yml` and checked locally as far as it can be (reproducible artifacts, the tool installs); the signing and the release itself happen on the first tag once the repository is on GitHub (FD-19), which the status board records. Three questions are proposed for the owner in `docs/plan/decisions.md`: publishing the tool package to nuget.org, keyless signing versus a minisign key, and the release naming.
