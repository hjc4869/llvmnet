# Evidence: what a bootstrap build costs, where the time goes, and four fixes (XP-16, XP-17, PF-30; 2026-11-16)

Build host: Linux x86-64 (Debian 13, kernel 6.12), AMD Ryzen AI MAX+ 395 (16 cores, 32 threads), 124 GB RAM, .NET SDK 10.0.404 with runtime 10.0.15, LLVM/Clang 22.1.8 (Debian), master `209eb23` with the pin moved to v0.3.0-dev.5 in the working tree. Another project's builds ran on the host for most of the session, so times carry noise: instruction counts (`perf stat`) are given beside them, and the variants of a comparison ran back to back. The harness, the sample, the logs and the profiles are in the gitignored `artifacts/bootperf/`.

## What was asked

After the release workflow moved to the bootstrap build (XP-11), its steps "Build LlvmNet.Llvm.dll" and "Build clang, flang, opt, llvm-ar and llvm-nm" went from 1 min 24 s and 8 min 11 s to 35 min 42 s and 2 h 25 min 21 s. The owner asked how the pinned release's toolchain, with its NativeAOT tools, compares with the native one, where the gap comes from, and for fixes.

## Two things are in those step times

**A cold cache, which is most of it.** 1 min 24 s and 8 min 11 s are a run that compiled nothing: the library's two links, or one, and the five tools' links. The workflow's cache of the LLVM build tree is keyed by the way LLVM is compiled (`selfhost-bootstrap-…` or `selfhost-native-…`), so the first bootstrap run found no tree of its kind and compiled all of LLVM, Clang, MLIR and Flang, about 5,300 objects, on four cores: 3 h 1 min for the two steps is 12 core-hours, 8 s an object. A native build from an empty cache takes most of that too. The run after it restores the tree (the workflow's logs cannot be read from this host, so this rests on the cache keys and on the arithmetic), and so does the run with the new pin, v0.3.0-dev.5: its sysroot's header hash is v0.3.0-dev.3's (`9a3edcfa…`), which `settle_sysroot_headers` reads, and its files carry its commit's time, 03:25 on 2026-11-16, which is earlier than the objects that release's own run compiled.

**The toolchain's own cost, which is the rest.** Measured below: 1.23 times the native toolchain's CPU time over a sample of the build (1.31 as the geometric mean over its objects, because a small object pays relatively more), which on a cold build of 3 hours is about half an hour.

## How it was measured

- **The sample.** 24 objects of the native build tree (`artifacts/selfhost-build/llvm`), chosen evenly along the build's cumulative compile time in ninja's log, so that the sample weighs objects as the build's time does: 5 of LLVM, 4 of Clang, 8 of MLIR and 7 of Flang, from `ValueSymbolTable.cpp` (1 s of CPU) to Flang's `check-expression.cpp` (50 s). MLIR and Flang are two thirds of the build's time.
- **The compile.** Each object's own command from `compile_commands.json`, run through a toolchain's `llvmnet-clang++`, so the driver's part is in it; CPU time is user plus system (`/usr/bin/time`). The variants of one object run back to back in one job, their order rotated from object to object, three jobs at a time, each on a CPU of its own.
- **The toolchains.** Native: this checkout's compiler on Debian's Clang and LLVM. v0.3.0-dev.5: the pinned `linux-x64` package as the workflow uses it. The others are that package with one part replaced.

## Where the time goes

| Part of one compile (`lib/IR/Instructions.cpp`, `-O3`) | Native | v0.3.0-dev.5 |
| --- | ---: | ---: |
| Clang (v0.3.0-dev.5's rebuilt here with symbols) | 2.24 s, 13.8 billion instructions | 2.79 s, 22.6 billion |
| The driver (start, and the ABI tag after Clang) | 0.08 s | 0.20 s |

**1. The tools were NativeAOT executables of JIT images.** The release published each tool's executable from the JIT image of the universal package (`llvmnet --write-aot-project`). The compiler translates differently when it links for NativeAOT, and a JIT image has none of it: `Program` has a type initializer, which NativeAOT cannot run at build time, so every method tests on entry whether it has run, and again before reads of globals (PF-26); every indirect call, each C++ virtual call, tests its target for a fat function pointer (PF-27); and a function with a loop keeps `localloc` for its stack allocations, with a frame pointer, a stack probe and a cookie (PF-24). Linked with `--aot-project` from the same objects by the same compiler, Clang executes 18.7 billion instructions for that object instead of 22.6 and takes 2.37 s: 1.06 times Debian's, where the published one took 1.24 times.

The project of a link also asks for x86-64-v2 on x64, where `--write-aot-project` left the SDK's default. That is not what helps: the JIT image published for x86-64-v2 executes the same 22.6 billion instructions (2.67 s), and the NativeAOT link published at the baseline executes as many as at x86-64-v2 (17.14 and 17.09 billion with PF-30's helpers; 2.26 and 2.29 s). The tools' projects therefore keep the baseline, and the packages run on the processors they ran on. `IlcOptimizationPreference=Speed` does not help either (18.7 billion, 2.40 s).

