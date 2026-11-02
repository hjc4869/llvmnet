using System.Text;
using LlvmNet.Runtime;

namespace LlvmNet;

/// <summary>How an option's value is spelled, in Clang's terms.</summary>
internal enum OptionKind
{
    /// <summary>No value: <c>-c</c>.</summary>
    Flag,
    /// <summary>The value is the next argument: <c>-Xclang -foo</c>.</summary>
    Separate,
    /// <summary>The value follows the name in the same argument: <c>-std=c11</c>, <c>-O2</c>.</summary>
    Joined,
    /// <summary>Either spelling: <c>-o file</c>, <c>-ofile</c>; long names join with <c>=</c>: <c>--runtime=system</c>.</summary>
    JoinedOrSeparate,
    /// <summary>A joined part and a separate value: <c>-Xarch_x86_64 -foo</c>.</summary>
    JoinedAndSeparate,
    /// <summary>Comma-separated values after the name: <c>-Wl,-rpath,/opt</c>.</summary>
    CommaJoined
}

/// <summary>What the driver does with an option, per runtime mode.</summary>
internal enum Handling
{
    /// <summary>llvmnet implements it.</summary>
    Handled,
    /// <summary>Passed to Clang or Flang unchanged.</summary>
    Forwarded,
    /// <summary>Accepted and dropped, with a documented reason (printed as a note with <c>-v</c>).</summary>
    Ignored,
    /// <summary>Fails with the documented reason.</summary>
    Rejected
}

/// <summary>
/// One row of the driver's option table (<see cref="DriverOptions.Table"/>). The table is the single description of
/// what llvmnet accepts: the parser, <c>--help</c>, the reference page (<c>docs/user/driver.md</c>) and the error
/// messages are all produced from it.
/// </summary>
internal sealed record OptionSpec(string Name, OptionKind Kind, string Group, string Help, Handling Portable, Handling System, string? Note = null, string Value = "<value>")
{
    internal OptionSpec(string name, OptionKind kind, string group, string help, Handling both, string? note = null, string value = "<value>")
        : this(name, kind, group, help, both, both, note, value) { }

    internal Handling For(string runtime) => runtime == "system" ? System : Portable;

    /// <summary>The spelling shown in <c>--help</c> and the reference.</summary>
    internal string Spelling => Kind switch
    {
        OptionKind.Flag => Name,
        OptionKind.Separate => $"{Name} {Value}",
        OptionKind.Joined => Name + Value,
        OptionKind.JoinedOrSeparate => Name.StartsWith("--", StringComparison.Ordinal) ? $"{Name}={Value}" : $"{Name} {Value}",
        OptionKind.JoinedAndSeparate => $"{Name}{Value} <argument>",
        OptionKind.CommaJoined => Name + Value,
        _ => Name
    };

    /// <summary>True when the option takes its value from the next argument.</summary>
    internal bool TakesNext => Kind is OptionKind.Separate or OptionKind.JoinedOrSeparate or OptionKind.JoinedAndSeparate;
}

/// <summary>One command-line argument after matching: an option with its value, or an input file (<see cref="Spec"/> null).</summary>
internal sealed record ParsedArgument(OptionSpec? Spec, string Text, string? Value)
{
    internal bool Is(string name) => Spec?.Name == name;
    /// <summary>The option and its value as given, for forwarding to the frontend.</summary>
    internal IEnumerable<string> AsGiven => Spec is { TakesNext: true } && Text == Spec.Name || Spec is { Kind: OptionKind.JoinedAndSeparate }
        ? [Text, Value ?? ""] : [Text];
}

internal static class DriverOptions
{
    // Groups in the order --help and the reference print them. The groups after Frontend hold the options llvmnet
    // accepts without effect and the ones it rejects; the reference shows the reason for each.
    internal const string Steps = "Steps and outputs";
    internal const string Inputs = "Inputs and libraries";
    internal const string Modes = "Runtime mode and ABI";
    internal const string Managed = "Managed and native libraries";
    internal const string Aot = "NativeAOT";
    internal const string Debugging = "Diagnostics and debugging";
    internal const string Information = "Information";
    internal const string Frontend = "Frontend options";
    internal const string NoEffect = "Accepted without effect";
    internal const string NotAccepted = "Not accepted";
    internal const string Linker = "-Wl, and -Xlinker options";
    internal const string Hidden = "hidden";
    internal static readonly string[] Groups = [Steps, Inputs, Modes, Managed, Aot, Debugging, Information, Frontend, NoEffect, NotAccepted, Linker];

    private const string PortableAbiReason = "it would change the versioned portable ABI (" + PortableAbi.Tag + "), whose target, data layout and headers are fixed; use --runtime=system for a native target";
    private const string NativeLinkerReason = "llvmnet links LLVM bitcode and emits CIL, so native linker options have no equivalent";
    private const string PositionReason = "position independence is a property of native code; CIL has none";
    private const string VectorNote = "Clang's default; llvmnet adds -fno-vectorize -fno-slp-vectorize unless -fvectorize or -fslp-vectorize is given";

    private static readonly Handling H = Handling.Handled, F = Handling.Forwarded, I = Handling.Ignored, R = Handling.Rejected;

