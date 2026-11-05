using LlvmNet.Runtime;

namespace LlvmNet;

internal sealed class CompilerOptions
{
    internal bool Library { get; set; }
    internal string Runtime { get; set; } = "portable";
    internal bool Verbose { get; set; }
    internal bool NativeAot { get; set; }
    /// <summary>--readytorun: the runtime identifier the JIT image is precompiled for (JP-06), or null for an IL-only image.</summary>
    internal string? ReadyToRun { get; set; }
    /// <summary>The instruction set the frontend may assume (--isa, ADR-16): baseline, x86-64-v2, x86-64-v3 or wasm-simd128.</summary>
    internal string Isa { get; set; } = "baseline";
    internal bool Simd128 => Isa == "wasm-simd128";
    /// <summary>
    /// The hidden --vector-lowering choice, for comparisons and tests: auto (the default) lowers vector code to Vector128
    /// except in the functions VectorEmitter.Legalize scalarizes, typed scalarizes only what the lowering cannot
    /// translate, and scalar scalarizes every vector, as llvmnet did before the typed lowering.
    /// </summary>
    internal string VectorLowering { get; set; } = "auto";
    /// <summary>Whether vector IR is lowered to Vector128 (ADR-16 Level 1).</summary>
    internal bool TypedVectors => VectorLowering != "scalar";
    /// <summary>The hidden --vector-idioms choice: on (the default) runs the sums of absolute differences and of 16-bit
    /// products as psadbw and pmaddwd (VectorEmitter.Idioms), off as their literal dataflow, for comparisons.</summary>
    internal bool VectorIdioms { get; set; } = true;
    /// <summary>The bits of a typed vector fragment, 128 or 256 (--vector-width, SV-06).</summary>
    internal int VectorWidth { get; set; } = 128;
    internal bool TrapMissingArguments { get; set; }
    internal bool DiagnoseLimits { get; set; }
    /// <summary>--report-simd: the vector code's functions, recognized reductions and SIMD helpers by class (SimdReport).</summary>
    internal bool ReportSimd { get; set; }
    internal bool CheckIndirectCalls { get; set; }
    /// <summary>Prints llvmnet's warnings while translating; -w, as for the frontend, silences them.</summary>
    internal Diagnostics? Warnings { get; set; }
    internal string AbiTag => Runtime == "system" ? "system-linux-x64-v1" : PortableAbi.Tag;
    internal List<string> SystemLibraries { get; } = [];
    internal List<string> CilReferences { get; } = [];
    /// <summary>Native extensions an image links (portable mode): referenced, registered at start and listed in its metadata.</summary>
    internal List<ExtensionPackage> Extensions { get; } = [];
    /// <summary>Native extensions a system-mode image links: their Linux libraries, registered by name and listed in its metadata.</summary>
    internal List<ExtensionPackage> SystemExtensions { get; } = [];
    /// <summary>The native kernel packages the image calls (--native-kernels, SV-13).</summary>
    internal List<KernelPackage> KernelPackages { get; } = [];
    /// <summary>System mode: native object files (assembled kernels) linked into the image's native library (SV-15).</summary>
    internal List<NativeObject> NativeObjects { get; } = [];
}

/// <summary>A native object file and its global symbols: the functions and data it defines and the symbols it references.
/// <paramref name="Source"/> is how messages name it: the input, the assembly source or archive(member).</summary>
internal sealed record NativeObject(string Path, IReadOnlySet<string> Functions, IReadOnlySet<string> Data, IReadOnlySet<string> Undefined)
{
    internal string Source { get; init; } = Path;
}

/// <summary>One link input: a source or object with the language in effect for it (from -x), or an archive with its whole-archive state.</summary>
internal sealed record LinkInput(string Path, string? Language, bool WholeArchive);

/// <summary>
/// The interpreted command line: every option classified by the table (<see cref="DriverOptions"/>) for the selected
/// runtime mode, with the handled ones applied, the forwarded ones collected in order and the rejected ones reported.
/// </summary>
internal sealed class Invocation
{
    internal CompilerOptions Options { get; } = new();
    internal string Driver { get; private set; }
    internal List<string> Frontend { get; } = [];
    internal List<LinkInput> Inputs { get; } = [];
    internal List<string> SearchPaths { get; } = [];
    /// <summary>-l libraries with the whole-archive state in effect where each was named.</summary>
    internal List<(string Name, bool WholeArchive)> Libraries { get; } = [];
    internal string? Output { get; private set; }
    internal string? KeepIr { get; private set; }
    internal bool CompileOnly { get; private set; }
    internal bool Textual { get; private set; }
    internal bool Preprocess { get; private set; }
    internal bool EmitLlvm { get; private set; }
    internal bool DependencyOnly { get; private set; }
    internal bool SyntaxOnly { get; private set; }
    internal bool Relocatable { get; private set; }
    internal bool DryRun { get; private set; }
    internal bool AotDebug { get; private set; }
    internal bool LinkerVersion { get; private set; }
    internal bool StartFiles { get; private set; } = true;
    internal bool DefaultLibraries { get; private set; } = true;
    internal bool CxxLibraries { get; private set; } = true;
    internal bool ModuleDirectoryGiven { get; private set; }
    internal string? RuntimeId { get; private set; }
    internal string? AotProject { get; private set; }
    internal string? AotInstructionSet { get; private set; }
    internal string AotPreference { get; private set; } = "Balanced";
    /// <summary>--extension names, in order.</summary>
    internal List<string> ExtensionNames { get; } = [];
    /// <summary>The directories of the native kernel packages to resolve undefined functions against (--native-kernels).</summary>
    internal List<string> KernelDirectories { get; } = [];
    /// <summary>Whether the command line gives --vector-width, which then wins over the width x86-64-v3 implies.</summary>
    internal bool VectorWidthGiven { get; private set; }
    /// <summary>The hidden --print-kernel-data: a file of the data names a kernel package shares with the program.</summary>
    internal string? KernelData { get; private set; }
    internal bool ListExtensions { get; private set; }
    /// <summary>--install-extension: a built extension package to install in the sysroot's ext/ directory.</summary>
    internal string? InstallExtension { get; private set; }
    /// <summary>--uninstall-extension: the name of an installed extension to remove.</summary>
    internal string? UninstallExtension { get; private set; }
    /// <summary>Queries the frontend answers (--version, -dumpmachine, -print-...), run instead of a compilation.</summary>
    internal List<string> Queries { get; } = [];