**2. The driver parsed and rewrote every object.** After Clang wrote the object, the driver's in-process LLVM read it, added the `llvmnet.abi` module flag and wrote it again. On Debian's LLVM that is 0.05 s. On the package's managed library it is 0.2 s, and little of it is LLVM's work: of the driver's samples 37% are in `libclrjit.so`, 31% in `libcoreclr.so` and 6% in the library's precompiled code. The library is precompiled with ReadyToRun, but crossgen2 leaves out the methods this needs most, and the runtime compiles 206 of them (760 KB of code) in every driver process:

- crossgen2 compiles no method that makes a call passing a `Vector128<T>` or a `UInt128` to a helper of `LlvmNet.Runtime.dll`, another version bubble, where it holds the layout of those types to be unstable ("Abi unstable type … requires runtime JIT"): 461 methods by `Vectors.Store(nint, Vector128<byte>, int)` alone, which stores a vector of less than 16 bytes, and 561 of the library's 20,400 in all, `ModuleBitcodeWriter::write` and `BitcodeReader::parseConstants` among them. LLVM's `-O3` code stores short vectors wherever it copies a small aggregate.
- It skips 17 of the largest methods ("it is skipped"), which the JIT then compiles with minimal optimization for their size: `BitcodeReader::parseFunctionBody` (100 KB of IL), `parseModule`, `MetadataLoaderImpl::parseOneMetadata`, `LLVMContextImpl`'s destructor and the static initializer of `TargetLibraryInfo.cpp` (297 KB of IL, 237 KB of code at every start).

**3. Wide and odd-width integers went through loops.** In profiles of the NativeAOT Clang (`perf record`, with symbols), 84 to 92% of the samples are in Clang's and LLVM's own translated code and the rest in what they call. Of that rest, the runtime's helpers for integers of 65 to 128 bits and of 3, 5, 6 or 7 bytes stood out: `WideInteger.Read` built the value from single bytes with a 128-bit shift for each, `Write` did the reverse, and `Binary` sign-extended both operands before it looked at the opcode; with `UInt128`'s shift operators they took 4.0% of the samples of a Flang object (`ConvertConstant.cpp`, 28 s) and 3.7% of `Instructions.cpp`. LLVM makes such integers when it merges adjacent bit-fields and small aggregates: the constructors of Clang's AST nodes (`CallExpr`, `DeclRefExpr`, `ImplicitCastExpr`), `computeDependence`, and `xxh3_64bits` with its 128-bit products. `Numeric.CountBits`, behind `llvm.ctlz`, `llvm.cttz` and `llvm.ctpop`, was a call (0.5%), and `memcmp` is musl's loop over single bytes (1.5%, where glibc's takes 0.5% of the native Clang's).

**What is not a gap.** For the same Flang object, Clang's and LLVM's own code takes 23.4 s translated and 22.7 s natively, 3% apart. mimalloc takes half of what glibc's allocator takes (2.1% of the samples against 4.1%). Starting the NativeAOT Clang takes 17 ms, the native one 13 ms.

## The fixes