    /// <summary>The option table. Order within a group is the order --help prints.</summary>
    internal static readonly IReadOnlyList<OptionSpec> Table =
    [
        // Steps and outputs
        new("-c", OptionKind.Flag, Steps, "Compile each input to an LLVM bitcode object (.o) and stop; .ll, .bc and .o inputs are tagged for the mode", H),
        new("-S", OptionKind.Flag, Steps, "With -emit-llvm, write textual LLVM IR (.ll) instead of an object", H),
        new("-E", OptionKind.Flag, Steps, "Preprocess only, with the mode's headers", H),
        new("-M", OptionKind.Flag, Steps, "Write a dependency file (also -MM, -MD, -MMD, -MF, -MT, -MQ, -MP, -MG, -MV, -MJ as in Clang)", H),
        new("-MM", OptionKind.Flag, Hidden, "", H),
        new("-fsyntax-only", OptionKind.Flag, Steps, "Parse and check the inputs without producing output", H),
        new("-emit-llvm", OptionKind.Flag, Steps, "Stop after linking and write the linked bitcode (default a.bc)", H),
        new("--emit-llvm", OptionKind.Flag, Hidden, "", H),
        new("-r", OptionKind.Flag, Steps, "Link to a relocatable bitcode object (default a.o) instead of an image", H),
        new("-shared", OptionKind.Flag, Steps, "Emit a CIL library without an entry point (default a.dll), not a native shared object", H),
        new("--emit-library", OptionKind.Flag, Hidden, "", H),
        new("-o", OptionKind.JoinedOrSeparate, Steps, "Output: a .dll is a CIL assembly with its runtimeconfig.json and LlvmNet.Runtime.dll; any other name is a Unix launcher plus a <name>.llvmnet/ payload, or a native executable with --nativeaot (default a.out)", H, value: "<file>"),
        new("-x", OptionKind.JoinedOrSeparate, Steps, "Language of the inputs that follow, as in Clang (c, c++, ir, f95, ...); .ll and .bc inputs are ir by extension", H, value: "<language>"),
        new("-###", OptionKind.Flag, Steps, "Print the frontend commands instead of running them, and stop before linking", H),
        // Inputs and libraries
        new("-L", OptionKind.JoinedOrSeparate, Inputs, "Directory searched for -l archives", H, value: "<dir>"),
        new("-l", OptionKind.JoinedOrSeparate, Inputs, "Link lib<name>.a, an archive of LLVM bitcode objects (-l:<file> names the file exactly), or else the native extension whose manifest lists the name (--extension); -lc, -lm, -lpthread, -ldl, -lrt and, in portable mode, -lc++ and the Fortran runtime names need no archive", H, value: "<name>"),
        new("-nostdlib", OptionKind.Flag, Inputs, "Portable: link neither the start object nor the sysroot libraries; system: no Fortran runtime", H),
        new("-nodefaultlibs", OptionKind.Flag, Inputs, "Portable: link no sysroot library (musl, libc++, flang-rt, OpenMP, builtins); system: no Fortran runtime", H),
        new("-nostartfiles", OptionKind.Flag, Inputs, "Portable: do not link musl's start object crt1.o", H),
        new("-nostdlib++", OptionKind.Flag, Inputs, "Portable: do not link libc++ and libc++abi", H),
        new("-Wl,", OptionKind.CommaJoined, Inputs, "Linker options; the accepted ones are listed under \"" + Linker + "\"", H, value: "<option>,..."),
        new("-Xlinker", OptionKind.Separate, Inputs, "One linker option, as -Wl,<option>", H, value: "<option>"),
        // Runtime mode and ABI
        new("--runtime", OptionKind.JoinedOrSeparate, Modes, "portable (the default; ABI " + PortableAbi.Tag + ", runs on every supported .NET host) or system (the native Linux x86-64 ABI, system-linux-x64-v1)", H, value: "<mode>"),
        new("--portable-sysroot", OptionKind.JoinedOrSeparate, Modes, "Use another portable sysroot instead of the toolchain's sysroot/" + PortableAbi.Tag, H, value: "<dir>"),
        new("--simd128", OptionKind.Flag, Modes, "Compile wasm_simd128.h intrinsics to Vector128 helper calls (docs/user/simd128.md)", H),
        // Managed and native libraries
        new("--cil-reference", OptionKind.JoinedOrSeparate, Managed, "Resolve calls to the public CExport methods of a managed library built with -shared, in the same mode", H, value: "<assembly>"),
        new("--system-library", OptionKind.JoinedOrSeparate, Managed, "System mode: a native shared library whose functions the program calls through generated bridges", H, value: "<library>"),
        new("--extension", OptionKind.JoinedOrSeparate, Managed, "Link a native extension installed in the sysroot's ext/ directory: in portable mode its assembly, registered for dlopen; in system mode its Linux libraries, as --system-library (also -l<name> for a name its manifest lists)", H, value: "<name>"),
        new("--native-import", OptionKind.JoinedOrSeparate, NotAccepted, "", R, "it was removed (ADR-04, SA-01); --runtime=system resolves every native function through generated bridges, and --system-library <shared-library> adds a library", "<spec>"),
        // NativeAOT
        new("--nativeaot", OptionKind.Flag, Aot, "Publish a native executable with NativeAOT (needs the .NET 10 SDK and the target's native toolchain)", H),
        new("--runtime-id", OptionKind.JoinedOrSeparate, Aot, "NativeAOT target runtime identifier (default: this host; linux-arm64 cross-publishes from Linux x64; system mode needs linux-x64)", H, value: "<rid>"),
        new("--aot-project", OptionKind.JoinedOrSeparate, Aot, "Write the NativeAOT project into <dir> for publishing on another host (dotnet publish -r <rid>) instead of publishing here", H, value: "<dir>"),
        new("--aot-optimize", OptionKind.JoinedOrSeparate, Aot, "Balanced (default), Speed or Size", H, value: "<preference>"),
        new("--aot-instruction-set", OptionKind.JoinedOrSeparate, Aot, "baseline (default: SSE2 on x64), x86-64-v3 (AVX2, BMI2, FMA: x64 CPUs since about 2015) or native (this host's CPU; the executable may not run elsewhere)", H, value: "<set>"),
        new("--aot-debug", OptionKind.Flag, Aot, "Keep native debug symbols and the map file", H),
        // Diagnostics and debugging
        new("--keep-ir", OptionKind.JoinedOrSeparate, Debugging, "Copy the linked bitcode, before lowering, to <file> (kept as <output>.failed.bc on a compile error)", H, value: "<file>"),
        new("--trap-missing-arguments", OptionKind.Flag, Debugging, "Compile a void call with fewer arguments than its definition to a trap instead of failing", H),
        new("--diagnose-limits", OptionKind.Flag, Debugging, "Report each function's IL size and locals, and the ones the JIT compiles without optimization", H),
        new("--check-indirect-calls", OptionKind.Flag, Debugging, "Check every indirect call's CIL signature against its target at run time", H),
        new("-v", OptionKind.Flag, Debugging, "Print the commands run and the notes for options without effect; alone, the frontend's version", H),
        // Information
        new("--help", OptionKind.Flag, Information, "This list; docs/user/driver.md is the full reference", H),
        new("--version", OptionKind.Flag, Information, "llvmnet's version, the LLVM version and the ABI tags, then the frontend's own version", H),
        new("--list-extensions", OptionKind.Flag, Information, "List the native extensions installed in the sysroot's ext/ directory, with their -l names, host libraries and headers", H),
        new("--driver-mode", OptionKind.JoinedOrSeparate, Information, "Frontend to use: gcc or clang (C), g++ or clang++ (C++), flang (Fortran), as the llvmnet-clang, llvmnet-clang++ and llvmnet-flang names do", H, value: "<mode>"),
        new("-dumpmachine", OptionKind.Flag, Information, "Frontend queries, answered by the selected frontend for the mode's target (x86_64-unknown-none in portable mode; also -dumpversion, -print-search-dirs, -print-resource-dir, -print-file-name=, -print-prog-name=, -print-target-triple, -print-libgcc-file-name)", H),
        new("-dumpversion", OptionKind.Flag, Hidden, "", H),
        new("-print-search-dirs", OptionKind.Flag, Hidden, "", H),
        new("-print-resource-dir", OptionKind.Flag, Hidden, "", H),
        new("-print-target-triple", OptionKind.Flag, Hidden, "", H),
        new("-print-libgcc-file-name", OptionKind.Flag, Hidden, "", H),
        new("-print-file-name=", OptionKind.Joined, Hidden, "", H, value: "<file>"),
        new("-print-prog-name=", OptionKind.Joined, Hidden, "", H, value: "<name>"),
        new("--print-driver-reference", OptionKind.Flag, Hidden, "", H),
        new("--print-option-table", OptionKind.Flag, Hidden, "", H),
        new("--print-host-limitations", OptionKind.Flag, Hidden, "", H),
        // Frontend options: the documented examples; every other option that starts with - is forwarded as well.
        new("-O", OptionKind.Joined, Frontend, "Optimization level. The default is -O1 without automatic vectorization (ADR-11); -O0 is accepted and -O3 fully tested", F, value: "<level>"),
        new("-fvectorize", OptionKind.Flag, Frontend, "Enable loop vectorization (also -fslp-vectorize); the vector IR is scalarized to CIL", F),
        new("-fslp-vectorize", OptionKind.Flag, Hidden, "", F),
        new("-std=", OptionKind.Joined, Frontend, "Language standard; the defaults are the frontends' own (gnu17 for C, gnu++17 for C++, Fortran 2018)", F, value: "<standard>"),
        new("-g", OptionKind.Flag, Frontend, "Debug information: compiler diagnostics get file:line:col (also -gline-tables-only); emitted assemblies carry none yet (BE-28)", F),
        new("-D", OptionKind.JoinedOrSeparate, Frontend, "Preprocessor definitions, include paths and the other Clang and Flang options (-U, -I, -isystem, -include, -W..., -f..., -fopenmp, -J, -module-dir, ...) are passed through in order", F, value: "<macro>"),
        new("-pthread", OptionKind.Flag, Frontend, "System mode: as in Clang. Portable mode: no effect, musl's pthreads are part of every program", F, I, "musl's pthreads are compiled into every portable program"),
        new("-stdlib=", OptionKind.Joined, Frontend, "System mode: as in Clang. Portable mode: no effect, the sysroot's libc++ is the C++ library", F, I, "the portable sysroot provides libc++ and nothing else", "<library>"),
        new("-mtune=", OptionKind.Joined, Frontend, "System mode: as in Clang. Portable mode: no effect, portable code is scalar CIL", F, I, "portable code is scalar CIL for one fixed data layout; the .NET JIT or NativeAOT tunes for the host", "<cpu>"),
        new("-mllvm", OptionKind.Separate, Frontend, "LLVM options and Flang's MLIR options (-mmlir) are passed through", F, value: "<option>"),
        new("-mllvm=", OptionKind.Joined, Hidden, "", F, value: "<option>"),
        new("-mmlir", OptionKind.Separate, Hidden, "", F, value: "<option>"),
        new("-module-dir", OptionKind.Separate, Hidden, "", F, value: "<dir>"),
        new("-module-dependency-dir", OptionKind.Separate, Hidden, "", F, value: "<dir>"),
        new("-module-file-info", OptionKind.Flag, Hidden, "", F),
        new("-multi-lib-config=", OptionKind.Joined, Hidden, "", F, value: "<file>"),
        new("-J", OptionKind.JoinedOrSeparate, Hidden, "", F, value: "<dir>"),
        // Options with no effect
        new("-fPIC", OptionKind.Flag, NoEffect, "", I, PositionReason + " (also -fpic, -fPIE, -fpie, -fno-pic, -fno-PIC, -fno-pie, -fno-PIE, -pie, -no-pie, -nopie, -static-pie)"),
        new("-fpic", OptionKind.Flag, Hidden, "", I, PositionReason),
        new("-fPIE", OptionKind.Flag, Hidden, "", I, PositionReason),
        new("-fpie", OptionKind.Flag, Hidden, "", I, PositionReason),
        new("-fno-pic", OptionKind.Flag, Hidden, "", I, PositionReason),
        new("-fno-PIC", OptionKind.Flag, Hidden, "", I, PositionReason),
        new("-fno-pie", OptionKind.Flag, Hidden, "", I, PositionReason),
        new("-fno-PIE", OptionKind.Flag, Hidden, "", I, PositionReason),
        new("-pie", OptionKind.Flag, Hidden, "", I, PositionReason),
        new("-no-pie", OptionKind.Flag, Hidden, "", I, PositionReason),
        new("-nopie", OptionKind.Flag, Hidden, "", I, PositionReason),
        new("-static-pie", OptionKind.Flag, Hidden, "", I, PositionReason),
        new("-static", OptionKind.Flag, NoEffect, "", I, R, "portable: a portable program is always one statically linked image; system: a system program uses the host's shared C library and native libraries through generated bridges, which cannot be linked statically"),
        new("-s", OptionKind.Flag, NoEffect, "", I, "there is no native symbol table to strip; --aot-debug controls NativeAOT symbols"),
        new("-pipe", OptionKind.Flag, NoEffect, "", I, "the frontend writes bitcode files; there is no assembler to pipe to"),
        new("-fuse-ld=", OptionKind.Joined, NoEffect, "", I, "linking always works on bitcode, with LLVM's linker inside llvmnet", "<linker>"),
        new("-flto", OptionKind.Flag, NoEffect, "", I, "every link is a whole-program bitcode link already (also -flto=<mode>, -fno-lto)"),
        new("-flto=", OptionKind.Joined, Hidden, "", I, "every link is a whole-program bitcode link already", "<mode>"),
        new("-fno-lto", OptionKind.Flag, Hidden, "", I, "every link is a whole-program bitcode link already"),
        new("-static-libgcc", OptionKind.Flag, NoEffect, "", I, "compiler-rt's builtins come from the sysroot in portable mode and libgcc_s from the host in system mode (also -shared-libgcc, -static-libstdc++, -rtlib=, -unwindlib=)"),
        new("-shared-libgcc", OptionKind.Flag, Hidden, "", I, "compiler-rt's builtins come from the sysroot in portable mode and libgcc_s from the host in system mode"),
        new("-static-libstdc++", OptionKind.Flag, Hidden, "", I, "C++ library code is always linked as bitcode"),
        new("-rtlib=", OptionKind.Joined, Hidden, "", I, "compiler-rt's builtins come from the sysroot in portable mode and libgcc_s from the host in system mode", "<library>"),
        new("-unwindlib=", OptionKind.Joined, Hidden, "", I, "the CLR unwinds the stack; there is no unwind library", "<library>"),
        new("-z", OptionKind.Separate, NoEffect, "", I, "ELF dynamic-section keywords (relro, now, noexecstack, ...) have no CIL equivalent", "<keyword>"),
        // Not accepted
        new("-rdynamic", OptionKind.Flag, NotAccepted, "", R, "a program's own symbols cannot be looked up with dlopen and dlsym; export them from a library built with -shared instead"),
        new("-e", OptionKind.JoinedOrSeparate, NotAccepted, "", R, "the entry point is always main (or the Fortran main program); " + NativeLinkerReason, "<symbol>"),
        new("-T", OptionKind.JoinedOrSeparate, NotAccepted, "", R, "linker scripts do not apply: " + NativeLinkerReason, "<script>"),
        new("-u", OptionKind.JoinedOrSeparate, NotAccepted, "", R, "forcing an archive member is not implemented; name the object on the command line or use -Wl,--whole-archive", "<symbol>"),
        new("-Wa,", OptionKind.CommaJoined, NotAccepted, "", R, "there is no assembler step (also -Xassembler)", "<option>"),
        new("-Xassembler", OptionKind.Separate, Hidden, "", R, "there is no assembler step", "<option>"),
        new("-fsanitize=", OptionKind.Joined, NotAccepted, "", R, "the sanitizer runtimes are not part of the sysroot or the system-mode link", "<check>"),
        new("-fprofile-instr-generate", OptionKind.Flag, NotAccepted, "", R, "the profiling runtime is not part of the sysroot or the system-mode link (also -fprofile-instr-generate=, -fprofile-generate, -fprofile-arcs, -ftest-coverage, --coverage)"),
        new("-fprofile-instr-generate=", OptionKind.Joined, Hidden, "", R, "the profiling runtime is not part of the sysroot or the system-mode link", "<file>"),
        new("-fprofile-generate", OptionKind.Flag, Hidden, "", R, "the profiling runtime is not part of the sysroot or the system-mode link"),
        new("-fprofile-generate=", OptionKind.Joined, Hidden, "", R, "the profiling runtime is not part of the sysroot or the system-mode link", "<dir>"),
        new("-fprofile-arcs", OptionKind.Flag, Hidden, "", R, "the profiling runtime is not part of the sysroot or the system-mode link"),
        new("-ftest-coverage", OptionKind.Flag, Hidden, "", R, "the profiling runtime is not part of the sysroot or the system-mode link"),
        new("-coverage", OptionKind.Flag, Hidden, "", R, "the profiling runtime is not part of the sysroot or the system-mode link"),
        new("--coverage", OptionKind.Flag, Hidden, "", R, "the profiling runtime is not part of the sysroot or the system-mode link"),
        new("-nolibc", OptionKind.Flag, NotAccepted, "", R, F, "portable: the C library cannot be left out on its own (crt1.o needs it); -nodefaultlibs leaves out every sysroot library"),
        new("-m", OptionKind.Joined, NotAccepted, "", R, F, "portable: -m<option> selects a CPU, instruction set or native ABI detail, and " + PortableAbiReason + "; -mllvm, -mmlir, -mtune= and the -module... options are the exceptions", "<option>"),
        new("-arch", OptionKind.Separate, Hidden, "", R, F, "portable: " + PortableAbiReason, "<arch>"),
        new("--target=", OptionKind.Joined, NotAccepted, "", R, F, "portable: " + PortableAbiReason + " (also -target <triple>, --sysroot, -isysroot, -fshort-wchar, -fshort-enums, -fpack-struct)", "<triple>"),
        new("-target", OptionKind.Separate, Hidden, "", R, F, "portable: " + PortableAbiReason, "<triple>"),
        new("--sysroot=", OptionKind.Joined, Hidden, "", R, F, "portable: the sysroot is fixed to the portable one; --portable-sysroot <dir> selects another build of it", "<dir>"),
        new("--sysroot", OptionKind.Separate, Hidden, "", R, F, "portable: the sysroot is fixed to the portable one; --portable-sysroot <dir> selects another build of it", "<dir>"),
        new("-isysroot", OptionKind.Separate, Hidden, "", R, F, "portable: the sysroot is fixed to the portable one; --portable-sysroot <dir> selects another build of it", "<dir>"),
        new("-fshort-wchar", OptionKind.Flag, Hidden, "", R, F, "portable: " + PortableAbiReason),
        new("-fshort-enums", OptionKind.Flag, Hidden, "", R, F, "portable: " + PortableAbiReason),
        new("-fpack-struct", OptionKind.Flag, Hidden, "", R, F, "portable: " + PortableAbiReason),
        new("-fpack-struct=", OptionKind.Joined, Hidden, "", R, F, "portable: " + PortableAbiReason, "<n>"),
    ];