    internal Invocation(IReadOnlyList<ParsedArgument> arguments, string driver)
    {
        Driver = driver;
        // The mode decides how several options are treated, so it is read before anything is classified.
        foreach (ParsedArgument argument in arguments)
            if (argument.Is("--runtime")) Options.Runtime = argument.Value!;
        if (Options.Runtime == "managed-host")
            throw new ArgumentException("--runtime=managed-host was removed (ADR-04); use --runtime=portable for a platform-neutral .NET ABI or --runtime=system for the native Linux ABI.");
        if (Options.Runtime is not ("system" or "portable")) throw new ArgumentException("--runtime requires portable or system.");
        List<string> linker = [];
        string? language = null;
        bool wholeArchive = false;
        foreach (ParsedArgument argument in arguments)
        {
            if (argument.Spec is null)
            {
                // Linker options between inputs apply to the archives that follow them (--whole-archive).
                wholeArchive = ApplyLinker(linker, wholeArchive);
                linker.Clear();
                Inputs.Add(new LinkInput(argument.Text, language, wholeArchive));
                continue;
            }
            OptionSpec spec = argument.Spec;
            switch (spec.For(Options.Runtime))
            {
                case Handling.Rejected:
                    // Naming the portable target itself changes nothing: build systems pass back what -dumpmachine reported.
                    if (Options.Runtime == "portable" && (argument.Is("--target=") && argument.Text == "--target=" + PortableAbi.Triple ||
                        argument.Is("-target") && argument.Value == PortableAbi.Triple))
                        continue;
                    throw new ArgumentException(DriverOptions.RejectionMessage(spec, argument.Text, Options.Runtime));
                case Handling.Ignored:
                    if (Options.Verbose || arguments.Any(other => other.Is("-v"))) Console.Error.WriteLine("llvmnet: note: " + DriverOptions.IgnoredNote(spec, argument.Text, Options.Runtime));
                    continue;
                case Handling.Forwarded:
                    Frontend.AddRange(argument.AsGiven.Select(text => text.Replace("\0", "", StringComparison.Ordinal)));
                    if (spec.Name is "-module-dir" or "-J") ModuleDirectoryGiven = true;
                    continue;
            }
            string value = argument.Value ?? "";
            switch (spec.Name)
            {
                case "-c": CompileOnly = true; break;
                case "-S": Textual = true; CompileOnly = true; break;
                case "-E": Preprocess = true; break;
                case "-M" or "-MM": DependencyOnly = true; Frontend.Add(argument.Text); break;
                case "-fsyntax-only": SyntaxOnly = true; Frontend.Add(argument.Text); break;
                case "-emit-llvm" or "--emit-llvm": EmitLlvm = true; break;
                case "-r": Relocatable = true; EmitLlvm = true; break;
                case "-shared" or "--emit-library": Options.Library = true; break;
                case "-o": Output = value; break;
                case "-x": language = value == "none" ? null : value; break;
                case "-###": DryRun = true; break;
                case "-L": SearchPaths.Add(value); break;
                case "-l":
                    wholeArchive = ApplyLinker(linker, wholeArchive);
                    linker.Clear();
                    Libraries.Add((value, wholeArchive));
                    break;
                case "-nostdlib": StartFiles = false; DefaultLibraries = false; break;
                case "-nostartfiles": StartFiles = false; break;
                case "-nodefaultlibs": DefaultLibraries = false; break;
                case "-nostdlib++": CxxLibraries = false; break;
                case "-Wl,": linker.AddRange(value.Split(',')); break;
                case "-Xlinker": linker.Add(value); break;
                case "--runtime": break;
                case "--portable-sysroot": PortableAbi.SysrootOverride = Path.GetFullPath(value); break;
                case "--simd128": Options.Isa = "wasm-simd128"; break;
                case "--isa=":
                    // The instruction set the frontend may assume (ADR-16): baseline; x86-64-v2 and x86-64-v3, whose macros
                    // and SSE to AVX2 intrinsics the runtime's helpers translate (Level 2); or wasm-simd128, the SIMD128
                    // header path (--simd128). neon comes with its helpers (SV-11).
                    if (value is "neon")
                        throw new ArgumentException($"--isa={value} is not implemented yet; the supported levels are baseline, x86-64-v2, x86-64-v3 and wasm-simd128.");
                    if (value is not ("baseline" or "x86-64-v2" or "x86-64-v3" or "wasm-simd128")) throw new ArgumentException("--isa requires baseline, x86-64-v2, x86-64-v3 or wasm-simd128.");
                    Options.Isa = value;
                    break;
                case var alias when DriverOptions.IsaAliases.Contains(alias) || DriverOptions.IsaV3Aliases.Contains(alias):
                    // An x86 SIMD feature flag in portable mode (system mode forwards it): its level, unless a higher one is
                    // already given, and the flag itself for Clang, which enables what it names (-mpclmul beyond the level).
                    string level = DriverOptions.IsaV3Aliases.Contains(alias) ? "x86-64-v3" : "x86-64-v2";
                    if (Options.Isa == "wasm-simd128") throw new ArgumentException($"{argument.Text} selects {level}, which cannot be combined with --isa=wasm-simd128.");
                    if (Options.Isa != "x86-64-v3") Options.Isa = level;
                    Frontend.Add(argument.Text);
                    break;
                case "--vector-width=":
                    if (value is not ("128" or "256")) throw new ArgumentException("--vector-width requires 128 or 256.");
                    Options.VectorWidth = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                    VectorWidthGiven = true;
                    break;
                case "--vector-lowering=":
                    if (value is not ("auto" or "typed" or "scalar")) throw new ArgumentException("--vector-lowering requires auto, typed or scalar.");
                    Options.VectorLowering = value;
                    break;
                case "--vector-idioms=":
                    if (value is not ("on" or "off")) throw new ArgumentException("--vector-idioms requires on or off.");
                    Options.VectorIdioms = value == "on";
                    break;
                case "--cil-reference": Options.CilReferences.Add(Path.GetFullPath(value)); break;
                case "--system-library": Options.SystemLibraries.Add(value); break;
                case "--extension": ExtensionNames.Add(value); break;
                case "--native-kernels": KernelDirectories.Add(Path.GetFullPath(value)); break;
                case "--print-kernel-data=": KernelData = Path.GetFullPath(value); break;
                case "--list-extensions": ListExtensions = true; break;
                case "--install-extension": InstallExtension = Path.GetFullPath(value); break;
                case "--uninstall-extension": UninstallExtension = value; break;
                case "--nativeaot": Options.NativeAot = true; break;
                case "--readytorun": Options.ReadyToRun = value; break;
                case "--runtime-id": RuntimeId = value; break;
                case "--aot-project": AotProject = Path.GetFullPath(value); Options.NativeAot = true; break;
                case "--aot-optimize":
                    AotPreference = value;
                    if (AotPreference is not ("Balanced" or "Speed" or "Size")) throw new ArgumentException("--aot-optimize requires Balanced, Speed or Size.");
                    break;
                case "--aot-instruction-set":
                    AotInstructionSet = value;
                    if (AotInstructionSet is not ("baseline" or "x86-64-v2" or "x86-64-v3" or "native")) throw new ArgumentException("--aot-instruction-set requires baseline, x86-64-v2, x86-64-v3 or native.");
                    break;
                case "--aot-debug": AotDebug = true; break;
                case "--keep-ir": KeepIr = Path.GetFullPath(value); break;
                case "--trap-missing-arguments": Options.TrapMissingArguments = true; break;
                case "--diagnose-limits": Options.DiagnoseLimits = true; break;
                case "--report-simd": Options.ReportSimd = true; break;
                case "--check-indirect-calls": Options.CheckIndirectCalls = true; break;
                case "-v": Options.Verbose = true; Queries.Add("-v"); break;
                case "--driver-mode":
                    Driver = value switch
                    {
                        "gcc" or "clang" => "clang",
                        "g++" or "clang++" => "clang++",
                        "flang" => "flang",
                        _ => throw new ArgumentException($"--driver-mode requires gcc, g++, clang, clang++ or flang, not '{value}'.")
                    };
                    break;
                case "--version" or "-dumpmachine" or "-dumpversion" or "-print-search-dirs" or "-print-resource-dir" or "-print-target-triple" or "-print-file-name=" or "-print-prog-name=" or "-print-libgcc-file-name":
                    Queries.Add(argument.Text);
                    break;
                default: throw new InvalidOperationException($"option {spec.Name} is in the table as handled but not implemented.");
            }
        }
        if (linker.Count != 0)
        {
            // Trailing linker options have no archive to apply to, but must still be valid.
            ApplyLinker(linker, wholeArchive);
            linker.Clear();
        }
        Validate();
    }