- **XP-16, the tools linked for NativeAOT.** `scripts/selfhost/build-aot-projects.sh` links the five tools a second time with `--aot-project` (`link_with_checkout` with a project directory, the same ninja link command), and the release workflow uploads those projects for the `aot-tools` jobs instead of ones written from the packaged JIT images; `precompile-toolchain.sh` takes them from `LLVMNET_AOT_PROJECTS`. A release pays five more links for it (below).
- **XP-17, the ABI tag written by Clang.** The sysroot has a stamp for each ISA level (`lib/abi/<level>.bc`, made by `build-sysroot.sh` with the driver itself): a module with the portable target and data layout and nothing but the flags. The driver passes it to Clang with `-Xclang -mlink-builtin-bitcode`, which links it into the object, and finds the flag's name in the bytes Clang wrote instead of parsing them; anything else (Flang, LLVM IR, textual output, system mode, a sysroot without stamps) is tagged as before. A C or C++ compile no longer loads LLVM in the driver: `tests/abi-stamp.sh` compiles with `LLVMNET_LLVM` naming a file that does not exist. The object has the flags and the IR of the driver's tagging; it is not the same bytes, because reading a module and writing it again reverses its use lists.
- **PF-30, the helpers.** `WideInteger`'s loads, stores, masks, sign extension, arithmetic and comparisons work on the two 64-bit halves and are inlined, as is `Numeric.CountBits`; the compiler passes their width and operation as constants, so a load of an `i96` is two loads and an `and` of two is two instructions (`DOTNET_JitDisasm` of callers with constant widths). 8.4 million comparisons with the previous implementation over every width, opcode and predicate agree, and `tests/semantics.sh` passes (47,795 probes, the JIT and NativeAOT). The portable C library's `memcmp` compares eight bytes at a time (`ports/musl/src/string/llvmnet/memcmp.c`; 200,000 random comparisons agree with glibc's).
- **The pin's file times.** `bootstrap.sh` replaced the prepared toolchain with the new package's files, which carry the time of that release's commit. That is later than objects an earlier run compiled and cached, and the native TableGen build depends on the package's Clang headers (14 of them for `APInt.cpp`), so a later pin would recompile its 535 objects; and it can be earlier than objects compiled against a header the new pin changed, which ninja would then keep. A file the new package has unchanged now keeps the time it had, and every other file gets the present time.

## After

**The sample.** CPU seconds for the 24 objects; the ratio of the totals is what the build costs, and the geometric mean weighs every object alike:

| Toolchain | CPU | Ratio of totals | Geometric mean |
| --- | ---: | ---: | ---: |
| Native: this checkout on Debian's LLVM | 249.2 s | 1.00 | 1.00 |
| v0.3.0-dev.5, the pin | 307.4 s | 1.23 | 1.31 |
| with Clang linked for NativeAOT (XP-16) | 270.5 s | 1.09 | 1.16 |
| and the runtime's helpers (PF-30) | 261.6 s | 1.05 | 1.11 |
| and `memcmp`: the package's Clang | 259.1 s | 1.04 | 1.10 |
| The package of this working tree, with its driver (XP-17) | 254.2 s | 1.02 | 1.06 |

The three rows after the pin replace only Clang in the pinned package; the last is the `linux-x64` package made from this working tree by the release's own steps (below). Every object of every row has the IR of the native toolchain's, 24 of 24 with the toolchains' directories and Clang's name masked: the objects Clang tags are the objects the driver tagged, and a Clang on the new helpers compiles what Debian's compiles. Peak memory is 2.84 GB for the largest object against 2.67 GB.

**One object.** `Instructions.cpp` takes Debian's Clang 13.8 billion instructions, v0.3.0-dev.5's 22.7, the Clang linked for NativeAOT 18.7, with the helpers 17.1 and with `memcmp` 16.9; the four built by llvmnet write the same file, which differs from Debian's in the compiler's name.

**The driver alone**, around a stand-in for Clang that copies a compiled object: 0.20 s in v0.3.0-dev.5, 0.045 s in the new package, and 0.06 s for the checkout on Debian's LLVM (0.08 s before).

**The release's steps on this working tree**, with Debian's LLVM as for `llvm: native`: `build-library.sh` 102 s, `build-tools.sh` 658 s, `build-aot-projects.sh` 595 s, `aot-tools.sh publish` of the five projects for `linux-x64` 649 s, and `package-toolchain.sh --portable --precompile=linux-x64` with those executables. The package is 449 MB (v0.3.0-dev.5's 478 MB): Clang 109 MB (118), Flang 130 MB (144), `opt` 56 MB (62), `llvm-ar` 13.2 MB (13.9), `llvm-nm` 14.6 MB (15.5). `tests/portable-toolchain/` passes with it on Linux x64, 62 of 62 (C, C++ and Fortran fixtures, launchers, archives, modules). A NativeAOT link made as a bootstrap build makes it, this checkout's compiler on its managed `LlvmNet.Llvm.dll` with v0.3.0-dev.5's Clang and `opt`, gives `llvm-nm`'s image byte for byte as the link on Debian's LLVM does. `bootstrap.sh`, given a prepared toolchain with one changed header and older times on two unchanged ones, kept the two times and gave the changed header the present time.

**Tests.** The core gate passes, 111 tests in 581 s, with the new `abi-stamp` among them, `semantics`, `abi-freeze` (the header set is unchanged) and the NativeAOT tests.

**What a release costs with this.** With a warm cache, the five NativeAOT links more: 10 minutes here, about as long as the tools' links as JIT images, which took the runner 8 min 11 s. A cold bootstrap build costs what it did until a release with these changes is the pin; from then on about 1.02 times a native one.

## Not done

- **ReadyToRun's gaps (XP-18, open).** The precompiled compiler still JIT-compiles those 206 methods whenever it loads its LLVM, which is now at a link, not at a compile, and for Fortran, whose frontend has no option to link a module in. So does every ReadyToRun image of a program with vector code or wide integers.
- **One link for both forms.** The second link of each tool repeats the bitcode link and `opt` of the first; translating the linked module twice in one run would save part of a release's extra links.
- Flang as a compiler and `opt` were not timed (LLVM's build compiles no Fortran, and `opt` runs once in a link); the kit runs them. The packages for macOS and Windows were not made here.
- The release workflow has not run with these changes, and the pin that would bring them to the bootstrap build is the release after the one that has them.