    /// <summary>The -Wl, and -Xlinker options llvmnet accepts; everything else after -Wl, is rejected.</summary>
    internal static readonly IReadOnlyList<OptionSpec> LinkerTable =
    [
        new("--start-group", OptionKind.Flag, Linker, "Accepted; archives are rescanned until no member resolves a symbol, as if every archive were in one group (also --end-group, -( and -))", H),
        new("--end-group", OptionKind.Flag, Hidden, "", H),
        new("-(", OptionKind.Flag, Hidden, "", H),
        new("-)", OptionKind.Flag, Hidden, "", H),
        new("--whole-archive", OptionKind.Flag, Linker, "Link every member of the archives that follow, until --no-whole-archive", H),
        new("--no-whole-archive", OptionKind.Flag, Hidden, "", H),
        new("--version", OptionKind.Flag, Linker, "Print the linker interface version and exit", H),
        new("-rpath", OptionKind.Separate, Linker, "", I, "there is no native loader path to record (also -rpath=, -rpath-link, --enable-new-dtags, --disable-new-dtags)", "<dir>"),
        new("-rpath=", OptionKind.Joined, Hidden, "", I, "there is no native loader path to record", "<dir>"),
        new("-rpath-link", OptionKind.Separate, Hidden, "", I, "there is no native loader path to record", "<dir>"),
        new("-rpath-link=", OptionKind.Joined, Hidden, "", I, "there is no native loader path to record", "<dir>"),
        new("--enable-new-dtags", OptionKind.Flag, Hidden, "", I, "there is no native loader path to record"),
        new("--disable-new-dtags", OptionKind.Flag, Hidden, "", I, "there is no native loader path to record"),
        new("--as-needed", OptionKind.Flag, Linker, "", I, "archive members are linked only when they resolve a symbol, and there are no shared-object dependencies (also --no-as-needed, -Bstatic, -Bdynamic, -Bsymbolic, -Bsymbolic-functions)"),
        new("--no-as-needed", OptionKind.Flag, Hidden, "", I, "there are no shared-object dependencies"),
        new("-Bstatic", OptionKind.Flag, Hidden, "", I, "every link is static"),
        new("-Bdynamic", OptionKind.Flag, Hidden, "", I, "every link is static"),
        new("-Bsymbolic", OptionKind.Flag, Hidden, "", I, "every link is static"),
        new("-Bsymbolic-functions", OptionKind.Flag, Hidden, "", I, "every link is static"),
        new("--gc-sections", OptionKind.Flag, Linker, "", I, "unreferenced definitions are removed from every executable anyway (also --no-gc-sections, --icf=, -O<n>, --sort-common)"),
        new("--no-gc-sections", OptionKind.Flag, Hidden, "", I, "unreferenced definitions are removed from every executable anyway"),
        new("--icf=", OptionKind.Joined, Hidden, "", I, "there are no sections to fold", "<mode>"),
        new("-O", OptionKind.Joined, Hidden, "", I, "there are no native sections to optimize", "<n>"),
        new("--sort-common", OptionKind.Flag, Hidden, "", I, "there are no native sections"),
        new("-z", OptionKind.Separate, Linker, "", I, "ELF dynamic-section keywords have no CIL equivalent", "<keyword>"),
        new("--build-id", OptionKind.Flag, Linker, "", I, "there is no ELF note section; the assembly's MVID is derived from its contents (also --build-id=, --hash-style=)"),
        new("--build-id=", OptionKind.Joined, Hidden, "", I, "there is no ELF note section", "<style>"),
        new("--hash-style=", OptionKind.Joined, Hidden, "", I, "there is no ELF symbol hash table", "<style>"),
        new("-soname", OptionKind.Separate, Linker, "", I, "a CIL library is named by its assembly name (also -soname=, -h)", "<name>"),
        new("-soname=", OptionKind.Joined, Hidden, "", I, "a CIL library is named by its assembly name", "<name>"),
        new("-h", OptionKind.Separate, Hidden, "", I, "a CIL library is named by its assembly name", "<name>"),
        new("-s", OptionKind.Flag, Linker, "", I, "there is no native symbol table to strip (also --strip-all, -S, --strip-debug)"),
        new("--strip-all", OptionKind.Flag, Hidden, "", I, "there is no native symbol table to strip"),
        new("-S", OptionKind.Flag, Hidden, "", I, "there is no native symbol table to strip"),
        new("--strip-debug", OptionKind.Flag, Hidden, "", I, "there is no native symbol table to strip"),
        new("--no-undefined", OptionKind.Flag, Linker, "", I, "an unresolved symbol is always a link error (also --allow-shlib-undefined, --no-allow-shlib-undefined)"),
        new("--allow-shlib-undefined", OptionKind.Flag, Hidden, "", I, "an unresolved symbol is always a link error"),
        new("--no-allow-shlib-undefined", OptionKind.Flag, Hidden, "", I, "an unresolved symbol is always a link error"),
        new("-Map", OptionKind.Separate, Linker, "", I, "no link map is written; --diagnose-limits reports the emitted functions (also -Map=)", "<file>"),
        new("-Map=", OptionKind.Joined, Hidden, "", I, "no link map is written", "<file>"),
        new("--version-script", OptionKind.Separate, Linker, "", I, "a library exports its default-visibility external definitions; use -fvisibility and visibility attributes (also --version-script=, --exclude-libs)", "<file>"),
        new("--version-script=", OptionKind.Joined, Hidden, "", I, "a library exports its default-visibility external definitions", "<file>"),
        new("--exclude-libs", OptionKind.Separate, Hidden, "", I, "a library exports its default-visibility external definitions", "<list>"),
        new("--exclude-libs=", OptionKind.Joined, Hidden, "", I, "a library exports its default-visibility external definitions", "<list>"),
        new("--export-dynamic", OptionKind.Flag, Linker, "", R, "as -rdynamic: a program's own symbols cannot be looked up with dlopen and dlsym (also -E)"),
        new("-E", OptionKind.Flag, Hidden, "", R, "as -rdynamic: a program's own symbols cannot be looked up with dlopen and dlsym"),
        new("--allow-multiple-definition", OptionKind.Flag, Linker, "", R, "a duplicate definition is always an error, which names both files"),
    ];

