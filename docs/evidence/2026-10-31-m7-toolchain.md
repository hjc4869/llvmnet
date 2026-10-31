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