    private bool ApplyLinker(List<string> linker, bool wholeArchive)
    {
        foreach (ParsedArgument argument in DriverOptions.ParseLinker(linker))
        {
            OptionSpec spec = argument.Spec!;
            switch (spec.For(Options.Runtime))
            {
                case Handling.Rejected:
                    throw new ArgumentException(DriverOptions.RejectionMessage(spec, "-Wl," + argument.Text, Options.Runtime));
                case Handling.Ignored:
                    if (Options.Verbose) Console.Error.WriteLine("llvmnet: note: " + DriverOptions.IgnoredNote(spec, "-Wl," + argument.Text, Options.Runtime));
                    continue;
            }
            switch (spec.Name)
            {
                case "--whole-archive": wholeArchive = true; break;
                case "--no-whole-archive": wholeArchive = false; break;
                case "--version": LinkerVersion = true; break;
                case "--start-group" or "--end-group" or "-(" or "-)": break;
                default: throw new InvalidOperationException($"linker option {spec.Name} is in the table as handled but not implemented.");
            }
        }
        return wholeArchive;
    }

    private void Validate()
    {
        bool nativeAot = Options.NativeAot;
        if (nativeAot && (CompileOnly || Preprocess || DependencyOnly || SyntaxOnly || EmitLlvm || Options.Library))
            throw new ArgumentException("--nativeaot requires an executable link, not -c, -E, -S, --emit-llvm or -shared.");
        if (!nativeAot && (RuntimeId is not null || AotDebug || AotPreference != "Balanced" || AotInstructionSet is not null))
            throw new ArgumentException("NativeAOT output settings require --nativeaot.");
        if (Options.ReadyToRun is not null && (nativeAot || CompileOnly || Preprocess || DependencyOnly || SyntaxOnly || EmitLlvm || Options.Library))
            throw new ArgumentException("--readytorun requires an executable link to a JIT image, not --nativeaot, -c, -E, -S, --emit-llvm or -shared.");
        if (Options.ReadyToRun is string target && !Toolchain.ReadyToRunTargets.Contains(target))
            throw new ArgumentException($"--readytorun requires one of {string.Join(", ", Toolchain.ReadyToRunTargets)}.");
        if (Options.Runtime == "system" && Options.ReadyToRun is not (null or "linux-x64"))
            throw new ArgumentException("The current system ABI profile requires --readytorun linux-x64.");
        if (AotProject is not null && RuntimeId is not null)
            throw new ArgumentException("--aot-project takes no --runtime-id: the runtime identifier is chosen when the project is published (dotnet publish -r <rid>).");
        if (Options.Runtime != "system" && Options.SystemLibraries.Count != 0) throw new ArgumentException("--system-library requires --runtime=system.");
        // x86-64-v3 code targets AVX2 hosts, where 256-bit vectors held whole run faster than as two halves (SV-06); a
        // --vector-width the command line gives wins.
        if (Options.Isa == "x86-64-v3" && !VectorWidthGiven) Options.VectorWidth = 256;
        if (PortableAbi.SysrootOverride is not null && Options.Runtime != "portable") throw new ArgumentException("--portable-sysroot requires --runtime=portable.");
        if (Options.Runtime == "system" && RuntimeId is not null && RuntimeId != "linux-x64")
            throw new ArgumentException("The current system ABI profile requires NativeAOT RID linux-x64.");
        // An extension that is not installed fails before anything runs, whether or not this invocation links.
        foreach (string name in ExtensionNames) Extensions.Find(name);
        // Native kernel packages are portable mode's: system mode links native objects and libraries itself (SV-15).
        if (KernelDirectories.Count != 0 && Options.Runtime == "system")
            throw new ArgumentException("--native-kernels is for portable mode; in system mode link the package's linux-x64 library with --system-library.");
        foreach (string directory in KernelDirectories)
        {
            KernelPackage package = KernelPackage.Read(directory);
            if (Options.KernelPackages.FirstOrDefault(other => other.Name == package.Name || other.Library == package.Library) is KernelPackage clash)
                throw new ArgumentException($"native kernel packages {clash.Directory} and {package.Directory} have the same name or library.");
            Options.KernelPackages.Add(package);
        }
        if (LinkerVersion && (Inputs.Count != 0 || Output is not null || CompileOnly || Preprocess || DependencyOnly || EmitLlvm || nativeAot || Options.Library))
            throw new ArgumentException("-Wl,--version is an informational query, not a link operation.");
    }
}

internal static class Driver
{
    /// <summary>-l names that are parts of the C library in both modes and need no archive.</summary>
    internal static readonly string[] CLibraryNames = ["c", "m", "pthread", "dl", "rt", "util", "crypt", "resolv"];

    /// <summary>-l names of the C++ and Fortran runtimes, which the portable sysroot supplies.</summary>
    internal static readonly string[] PortableRuntimeNames = ["c++", "c++abi", "stdc++", "supc++", "flang_rt.runtime", "FortranRuntime", "FortranDecimal"];