    private static readonly Dictionary<string, OptionSpec> exact = new(StringComparer.Ordinal);
    private static readonly OptionSpec[] prefixes;
    private static readonly Dictionary<string, OptionSpec> linkerExact = new(StringComparer.Ordinal);
    private static readonly OptionSpec[] linkerPrefixes;

    static DriverOptions()
    {
        List<OptionSpec> all = [.. Table];
        // The frontends' own options, from their --help: known so that a value is never taken for an input and a
        // joined spelling never for one of llvmnet's shorter options. The hand-written rows above take precedence,
        // and a rule written for a prefix (-m<option> in portable mode) covers the frontend spellings under it.
        var known = Table.Select(spec => spec.Name).ToHashSet(StringComparer.Ordinal);
        OptionSpec[] rules = Table.Where(spec => spec.Kind is OptionKind.Joined or OptionKind.CommaJoined && (spec.Portable is Handling.Rejected or Handling.Ignored || spec.System is Handling.Rejected or Handling.Ignored))
            .OrderByDescending(spec => spec.Name.Length).ToArray();
        OptionSpec Generated(string name, OptionKind kind)
        {
            OptionSpec? rule = rules.FirstOrDefault(rule => name.StartsWith(rule.Name, StringComparison.Ordinal));
            return rule is null ? new OptionSpec(name, kind, Frontend, "", F) : new OptionSpec(name, kind, Hidden, "", rule.Portable, rule.System, rule.Note);
        }
        foreach (string name in FrontendOptions.Separate) if (known.Add(name)) all.Add(Generated(name, OptionKind.JoinedOrSeparate));
        foreach (string name in FrontendOptions.Joined) if (known.Add(name)) all.Add(Generated(name, OptionKind.Joined));
        foreach (string name in FrontendOptions.JoinedAndSeparate) if (known.Add(name)) all.Add(Generated(name, OptionKind.JoinedAndSeparate));
        foreach (string name in FrontendOptions.Flags) if (known.Add(name)) all.Add(Generated(name, OptionKind.Flag));
        (exact, prefixes) = Index(all);
        (linkerExact, linkerPrefixes) = Index(LinkerTable);
    }

