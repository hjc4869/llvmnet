using LlvmNet.Runtime;

namespace LlvmNet;

internal sealed class CompilerOptions
{
    internal bool Library { get; set; }
    internal string Runtime { get; set; } = "portable";
    internal bool Verbose { get; set; }
    internal bool NativeAot { get; set; }
    internal bool Simd128 { get; set; }
    internal bool TrapMissingArguments { get; set; }
    internal bool DiagnoseLimits { get; set; }
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
    internal bool ListExtensions { get; private set; }
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
                case "--simd128": Options.Simd128 = true; break;
                case "--cil-reference": Options.CilReferences.Add(Path.GetFullPath(value)); break;
                case "--system-library": Options.SystemLibraries.Add(value); break;
                case "--extension": ExtensionNames.Add(value); break;
                case "--list-extensions": ListExtensions = true; break;
                case "--nativeaot": Options.NativeAot = true; break;
                case "--runtime-id": RuntimeId = value; break;
                case "--aot-project": AotProject = Path.GetFullPath(value); Options.NativeAot = true; break;
                case "--aot-optimize":
                    AotPreference = value;
                    if (AotPreference is not ("Balanced" or "Speed" or "Size")) throw new ArgumentException("--aot-optimize requires Balanced, Speed or Size.");
                    break;
                case "--aot-instruction-set":
                    AotInstructionSet = value;
                    if (AotInstructionSet is not ("baseline" or "x86-64-v3" or "native")) throw new ArgumentException("--aot-instruction-set requires baseline, x86-64-v3 or native.");
                    break;
                case "--aot-debug": AotDebug = true; break;
                case "--keep-ir": KeepIr = Path.GetFullPath(value); break;
                case "--trap-missing-arguments": Options.TrapMissingArguments = true; break;
                case "--diagnose-limits": Options.DiagnoseLimits = true; break;
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
        if (AotProject is not null && RuntimeId is not null)
            throw new ArgumentException("--aot-project takes no --runtime-id: the runtime identifier is chosen when the project is published (dotnet publish -r <rid>).");
        if (Options.Runtime != "system" && Options.SystemLibraries.Count != 0) throw new ArgumentException("--system-library requires --runtime=system.");
        if (PortableAbi.SysrootOverride is not null && Options.Runtime != "portable") throw new ArgumentException("--portable-sysroot requires --runtime=portable.");
        if (Options.Runtime == "system" && RuntimeId is not null && RuntimeId != "linux-x64")
            throw new ArgumentException("The current system ABI profile requires NativeAOT RID linux-x64.");
        // An extension that is not installed fails before anything runs, whether or not this invocation links.
        foreach (string name in ExtensionNames) Extensions.Find(name);
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
        var invocation = new Invocation(parsed, driver);
        driver = invocation.Driver;
        if (invocation.ListExtensions) { Console.Write(Extensions.Listing()); return 0; }
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
            if (options.Runtime == "portable") preprocessing.InsertRange(0, PortableAbi.Arguments(frontend, fortranInput, driver == "clang++" || inputs.Any(IsCpp)));
            else if (!fortranInput) preprocessing.InsertRange(0, ["-idirafter", Extensions.InterfaceInclude]);
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
            foreach (LinkInput input in inputs)
            {
                string extension = Path.GetExtension(input.Path);
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
                List<string> command = ["-O1"];
                if (!frontend.Contains("-fvectorize") && !frontend.Contains("-fslp-vectorize")) command.AddRange(["-fno-vectorize", "-fno-slp-vectorize"]);
                if (driver == "clang++") command.Add("--driver-mode=g++");
                // LLVM IR gets the mode's target and macros only; its headers and feature macros do not apply.
                if (options.Runtime == "portable") command.AddRange(ir ? PortableAbi.Arguments(frontend, true, false) : PortableAbi.Arguments(frontend, fortran, cpp));
                // <llvmnet/ext.h> after the host's headers, so that it shadows nothing.
                else if (!fortran && !ir) command.AddRange(["-idirafter", Extensions.InterfaceInclude]);
                if (fortran)
                {
                    if (!invocation.ModuleDirectoryGiven)
                        command.AddRange(["-module-dir", invocation.CompileOnly ? Path.GetDirectoryName(Path.GetFullPath(destination))! : temporary]);
                }
                else
                    command.Add("-fno-stack-protector");
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
                try { Llvm.StampAbi(destination, options.AbiTag, invocation.Textual); }
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
            // System executables take the Fortran runtime from flang-rt built against glibc; the C library is native.
            string systemFortran = Path.Combine(Toolchain.Root, "sysroot", "system-linux-x64-v1", "lib", "libflang_rt.runtime.a");
            if (options.Runtime == "system" && !options.Library && !invocation.Relocatable && invocation.DefaultLibraries && File.Exists(systemFortran))
                bitcode.Add((systemFortran, false));
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
            Llvm.Link([.. bitcode.Select(input => input.Path)], [.. bitcode.Select(input => input.WholeArchive)], linked, options.AbiTag,
                executable ? ["main", "_QQmain"] : [], threadAttachment);
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
                Llvm.RemoveTargetAttributes(prepared.Handle);
                Llvm.LowerConstrainedFloatingPoint(prepared.Handle);
                if (Llvm.LLVMWriteBitcodeToFile(prepared.Handle, linked) != 0)
                    throw new IOException("Cannot write the legalized module.");
            }
            string scalarizer = options.Simd128 ? "scalarizer" : "scalarizer<load-store>";
            string passes = (options.Library ? "" : "internalize,globaldce,") + $"function(lower-constant-intrinsics,expand-reductions,scalarize-masked-mem-intrin,{scalarizer}),globaldce,verify";
            List<string> optimization = [$"-passes={passes}", linked, "-o", lowered];
            if (!options.Library) optimization.Add("--internalize-public-api-list=main,_QQmain" + (options.Runtime == "portable" ? ",__llvmnet_libc_main,__llvmnet_register_finalizer,__llvmnet_cxx_can_catch" : "") +
                (options.Extensions.Count != 0 && options.Runtime == "portable" ? ",__llvmnet_attach_thread,__llvmnet_detach_thread" : ""));
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
            try
            {
                new CilCompiler(options).Compile(module.Handle, payload);
            }
            catch (CompilationError error)
            {
                // Keep the linked IR so the failure can be reproduced with "llvmnet <file> -o ...".
                string kept = output + ".failed.bc";
                File.Copy(linkedInput, kept, true);
                error.KeptIr = kept;
                throw;
            }
            if (invocation.AotProject is not null)
            {
                Toolchain.AotProject(payload, invocation.AotProject, invocation.AotPreference, invocation.AotInstructionSet ?? "baseline", invocation.AotDebug, options.SystemLibraries);
                return 0;
            }
            if (nativeAot) return Toolchain.NativeAot(payload, output, temporary, invocation.RuntimeId, invocation.AotPreference, invocation.AotInstructionSet ?? "baseline", verbose, invocation.AotDebug, options.SystemLibraries);
            if (launcher) Toolchain.Launcher(output, payload);
            return 0;
        }
        finally
        {
            Directory.Delete(temporary, true);
        }
    }

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