    internal static int Run(string[] arguments)
    {
        arguments = Toolchain.ExpandResponseFiles(arguments);
        string driver = Environment.GetEnvironmentVariable("LLVMNET_DRIVER") ?? "clang";
        List<ParsedArgument> parsed = DriverOptions.Parse(arguments);
        if (arguments.Length == 0 || parsed.Any(argument => argument.Is("--help")))
        {
            Console.Write(DriverOptions.Help(driver));
            return arguments.Length == 0 ? 2 : 0;
        }
        if (parsed.Any(argument => argument.Is("--print-driver-reference"))) { Console.Write(DriverOptions.Reference()); return 0; }
        if (parsed.Any(argument => argument.Is("--print-option-table"))) { Console.Write(DriverOptions.OptionTable()); return 0; }
        if (parsed.Any(argument => argument.Is("--print-host-limitations")))
        {
            // The services the portable host cannot provide, as the runtime declares them (scripts/limitations.py).
            foreach (string line in HostSyscalls.DescribeUnavailable()) Console.WriteLine(line);
            return 0;
        }
        if (parsed.Any(argument => argument.Is("--print-extension-kit")))
        {
            // What build-extensions.sh needs from the toolchain it runs with, in the repository, the toolchain package or
            // the dotnet tool (NX-12): the version stamped into an extension assembly, the runtime assembly it compiles
            // against, the default portable sysroot, and where the script itself and the template are.
            string kit = Path.Combine(Toolchain.Root, "share", "llvmnet", "extensions");
            if (!System.IO.Directory.Exists(kit)) kit = Path.Combine(Toolchain.Root, "scripts");
            string template = Path.Combine(Toolchain.Root, "share", "llvmnet", "templates", "native-extension");
            if (!System.IO.Directory.Exists(template)) template = Path.Combine(Toolchain.Root, "templates", "native-extension");
            Console.Write($"version {BuildInfo.Version}\nruntime {typeof(BuildInfo).Assembly.Location}\nsysroot {PortableAbi.Sysroot}\n" +
                $"kit {kit}\ntemplate {template}\n");
            return 0;
        }
        var invocation = new Invocation(parsed, driver);
        driver = invocation.Driver;
        if (invocation.ListExtensions) { Console.Write(Extensions.Listing()); return 0; }
        if (invocation.InstallExtension is not null) { Console.WriteLine(Extensions.Install(invocation.InstallExtension)); return 0; }
        if (invocation.UninstallExtension is not null) { Console.WriteLine(Extensions.Uninstall(invocation.UninstallExtension)); return 0; }
        CompilerOptions options = invocation.Options;
        if (!invocation.Frontend.Contains("-w")) options.Warnings = new Diagnostics(arguments);
        string clang = Toolchain.Executable("clang-22", "LLVMNET_CLANG");
        string flang = Toolchain.Executable("flang-22", "LLVMNET_FLANG");
        Toolchain.DryRun = invocation.DryRun;
        if (invocation.Queries.Count != 0 && (invocation.Queries.Count > 1 || invocation.Queries[0] != "-v" || invocation.Inputs.Count == 0))
        {
            // Frontend queries are answered by the selected frontend, as Clang answers them without compiling anything.
            string executable = driver == "flang" ? flang : driver == "clang++" ? Toolchain.Executable("clang++-22", "LLVMNET_CLANG") : clang;
            if (invocation.Queries.Contains("--version"))
            {
                Console.WriteLine($"llvmnet {BuildInfo.Version} (LLVM {Llvm.Version}; ABIs system-linux-x64-v1, {PortableAbi.Tag})");
                Console.WriteLine("Frontend follows:");
            }
            // In portable mode the frontend answers for the portable target (-dumpmachine prints its triple).
            List<string> target = options.Runtime == "portable" ? ["--target=" + PortableAbi.Triple] : [];
            return Toolchain.Run(executable, [.. target, .. invocation.Queries.Where(query => query != "-v" || invocation.Inputs.Count == 0), .. invocation.Frontend], options.Verbose);
        }
        if (invocation.LinkerVersion)
        {
            Console.WriteLine("LLD-compatible llvmnet linker interface (LLVM 22.1.8; emits CIL, not native images)");
            return 0;
        }
        List<string> frontend = invocation.Frontend;
        if (options.Simd128)
            frontend.InsertRange(0, ["-D__LLVMNET_SIMD128__=1", "-I", Path.Combine(Toolchain.Root, "sysroot", "simd128", "include")]);
        List<LinkInput> inputs = invocation.Inputs;
        bool verbose = options.Verbose;
        // Preprocessing, dependency generation and -fsyntax-only run the frontend alone, with the mode's headers.
        if (invocation.Preprocess || invocation.DependencyOnly || invocation.SyntaxOnly)
        {
            List<string> preprocessing = [.. frontend];
            if (invocation.Preprocess) preprocessing.Add("-E");
            foreach (LinkInput input in inputs)
            {
                if (input.Language is not null) preprocessing.AddRange(["-x", input.Language]);
                preprocessing.Add(input.Path);
            }
            if (invocation.Output is not null)
                preprocessing.AddRange(["-o", invocation.Output]);
            bool fortranInput = driver == "flang" || inputs.Any(IsFortran);
            if (options.Runtime == "portable") preprocessing.InsertRange(0, PortableAbi.Arguments(frontend, fortranInput, driver == "clang++" || inputs.Any(IsCpp), options.Isa));
            else if (!fortranInput) preprocessing.InsertRange(0, ["-idirafter", Extensions.InterfaceInclude]);
            if (!fortranInput) preprocessing.InsertRange(0, IsaArguments(options.Isa));
            if (driver == "clang++") preprocessing.Insert(0, "--driver-mode=g++");
            return Toolchain.Run(fortranInput ? flang : clang, preprocessing, verbose, options.Runtime == "portable");
        }
        if (inputs.Count == 0 && invocation.Libraries.Count == 0)
            throw new ArgumentException("No input files.");
        if (invocation.CompileOnly && inputs.Count != 1 && invocation.Output is not null)
            throw new ArgumentException("Cannot use one -o with multiple inputs and -c.");

        string temporary = Path.Combine(Path.GetTempPath(), $"llvmnet-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporary);
        try
        {
            List<(string Path, bool WholeArchive)> bitcode = [];
            // System mode: native objects (assembled kernels) for the image's native library (SV-15).
            List<string> nativeInputs = [];
            Dictionary<string, string> assemblySources = [];
            string members = Path.Combine(temporary, "native-members");
            foreach (LinkInput input in inputs)
            {
                string extension = Path.GetExtension(input.Path);
                if (input.Language is "assembler" or "assembler-with-cpp" || input.Language is null && extension is ".s" or ".S" or ".sx" or ".asm")
                {
                    string assembled = invocation.CompileOnly ? invocation.Output ?? Path.GetFileNameWithoutExtension(input.Path) + ".o" : Path.Combine(temporary, $"native{nativeInputs.Count}.o");
                    int assembly = Assemble(input, assembled, options, clang, frontend, verbose);
                    if (assembly != 0) return assembly;
                    if (!invocation.CompileOnly && !invocation.DryRun)
                    {
                        nativeInputs.Add(assembled);
                        assemblySources[assembled] = input.Path;
                    }
                    continue;
                }
                if (input.Language is null && IsNativeObject(input.Path))
                {
                    // A native object on the link line: system mode links it into the image's native library, and a
                    // portable link with a kernel package leaves it to the package, as it does archives' native members.
                    if (invocation.CompileOnly) throw new NotSupportedException($"-c needs a source, LLVM IR or a bitcode object; {input.Path} is a native object file.");
                    if (options.Runtime == "system") nativeInputs.Add(input.Path);
                    else if (options.KernelPackages.Count == 0)
                        throw new NotSupportedException($"{input.Path} is a native object file, such as assembled code; llvmnet links bitcode, and portable mode calls native code through a native kernel package (--native-kernels, docs/user/native-kernels.md); system mode (--runtime=system) links native objects into the image's native library.");
                    continue;
                }
                // A linker input is what its content says, as for a native linker: an archive named .so or .lib, say.
                if (input.Language is null && !SourceExtensions.Contains(extension) && !IsFortran(input))
                    extension = LinkerInputKind(input.Path) ?? extension;
                bool ir = input.Language == "ir" || input.Language is null && extension is ".bc" or ".ll";
                if (extension is ".o" or ".a" && input.Language is null || ir && !invocation.CompileOnly)
                {
                    if (invocation.CompileOnly)
                    {
                        // -c on an object re-tags nothing: the object keeps the tag it has, which must match the mode.
                        if (extension == ".a") throw new NotSupportedException("-c requires a source, LLVM IR or an object, not an archive.");
                        string copy = invocation.Output ?? Path.GetFileNameWithoutExtension(input.Path) + ".o";
                        if (!invocation.DryRun) Llvm.Link([input.Path], [false], copy, options.AbiTag);
                        continue;
                    }
                    bitcode.Add((input.Path, input.WholeArchive && extension == ".a"));
                    continue;
                }
                bool fortran = driver == "flang" || IsFortran(input);
                bool cpp = driver == "clang++" || IsCpp(input);
                if (!fortran && input.Language is null && !SourceExtensions.Contains(extension))
                    throw new NotSupportedException($"Unsupported input kind: {input.Path} (name the language with -x)");
                string destination = invocation.CompileOnly ? invocation.Output ?? Path.GetFileNameWithoutExtension(input.Path) + (invocation.Textual ? ".ll" : ".o") : Path.Combine(temporary, $"input{bitcode.Count}.bc");
                // -O1 unless the command line gives a level (ADR-11). The frontend then decides the vectorizers by the level,
                // as natively: -O2 and above run them, -O1 does not (the owner's SV-05 decision of 2026-11-05).
                List<string> command = ["-O1"];
                if (driver == "clang++") command.Add("--driver-mode=g++");
                // LLVM IR gets the mode's target and macros only; its headers and feature macros do not apply.
                if (options.Runtime == "portable") command.AddRange(ir ? PortableAbi.Arguments(frontend, true, false, "baseline") : PortableAbi.Arguments(frontend, fortran, cpp, options.Isa));
                // <llvmnet/ext.h> after the host's headers, so that it shadows nothing.
                else if (!fortran && !ir) command.AddRange(["-idirafter", Extensions.InterfaceInclude]);
                if (fortran)
                {
                    if (!invocation.ModuleDirectoryGiven)
                        command.AddRange(["-module-dir", invocation.CompileOnly ? Path.GetDirectoryName(Path.GetFullPath(destination))! : temporary]);
                }
                else
                    command.Add("-fno-stack-protector");
                if (!fortran && !ir) command.AddRange(IsaArguments(options.Isa));
                // Mixed-language builds pass Fortran module options to every input; Clang does not accept them.
                command.AddRange(fortran ? frontend : WithoutFortranOptions(frontend));
                command.AddRange(["-emit-llvm", invocation.Textual ? "-S" : "-c"]);
                if (input.Language is not null) command.AddRange(["-x", input.Language]);
                command.AddRange([input.Path, "-o", destination]);
                int result = Toolchain.Run(fortran ? flang : clang, command, verbose, options.Runtime == "portable");
                if (result != 0)
                    return result;
                if (invocation.DryRun) continue;
                if (!File.Exists(destination))
                    throw new InvalidOperationException($"the frontend did not write {destination}; an option such as -fdriver-only or a -print query stops it before code generation.");
                try { Llvm.StampAbi(destination, options.AbiTag, options.Isa, invocation.Textual); }
                catch (InvalidOperationException error)
                {
                    throw new InvalidOperationException($"the frontend's output {destination} is not LLVM {(invocation.Textual ? "IR" : "bitcode")} ({error.Message}); an option such as --analyze, -emit-ast or --precompile changes what the frontend writes.");
                }
                if (!frontend.Contains("-w")) WarnX86Intrinsics(destination, options.Runtime == "system", new Diagnostics(arguments));
                bitcode.Add((destination, false));
            }
            if (invocation.CompileOnly || invocation.DryRun)
                return 0;
            List<ExtensionPackage> extensions = [.. invocation.ExtensionNames.Select(Extensions.Find)];
            foreach ((string library, bool wholeArchive) in invocation.Libraries)
            {
                // Parts of the C library in both modes; in portable mode the sysroot also supplies the C++ and Fortran runtimes.
                if (CLibraryNames.Contains(library) || options.Runtime == "portable" && PortableRuntimeNames.Contains(library))
                    continue;
                // -l:name names the file exactly; -lname looks for libname.a, then for a native extension that answers the name.
                string file = library.StartsWith(':') ? library[1..] : $"lib{library}.a";
                string? archive = invocation.SearchPaths.Select(directory => Path.Combine(directory, file)).FirstOrDefault(File.Exists);
                if (archive is null && !library.StartsWith(':') && Extensions.ForLibrary(library) is ExtensionPackage extension)
                {
                    extensions.Add(extension);
                    continue;
                }
                if (archive is null)
                    throw new FileNotFoundException($"Cannot find LLVM bitcode archive {file} in the -L directories ({string.Join(", ", invocation.SearchPaths.DefaultIfEmpty("none given"))}), and no native extension answers -l{library}. Native libraries need --runtime=system with --system-library, or a native extension (llvmnet --list-extensions).");
                bitcode.Add((archive, wholeArchive));
            }
            // A native extension is a managed reference registered at start in portable mode, and its Linux libraries in
            // system mode, where generated bridges call them directly.
            extensions = [.. extensions.DistinctBy(extension => extension.Name)];
            Extensions.Require(extensions);
            foreach (ExtensionPackage extension in extensions)
            {
                if (options.Runtime == "system")
                {
                    if (extension.Linux.Count == 0) throw new InvalidOperationException($"native extension '{extension.Name}' names no Linux library for system mode.");
                    options.SystemLibraries.AddRange(extension.Linux.Except(options.SystemLibraries));
                    options.SystemExtensions.Add(extension);
                    continue;
                }
                options.CilReferences.Add(extension.Assembly);
                options.Extensions.Add(extension);
                if (File.Exists(extension.Wrappers)) bitcode.Add((extension.Wrappers, false));
            }
            // Portable executables link musl's start object and the sysroot archives, in dependency order. Libraries do
            // not: musl keeps per-process state (thread pointer, heap, stdio) that one image per process must own. Nor
            // does a relocatable link (-r), which combines the inputs for a later link.
            if (options.Runtime == "portable" && !options.Library && !invocation.Relocatable)
            {
                PortableAbi.RequireSysroot();
                if (invocation.StartFiles) bitcode.Add((PortableAbi.Library("crt1.o"), false));
                if (invocation.DefaultLibraries)
                    foreach (string archive in new[] { "libomp.a", "libflang_rt.runtime.a", "libc++.a", "libc++abi.a", "libc.a", "libclang_rt.builtins.a" })
                        if ((invocation.CxxLibraries || archive is not ("libc++.a" or "libc++abi.a")) && File.Exists(PortableAbi.Library(archive)))
                            bitcode.Add((PortableAbi.Library(archive), false));
            }
            // System executables take the Fortran runtime from flang-rt built against glibc, and compiler-rt's x86 CPU model
            // (__builtin_cpu_supports, target_clones), which the native libraries do not export; the C library is native.
            if (options.Runtime == "system" && !options.Library && !invocation.Relocatable && invocation.DefaultLibraries)
                foreach (string archive in new[] { "libflang_rt.runtime.a", "libclang_rt.builtins.a" })
                    if (Path.Combine(Toolchain.Root, "sysroot", "system-linux-x64-v1", "lib", archive) is string path && File.Exists(path))
                        bitcode.Add((path, false));
            // System mode links the native objects given on the command line and the native members of the archives
            // into the image's native library, and every symbol they reference stays in the bitcode link (SV-15).
            if (options.Runtime == "system")
            {
                Directory.CreateDirectory(members);
                foreach (string native in nativeInputs)
                    options.NativeObjects.AddRange(Llvm.NativeObjects(native, members).Select(item => item with { Source = assemblySources.GetValueOrDefault(native, native) }));
                foreach ((string path, _) in bitcode)
                    if (LinkerInputKind(path) == ".a")
                        options.NativeObjects.AddRange(Llvm.NativeObjects(path, members).Select(item => item with { Source = $"{path}({Path.GetFileName(item.Path).Split('-', 2)[^1]})" }));
            }
            HashSet<string> nativeDefined = [.. options.NativeObjects.SelectMany(native => native.Functions.Concat(native.Data))];
            string[] nativeReferences = [.. options.NativeObjects.SelectMany(native => native.Undefined).Where(name => !nativeDefined.Contains(name)).Distinct().Order(StringComparer.Ordinal)];
            string linked = Path.Combine(temporary, "linked.bc");
            // An executable's root is its entry point, which an archive member may define (gtest_main, Catch2's main),
            // as crt1.o's reference to main extracts it natively. A library or a relocatable object has no entry point.
            bool executable = !options.Library && !invocation.Relocatable;
            if (!executable && bitcode.All(input => LinkerInputKind(input.Path) == ".a" && !input.WholeArchive))
                throw new ArgumentException("Provide at least one explicit object or source as the link root, or -Wl,--whole-archive before an archive.");
            // An executable that uses a native extension also links the musl port's thread attachment, which the
            // extensions' reverse thunks need on threads a host library started (NX-08).
            string[] threadAttachment = executable && options.Extensions.Count != 0 && options.Runtime == "portable" && invocation.DefaultLibraries
                ? ["__llvmnet_attach_thread", "__llvmnet_detach_thread"] : [];
            // Data a kernel package shares with the program stays in the link even when only the package's code reads it.
            string[] dataNames = invocation.KernelData is null ? [] : [.. File.ReadAllLines(invocation.KernelData).Select(line => line.Trim()).Where(line => line.Length != 0)];
            string[] kernelData = [.. options.KernelPackages.SelectMany(package => package.Data), .. dataNames];
            Llvm.Link([.. bitcode.Select(input => input.Path)], [.. bitcode.Select(input => input.WholeArchive)], linked, options.AbiTag,
                executable && invocation.KernelData is null ? ["main", "_QQmain"] : [], [.. threadAttachment, .. kernelData, .. nativeReferences],
                nativeMembers: options.KernelPackages.Count != 0 || invocation.KernelData is not null || options.Runtime == "system");
            if (invocation.KernelData is not null)
            {
                using var dataModule = new LlvmModule(linked);
                string source = KernelDataSource(dataModule.Handle, dataNames);
                if (invocation.Output is null) Console.Write(source);
                else File.WriteAllText(invocation.Output, source);
                return 0;
            }
            if (invocation.KeepIr is not null)
                File.Copy(linked, invocation.KeepIr, true);
            string output = Path.GetFullPath(invocation.Output ?? (options.Library ? "a.dll" : invocation.Relocatable ? "a.o" : invocation.EmitLlvm ? "a.bc" : "a.out"));
            if (invocation.EmitLlvm)
            {
                File.Copy(linked, output, true);
                return 0;
            }
            string lowered = Path.Combine(temporary, "lowered.bc");
            using (var prepared = new LlvmModule(linked))
            {
                Llvm.WrapVectorInvokes(prepared.Handle);
                Llvm.NarrowVectorIntegers(prepared.Handle);
                Llvm.FuseMultiplyAdds(prepared.Handle);
                Llvm.RemoveTargetAttributes(prepared.Handle);
                Llvm.LowerConstrainedFloatingPoint(prepared.Handle);
                if (Llvm.LLVMWriteBitcodeToFile(prepared.Handle, linked) != 0)
                    throw new IOException("Cannot write the legalized module.");
            }
            // The typed vector lowering keeps vector code whole, reductions included, and expands and scalarizes only the
            // functions it does not translate (VectorEmitter.Legalize); without it every vector operation becomes scalar code.
            if (options.Simd128 && !options.TypedVectors)
                throw new ArgumentException("--simd128 needs the typed vector lowering; it cannot be combined with --vector-lowering=scalar.");
            string scalarizer = options.TypedVectors ? "" : ",scalarizer<load-store>";
            string reductions = options.TypedVectors ? "" : "expand-reductions,";
            string passes = (options.Library ? "" : "internalize,globaldce,") + $"function(lower-constant-intrinsics,{reductions}scalarize-masked-mem-intrin{scalarizer}),globaldce,verify";
            List<string> optimization = [$"-passes={passes}", linked, "-o", lowered];
            if (!options.Library) optimization.Add("--internalize-public-api-list=main,_QQmain" + (options.Runtime == "portable" ? ",__llvmnet_libc_main,__llvmnet_register_finalizer,__llvmnet_cxx_can_catch" : "") +
                (options.Extensions.Count != 0 && options.Runtime == "portable" ? ",__llvmnet_attach_thread,__llvmnet_detach_thread" : "") +
                string.Concat(options.KernelPackages.SelectMany(package => package.Data).Concat(nativeReferences).Select(data => "," + data)));
            int optimizeResult = Toolchain.Run(Toolchain.Executable("opt-22", "LLVMNET_OPT"), optimization, verbose);
            if (optimizeResult != 0)
                return optimizeResult;
            string linkedInput = linked;
            linked = lowered;
            using var module = new LlvmModule(linked);
            bool nativeAot = options.NativeAot;
            bool launcher = !nativeAot && !options.Library && !output.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
            string payloadDirectory = nativeAot ? Path.Combine(temporary, "payload") : launcher ? output + ".llvmnet" : Path.GetDirectoryName(output)!;
            Directory.CreateDirectory(payloadDirectory);
            string payload = nativeAot || launcher ? Path.Combine(payloadDirectory, "program.dll") : output;
            // An image of x86-64-v3 objects holds its 256-bit vectors whole, as the level's command line does (SV-06, SV-09).
            if (!invocation.VectorWidthGiven && Llvm.IsaLevels(module.Handle).Split(',').Contains("x86-64-v3")) options.VectorWidth = 256;
            var compiler = new CilCompiler(options);
            try
            {
                compiler.Compile(module.Handle, payload);
            }
            catch (CompilationError error)
            {
                // Keep the linked IR so the failure can be reproduced with "llvmnet <file> -o ...".
                string kept = output + ".failed.bc";
                File.Copy(linkedInput, kept, true);
                error.KeptIr = kept;
                throw;
            }
            string levels = Llvm.IsaLevels(module.Handle);
            if (invocation.AotProject is not null)
            {
                Toolchain.AotProject(payload, invocation.AotProject, invocation.AotPreference, AotInstructionSet(invocation, levels, compiler.HasVectorCode, verbose), invocation.AotDebug, options.SystemLibraries, options.KernelPackages);
                return 0;
            }
            // A JIT image loads each identifier's kernel library from runtimes/<rid>/native/ beside it.
            if (!nativeAot)
                foreach (KernelPackage package in options.KernelPackages)
                    foreach ((string identifier, (string? shared, _)) in package.Runtimes)
                    {
                        if (shared is null) continue;
                        string native = Path.Combine(payloadDirectory, "runtimes", identifier, "native");
                        Directory.CreateDirectory(native);
                        File.Copy(shared, Path.Combine(native, Path.GetFileName(shared)), true);
                    }
            if (nativeAot) return Toolchain.NativeAot(payload, output, temporary, invocation.RuntimeId, invocation.AotPreference, AotInstructionSet(invocation, levels, compiler.HasVectorCode, verbose), verbose, invocation.AotDebug, options.SystemLibraries, options.KernelPackages);
            if (options.ReadyToRun is string rid && Toolchain.ReadyToRun(payload, rid, temporary, verbose) is int precompiled && precompiled != 0)
                return precompiled;
            if (launcher) Toolchain.Launcher(output, payload);
            return 0;
        }
        finally
        {
            Directory.Delete(temporary, true);
        }
    }

    /// <summary>
    /// The C source of a native kernel package's shared data (--print-kernel-data, SV-13), which the package's build
    /// compiles into its libraries, and of system mode's native objects' (SV-15), which llvmnet links into the image's
    /// native library: storage of each named variable the program's bitcode defines, with its size and
    /// alignment, declarations of those the package defines itself, and for each an accessor, llvmnet_kernel_data_&lt;name&gt;,
    /// that returns its address and through which the program uses the package's copy.
    /// </summary>
    internal static string KernelDataSource(nint module, IEnumerable<string> names)
    {
        nint layout = Llvm.LLVMCreateTargetData(Llvm.LLVMGetDataLayoutStr(module));
        var source = new System.Text.StringBuilder("/* Generated by llvmnet --print-kernel-data: the data this kernel package shares with the program. */\n");
        foreach (string name in names)
        {
            nint global = Llvm.LLVMGetNamedGlobal(module, name);
            if (global != 0 && Llvm.LLVMGetInitializer(global) != 0)
            {
                nint type = Llvm.LLVMGlobalGetValueType(global);
                uint alignment = Math.Max(Llvm.LLVMGetAlignment(global), Llvm.LLVMABIAlignmentOfType(layout, type));
                source.Append($"__attribute__((aligned({alignment}))) unsigned char {name}[{Math.Max(Llvm.LLVMABISizeOfType(layout, type), 1)}];\n");
            }
            else
                source.Append($"extern unsigned char {name}[];\n");
            source.Append($"void *{KernelPackage.Accessor(name)}(void) {{ return {name}; }}\n");
        }
        return source.ToString();
    }

    /// <summary>
    /// ILC's instruction set: the one given, or for an x64 program with objects compiled at --isa=x86-64-v2 or x86-64-v3
    /// (the image's llvmnet.isa levels) the highest of them, which such a program requires as it does natively; for one
    /// with SIMD128 objects x86-64-v2, whose SSSE3 gives Vector128.Shuffle its pshufb (the owner's PF-08 decision of
    /// 2026-11-05), and for one with vector code x86-64-v2, whose SSE4.1 the vectorizers' widenings, multiplications and
    /// blends need (at the baseline dav1d decoded 1.26 times slower vectorized than scalar; the owner's SV-05 decision of
    /// 2026-11-05); otherwise the baseline, where SSE4.2 and AVX2 are only opportunistic sets that the helpers test at run
    /// time and the generic vector operations do not use.
    /// </summary>
    private static string AotInstructionSet(Invocation invocation, string levels, bool vectorCode, bool verbose)
    {
        if (invocation.AotInstructionSet is not null) return invocation.AotInstructionSet;
        if (!(invocation.RuntimeId ?? System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier).EndsWith("-x64", StringComparison.Ordinal)) return "baseline";
        string[] recorded = levels.Split(',');
        if (recorded.Contains("x86-64-v3")) return "x86-64-v3";
        if (recorded.Contains("x86-64-v2") || recorded.Contains("wasm-simd128")) return "x86-64-v2";
        if (!vectorCode) return "baseline";
        if (verbose) Console.Error.WriteLine("llvmnet: note: NativeAOT targets x86-64-v2 (SSE4.2), because the program has vector code; --aot-instruction-set baseline gives an executable for every x64 processor");
        return "x86-64-v2";
    }

    /// <summary>
    /// The frontend options of an ISA level for C and C++: x86-64-v2 selects SSE3 to SSE4.2 and POPCNT as -march does,
    /// x86-64-v3 also AVX, AVX2, FMA, F16C, BMI1, BMI2, LZCNT and MOVBE, with their macros, intrinsics and vectorizer
    /// costs, but not CMPXCHG16B, so that 16-byte atomics stay the library calls objects of every level share.
    /// </summary>
    private static List<string> IsaArguments(string isa) => isa is "x86-64-v2" or "x86-64-v3" ? ["-march=" + isa, "-mno-cx16"] : [];

    /// <summary>
    /// Warns, when the unit is compiled, about each x86 target intrinsic the backend does not translate
    /// (FunctionEmitter.TranslatesX86Intrinsic): such intrinsics come from SSE and AVX code paths, which system mode's
    /// native feature macros select, and the translation rejects them only at the final link. A warning, not an error:
    /// the link drops unreferenced functions, and library code often holds such a function that few programs reach
    /// (flang-rt's floating-point exception accessors read MXCSR). Bitcode keeps symbol names verbatim in its string
    /// table, so a unit without the prefix is not parsed a second time.
    /// </summary>
    private static void WarnX86Intrinsics(string path, bool system, Diagnostics diagnostics)
    {
        if (File.ReadAllBytes(path).AsSpan().IndexOf("llvm.x86."u8) < 0) return;
        using var module = new LlvmModule(path);
        foreach (nint function in Llvm.Functions(module.Handle))
        {
            string name = Llvm.Name(function);
            if (!name.StartsWith("llvm.x86.", StringComparison.Ordinal) || FunctionEmitter.TranslatesX86Intrinsic(name)) continue;
            for (nint use = Llvm.LLVMGetFirstUse(function); use != 0; use = Llvm.LLVMGetNextUse(use))
            {
                nint call = Llvm.LLVMGetUser(use);
                if (Llvm.LLVMIsAInstruction(call) == 0) continue;
                nint caller = Llvm.LLVMGetBasicBlockParent(Llvm.LLVMGetInstructionParent(call));
                string? location = Llvm.DebugLocation(call) ?? Llvm.DebugLocation(caller);
                diagnostics.Warning(location, FunctionEmitter.X86IntrinsicMessage(name, system, compiling: true));
                diagnostics.Note(null, $"in function '{Llvm.Demangle(Llvm.Name(caller))}'{(location is null ? " (compile with -g or -gline-tables-only for source locations)" : "")}");
                break;
            }
        }
    }

    private static List<string> WithoutFortranOptions(List<string> arguments)
    {
        List<string> result = [];
        for (int index = 0; index < arguments.Count; index++)
        {
            if (arguments[index] is "-module-dir" or "-J" or "-fintrinsic-modules-path") { index++; continue; }
            if (arguments[index].StartsWith("-J", StringComparison.Ordinal) || arguments[index].StartsWith("-fintrinsic-modules-path", StringComparison.Ordinal)) continue;
            result.Add(arguments[index]);
        }
        return result;
    }

    private static readonly HashSet<string> SourceExtensions = new(StringComparer.Ordinal) { ".c", ".cc", ".cpp", ".cxx", ".C", ".i", ".ii", ".ll", ".bc" };

    /// <summary>Whether a file is a native object (ELF, COFF or Mach-O), by its content.</summary>
    internal static bool IsNativeObject(string path)
    {
        Span<byte> magic = stackalloc byte[20];
        int read;
        try
        {
            using FileStream stream = File.OpenRead(path);
            read = stream.ReadAtLeast(magic, magic.Length, throwOnEndOfStream: false);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        // ELF: the relocatable type (1) at offset 16; Mach-O's 64- and 32-bit magic; COFF objects for x64 and ARM64.
        if (read >= 18 && magic[..4].SequenceEqual((ReadOnlySpan<byte>)[0x7F, (byte)'E', (byte)'L', (byte)'F'])) return magic[16] == 1 && magic[17] == 0;
        if (read >= 4 && (magic[..4].SequenceEqual((ReadOnlySpan<byte>)[0xCF, 0xFA, 0xED, 0xFE]) || magic[..4].SequenceEqual((ReadOnlySpan<byte>)[0xCE, 0xFA, 0xED, 0xFE]))) return true;
        return read >= 2 && (magic[..2].SequenceEqual((ReadOnlySpan<byte>)[0x64, 0x86]) || magic[..2].SequenceEqual((ReadOnlySpan<byte>)[0x64, 0xAA]));
    }

    /// <summary>
    /// Assembles an assembly source natively (SV-15): .s and .S (and -x assembler, assembler-with-cpp) with Clang for
    /// x86-64 Linux, with the command line's preprocessor and target options, and .asm with nasm (elf64, its -I and -D
    /// options). Only system mode links native objects; portable mode calls assembly through a kernel package.
    /// </summary>
    private static int Assemble(LinkInput input, string destination, CompilerOptions options, string clang, List<string> frontend, bool verbose)
    {
        if (options.Runtime != "system")
            throw new NotSupportedException($"{input.Path} is assembly, which llvmnet does not translate; portable mode calls assembled code through a native kernel package (--native-kernels, docs/user/native-kernels.md), and system mode (--runtime=system) links it into the image's native library.");
        if (input.Language is null && Path.GetExtension(input.Path) == ".asm")
        {
            List<string> nasm = ["-f", "elf64"];
            for (int index = 0; index < frontend.Count; index++)
            {
                string argument = frontend[index];
                string? value = argument is "-I" or "-D" && index + 1 < frontend.Count ? frontend[++index] : argument.Length > 2 && argument[0] == '-' && argument[1] is 'I' or 'D' ? argument[2..] : null;
                if (value is null) continue;
                nasm.Add((argument[1] == 'I' ? "-I" : "-D") + (argument[1] == 'I' && !value.EndsWith('/') ? value + "/" : value));
            }
            nasm.AddRange([input.Path, "-o", destination]);
            return Toolchain.Run(Toolchain.Executable("nasm", "LLVMNET_NASM"), nasm, verbose);
        }
        List<string> command = ["--target=x86_64-linux-gnu", "-c", .. WithoutFortranOptions(frontend)];
        if (input.Language is not null) command.AddRange(["-x", input.Language]);
        command.AddRange([input.Path, "-o", destination]);
        return Toolchain.Run(clang, command, verbose);
    }

    /// <summary>
    /// What a file whose extension names no language is, read from its content: Clang hands such a file to the linker,
    /// so llvmnet links it when it is an LLVM bitcode object (".o") or an archive (".a"), whatever its name. CMake's
    /// Generic platform, for one, names objects <c>.obj</c>, and Chromium's toolchains archives <c>.so</c> or <c>.lib</c>.
    /// </summary>
    internal static string? LinkerInputKind(string path)
    {
        Span<byte> magic = stackalloc byte[8];
        int read;
        try
        {
            using FileStream stream = File.OpenRead(path);
            read = stream.ReadAtLeast(magic, magic.Length, throwOnEndOfStream: false);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        if (read == 8 && (magic.SequenceEqual("!<arch>\n"u8) || magic.SequenceEqual("!<thin>\n"u8))) return ".a";
        // Raw bitcode starts with 'BC' 0xC0DE; the bitcode wrapper header with the little-endian word 0x0B17C0DE.
        if (read >= 4 && (magic[..4].SequenceEqual((ReadOnlySpan<byte>)[0x42, 0x43, 0xC0, 0xDE]) ||
                          magic[..4].SequenceEqual((ReadOnlySpan<byte>)[0xDE, 0xC0, 0x17, 0x0B]))) return ".o";
        return null;
    }

    private static bool IsFortran(LinkInput input) => input.Language is "f95" or "f95-cpp-input" ||
        input.Language is null && Path.GetExtension(input.Path).ToLowerInvariant() is ".f" or ".for" or ".f90" or ".f95" or ".f03" or ".f08";
    private static bool IsCpp(LinkInput input) => input.Language is "c++" or "c++-header" or "c++-cpp-output" or "objective-c++" ||
        input.Language is null && Path.GetExtension(input.Path) is ".cc" or ".cpp" or ".cxx" or ".C" or ".ii";
}