    private static (Dictionary<string, OptionSpec>, OptionSpec[]) Index(IEnumerable<OptionSpec> specs)
    {
        Dictionary<string, OptionSpec> byName = new(StringComparer.Ordinal);
        List<OptionSpec> joined = [];
        foreach (OptionSpec spec in specs)
        {
            if (spec.Kind is OptionKind.Flag or OptionKind.Separate or OptionKind.JoinedOrSeparate) byName[spec.Name] = spec;
            if (spec.Kind is not (OptionKind.Flag or OptionKind.Separate)) joined.Add(spec);
        }
        return (byName, joined.OrderByDescending(spec => spec.Name.Length).ToArray());
    }

    /// <summary>Matches every argument against the table. Values are consumed here; nothing is interpreted yet.</summary>
    internal static List<ParsedArgument> Parse(IReadOnlyList<string> arguments)
    {
        List<ParsedArgument> result = [];
        for (int index = 0; index < arguments.Count; index++)
        {
            string argument = arguments[index];
            if (!argument.StartsWith('-') || argument == "-") { result.Add(new ParsedArgument(null, argument, null)); continue; }
            (OptionSpec spec, string? value, bool takesNext) = Match(argument, exact, prefixes);
            if (takesNext)
            {
                if (++index >= arguments.Count) throw new ArgumentException($"option '{argument}' requires a value ({spec.Value}).");
                value = spec.Kind == OptionKind.JoinedAndSeparate ? value + "\0" + arguments[index] : arguments[index];
            }
            result.Add(new ParsedArgument(spec, argument, value));
        }
        return result;
    }

    private static (OptionSpec Spec, string? Value, bool TakesNext) Match(string argument, Dictionary<string, OptionSpec> byName, OptionSpec[] joined)
    {
        if (byName.TryGetValue(argument, out OptionSpec? found))
            return (found, null, found.TakesNext);
        foreach (OptionSpec spec in joined)
        {
            if (argument.Length <= spec.Name.Length || !argument.StartsWith(spec.Name, StringComparison.Ordinal)) continue;
            string rest = argument[spec.Name.Length..];
            if (spec.Kind == OptionKind.JoinedOrSeparate && spec.Name.StartsWith("--", StringComparison.Ordinal))
            {
                if (rest[0] != '=') continue;
                return (spec, rest[1..], false);
            }
            return (spec, rest, spec.Kind == OptionKind.JoinedAndSeparate);
        }
        // Unknown: the frontend decides. Clang and Flang report an unknown argument themselves.
        return (new OptionSpec(argument, OptionKind.Flag, Frontend, "", F), null, false);
    }

    /// <summary>Classifies linker arguments (the comma-separated parts of every -Wl, and each -Xlinker value) in order.</summary>
    internal static List<ParsedArgument> ParseLinker(IReadOnlyList<string> arguments)
    {
        List<ParsedArgument> result = [];
        for (int index = 0; index < arguments.Count; index++)
        {
            string argument = arguments[index];
            OptionSpec spec;
            string? value;
            bool takesNext;
            if (linkerExact.TryGetValue(argument, out OptionSpec? found)) (spec, value, takesNext) = (found, null, found.TakesNext);
            else
            {
                OptionSpec? prefix = linkerPrefixes.FirstOrDefault(candidate => argument.Length > candidate.Name.Length && argument.StartsWith(candidate.Name, StringComparison.Ordinal));
                (spec, value, takesNext) = prefix is null
                    ? (new OptionSpec(argument, OptionKind.Flag, Linker, "", R, NativeLinkerReason), null, false)
                    : (prefix, argument[prefix.Name.Length..], false);
            }
            if (takesNext)
            {
                if (++index >= arguments.Count) throw new ArgumentException($"linker option '-Wl,{argument}' requires a value ({spec.Value}).");
                value = arguments[index];
            }
            result.Add(new ParsedArgument(spec, argument, value));
        }
        return result;
    }

    /// <summary>The message for an option the table rejects in the given mode.</summary>
    internal static string RejectionMessage(OptionSpec spec, string argument, string runtime)
    {
        string note = spec.Note ?? "it is not supported";
        bool modeSpecific = spec.Portable != spec.System;
        if (modeSpecific && note.StartsWith(runtime + ": ", StringComparison.Ordinal)) note = note[(runtime.Length + 2)..];
        else if (modeSpecific) note = StripModePrefix(note, runtime);
        return $"option '{argument}' is not accepted{(modeSpecific ? $" in {runtime} mode" : "")}: {note}.";
    }

    // "portable: ...; system: ..." notes describe both modes; the message for one mode shows its own part.
    private static string StripModePrefix(string note, string runtime)
    {
        foreach (string part in note.Split("; "))
            if (part.StartsWith(runtime + ": ", StringComparison.Ordinal)) return part[(runtime.Length + 2)..];
        return note;
    }

    /// <summary>The note printed with -v for an option the table ignores in the given mode.</summary>
    internal static string IgnoredNote(OptionSpec spec, string argument, string runtime)
    {
        string note = spec.Note ?? "";
        if (spec.Portable != spec.System) note = StripModePrefix(note, runtime);
        int parenthesis = note.IndexOf(" (also", StringComparison.Ordinal);
        if (parenthesis > 0) note = note[..parenthesis];
        return $"option '{argument}' has no effect: {note}.";
    }

    /// <summary>The --help text, generated from the table.</summary>
    internal static string Help(string driver)
    {
        var text = new StringBuilder();
        text.AppendLine($"llvmnet {BuildInfo.Version}: C, C++ and Fortran through Clang/Flang {Llvm.Version} to .NET 10 CIL (frontend: {driver})");
        text.AppendLine("Usage: llvmnet [options] <inputs>... [-o <output>]");
        text.AppendLine("       llvmnet-clang, llvmnet-clang++ and llvmnet-flang select the frontend; llvmnet chooses it from each input's extension.");
        foreach (string group in Groups)
        {
            IEnumerable<OptionSpec> rows = (group == Linker ? LinkerTable : Table).Where(spec => spec.Group == group);
            if (!rows.Any()) continue;
            text.AppendLine();
            text.AppendLine(group + ":");
            if (group == Frontend)
                text.AppendLine("  Every option not listed on this page is passed to Clang or Flang in order; those below are the ones worth knowing.");
            if (group == NoEffect)
                text.AppendLine("  Accepted for build systems that pass them; -v prints the reason each one has no effect.");
            if (group == NotAccepted)
                text.AppendLine("  Each fails with its reason; most are native linker or native ABI options.");
            if (group == Linker)
                text.AppendLine("  Other -Wl, options fail: llvmnet links bitcode and emits CIL.");
            foreach (OptionSpec spec in rows)
            {
                string spelling = group == Linker ? "-Wl," + spec.Spelling : spec.Spelling;
                string description = spec.Help.Length > 0 ? spec.Help : Describe(spec);
                text.AppendLine(spelling.Length > 26 ? $"  {spelling}\n{"",29}{description}" : $"  {spelling,-27}{description}");
            }
        }
        text.AppendLine();
        text.AppendLine("Environment: LLVMNET_CLANG, LLVMNET_FLANG and LLVMNET_OPT select the frontend executables; LLVMNET_DOTNET the dotnet command;");
        text.AppendLine("  LLVMNET_ROOT the toolchain root; LLVMNET_TRACE=1 prints a stack trace with every compiler error.");
        text.AppendLine("Exit status: 0 success; 1 compilation error, or the frontend's or NativeAOT's own failure; 2 usage error; 3 internal compiler error.");
        text.AppendLine("Reference: docs/user/driver.md in the toolchain's share/llvmnet directory.");
        return text.ToString();
    }

    private static string Describe(OptionSpec spec)
    {
        string note = spec.Note ?? "";
        return spec switch
        {
            { Portable: Handling.Ignored, System: Handling.Ignored } => "No effect: " + note,
            { Portable: Handling.Rejected, System: Handling.Rejected } => "Not accepted: " + note,
            { Portable: Handling.Ignored, System: Handling.Rejected } => "No effect in portable mode, not accepted in system mode: " + note,
            { Portable: Handling.Rejected, System: Handling.Forwarded } => "Not accepted in portable mode, passed to the frontend in system mode: " + note,
            { Portable: Handling.Ignored, System: Handling.Forwarded } => "No effect in portable mode, passed to the frontend in system mode: " + note,
            _ => note
        };
    }

    /// <summary>The option tables of docs/user/driver.md, generated from the table (tests/driver-reference.sh keeps the page current).</summary>
    internal static string Reference()
    {
        var text = new StringBuilder();
        foreach (string group in Groups)
        {
            IEnumerable<OptionSpec> rows = (group == Linker ? LinkerTable : Table).Where(spec => spec.Group == group);
            if (!rows.Any()) continue;
            text.AppendLine($"### {group}");
            text.AppendLine();
            switch (group)
            {
                case Frontend:
                    text.AppendLine("Every option that is not in one of these tables is passed to Clang or Flang in order, including its value when it takes one as the next argument (the driver knows the frontends' option spellings from their own `--help`). The frontend reports unknown options and invalid values itself. Options that depend on the mode say so.");
                    text.AppendLine();
                    break;
                case NoEffect:
                    text.AppendLine("These are accepted so that build systems can pass them, and dropped. `-v` prints the reason for each one as a note.");
                    text.AppendLine();
                    break;
                case NotAccepted:
                    text.AppendLine("Each of these fails with the reason in the table, as `llvmnet: error: option '<option>' is not accepted: <reason>` (exit status 2).");
                    text.AppendLine();
                    break;
                case Linker:
                    text.AppendLine("The comma-separated parts of every `-Wl,` option and each `-Xlinker` value are classified one by one. Any other linker option fails: llvmnet links LLVM bitcode and emits CIL, so native linker options have no equivalent.");
                    text.AppendLine();
                    break;
            }
            bool reasons = group is NoEffect or NotAccepted or Linker;
            text.AppendLine(reasons ? "| Option | Treatment | Reason |" : "| Option | Effect |");
            text.AppendLine(reasons ? "| --- | --- | --- |" : "| --- | --- |");
            foreach (OptionSpec spec in rows)
            {
                string spelling = (group == Linker ? "-Wl," : "") + spec.Spelling;
                string cell = Escape(spelling);
                if (reasons)
                    text.AppendLine($"| `{cell}` | {Treatment(spec)} | {Escape(spec.Help.Length > 0 ? spec.Help : spec.Note ?? "")} |");
                else
                    text.AppendLine($"| `{cell}` | {Escape(spec.Help)} |");
            }
            text.AppendLine();
        }
        return text.ToString().TrimEnd() + "\n";
    }

    private static string Treatment(OptionSpec spec) => (spec.Portable, spec.System) switch
    {
        (Handling.Handled, Handling.Handled) => "handled",
        (Handling.Ignored, Handling.Ignored) => "no effect",
        (Handling.Rejected, Handling.Rejected) => "not accepted",
        (Handling.Ignored, Handling.Rejected) => "portable: no effect; system: not accepted",
        (Handling.Rejected, Handling.Forwarded) => "portable: not accepted; system: passed to the frontend",
        (Handling.Ignored, Handling.Forwarded) => "portable: no effect; system: passed to the frontend",
        (Handling.Rejected, Handling.Ignored) => "portable: not accepted; system: no effect",
        _ => $"portable: {spec.Portable}; system: {spec.System}".ToLowerInvariant()
    };

    private static string Escape(string text) => text.Replace("|", "\\|", StringComparison.Ordinal);

    /// <summary>Every row as tab-separated text (name, kind, portable, system, group), for the option tests.</summary>
    internal static string OptionTable()
    {
        var text = new StringBuilder();
        foreach (OptionSpec spec in exact.Values.Concat(prefixes).Distinct().OrderBy(spec => spec.Name, StringComparer.Ordinal))
            text.AppendLine($"{spec.Name}\t{spec.Kind}\t{spec.Portable}\t{spec.System}\t{spec.Group}");
        foreach (OptionSpec spec in LinkerTable)
            text.AppendLine($"-Wl,{spec.Name}\t{spec.Kind}\t{spec.Portable}\t{spec.System}\t{spec.Group}");
        return text.ToString();
    }
}
