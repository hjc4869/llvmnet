using LlvmNet.Runtime;

namespace LlvmNet;

internal sealed record NativeImport(string Library, string EntryPoint);

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
    internal string AbiTag => Runtime == "system" ? "system-linux-x64-v1" : PortableAbi.Tag;
    internal List<string> SystemLibraries { get; } = [];
    internal Dictionary<string, NativeImport> NativeImports { get; } = new(StringComparer.Ordinal);
    internal List<string> CilReferences { get; } = [];
}

internal static class Driver
{
    internal static int Run(string[] arguments)
    {
        arguments = Toolchain.ExpandResponseFiles(arguments);
        string driver = Environment.GetEnvironmentVariable("LLVMNET_DRIVER") ?? "clang";
        if (arguments.Length == 1 && (arguments[0] is "--version" or "-v" or "-dumpmachine" or "-dumpversion" or "-print-search-dirs" or "-print-resource-dir" || arguments[0].StartsWith("-print-file-name=", StringComparison.Ordinal)))
        {
            string executable = driver == "flang" ? Toolchain.Executable("flang-22", "LLVMNET_FLANG") : Toolchain.Executable(driver == "clang++" ? "clang++-22" : "clang-22", "LLVMNET_CLANG");
            if (arguments[0] == "--version")
            {
                Console.WriteLine($"llvmnet {BuildInfo.Version} (LLVM {Llvm.Version}; ABIs system-linux-x64-v1, {PortableAbi.Tag})");
                Console.WriteLine("Frontend follows:");
            }
            return Toolchain.Run(executable, arguments);
        }
        if (arguments.Length == 0 || arguments.Contains("--help"))
        {
            Console.WriteLine(string.Join(Environment.NewLine, new[]
            {
                "llvmnet: Clang/LLVM 22 to unsafe .NET 10 CIL",
                "Usage: llvmnet [frontend options] inputs... -o output",
                "  -c                       Compile to LLVM bitcode; do not link",
                "  -S -emit-llvm            Emit textual LLVM IR",
                "  -E                       Preprocess with the selected frontend",
                "  -M / -MM / -MD / -MMD    Clang dependency-file options",
                "  -L<dir> -l<name>         Link LLVM bitcode archives (-lc, -lm, -lpthread, -ldl, -lrt, -lc++ are built in)",
                "  -nostdlib, -nodefaultlibs, -nostartfiles  Portable: omit the sysroot libraries and/or start object",
                "  -r                       Link to a relocatable bitcode object",
                "  -shared / --emit-library Emit a CIL library without an entry point",
                "  --cil-reference <dll>    Resolve ABI-compatible managed CExport methods",
                "  --native-import <spec>   Explicit scalar P/Invoke: symbol=library!entrypoint",
                "  --system-library <file>  Additional native library in system ABI mode",
                "  --runtime <mode>         portable (default) or system",
                "  --simd128                Enable architecture-neutral SIMD128 helper calls",
                "  --trap-missing-arguments Trap invalid void calls with missing arguments",
                "  --diagnose-limits        Report per-function IL size, locals and JIT optimization thresholds",
                "  --check-indirect-calls   Debug: verify each indirect call's CIL signature against its target",
                "  --portable-sysroot <dir> Use another portable sysroot (default: the toolchain's sysroot/" + PortableAbi.Tag + ")",
                "  --emit-llvm              Stop after LLVM linking",
                "  --keep-ir <file>         Preserve linked LLVM bitcode",
                "  --nativeaot              Publish a self-contained NativeAOT executable",
                "  --runtime-id <rid>       NativeAOT target RID (default: current host)",
                "  --aot-optimize <choice>  Balanced, Speed or Size",
                "  --aot-instruction-set <choice> baseline (default) or native (build host CPU)",
                "  --aot-debug              Retain NativeAOT native debug symbols",
                "  @response-file           Read quoted compiler arguments from a file",
                "A .dll output is managed CIL; other executable outputs are Unix launchers unless --nativeaot is selected.",
                "LLVMNET_CLANG, LLVMNET_FLANG and LLVMNET_OPT override bundled/PATH tool selection."
            }));
            return arguments.Length == 0 ? 2 : 0;
        }
        var options = new CompilerOptions();
        List<string> frontend = [];
        List<string> inputs = [];
        List<string> libraries = [];
        List<string> searchPaths = [];
        string? output = null;
        string? keepIr = null;
        bool compileOnly = false;
        bool textual = false;
        bool preprocess = false;
        bool emitLlvm = false;
        bool dependencyOnly = false;
        bool syntaxOnly = false;
        bool relocatable = false;
        bool nativeAot = false;
        bool aotDebug = false;
        bool verbose = false;
        bool linkerVersion = false;
        bool startFiles = true;
        bool staticLink = false;
        bool defaultLibraries = true;
        string? runtimeId = null;
        string? aotInstructionSet = null;
        string aotPreference = "Balanced";
        for (int index = 0; index < arguments.Length; index++)
        {
            string argument = arguments[index];
            string Next() => ++index < arguments.Length ? arguments[index] : throw new ArgumentException($"Missing value for {argument}");
            switch (argument)
            {
                case "-o": output = Next(); break;
                case "-v": verbose = true; break;
                case "-Wl,--version": linkerVersion = true; break;
                case "-r": relocatable = true; emitLlvm = true; break;
                case "-M": case "-MM": dependencyOnly = true; frontend.Add(argument); break;
                case "-fsyntax-only": syntaxOnly = true; frontend.Add(argument); break;
                case "-pipe": case "-fuse-ld=lld": case "-flto": case "-flto=full": case "-flto=thin": break;
                case "--runtime": options.Runtime = Next(); break;
                case "--system-library": options.SystemLibraries.Add(Next()); break;
                case "--nativeaot": nativeAot = true; break;
                case "--simd128": options.Simd128 = true; break;
                case "--trap-missing-arguments": options.TrapMissingArguments = true; break;
                case "--diagnose-limits": options.DiagnoseLimits = true; break;
                case "--portable-sysroot": PortableAbi.SysrootOverride = Path.GetFullPath(Next()); break;
                case "-nostdlib": startFiles = false; defaultLibraries = false; break;
                case "-nostartfiles": startFiles = false; break;
                case "-nodefaultlibs": defaultLibraries = false; break;
                case "--check-indirect-calls": options.CheckIndirectCalls = true; break;
                case "--aot-debug": aotDebug = true; break;
                case "--runtime-id": runtimeId = Next(); break;
                case "--aot-instruction-set":
                    aotInstructionSet = Next();
                    if (aotInstructionSet is not ("baseline" or "native")) throw new ArgumentException("--aot-instruction-set requires baseline or native.");
                    break;
                case "--aot-optimize":
                    aotPreference = Next();
                    if (aotPreference is not ("Balanced" or "Speed" or "Size")) throw new ArgumentException("--aot-optimize requires Balanced, Speed or Size.");
                    break;
                case "-c": compileOnly = true; break;
                case "-S": textual = true; compileOnly = true; break;
                case "-E": preprocess = true; break;
                case "-emit-llvm": case "--emit-llvm": emitLlvm = true; break;
                case "--emit-library": case "-shared": options.Library = true; break;
                case "--cil-reference": options.CilReferences.Add(Path.GetFullPath(Next())); break;
                case "--keep-ir": keepIr = Path.GetFullPath(Next()); break;
                case "--native-import":
                    string specification = Next();
                    int assignment = specification.IndexOf('=');
                    int separator = specification.LastIndexOf('!');
                    if (assignment < 1 || separator <= assignment + 1 || separator == specification.Length - 1)
                        throw new ArgumentException("Native import syntax: symbol=library!entrypoint");
                    options.NativeImports.Add(specification[..assignment], new NativeImport(specification[(assignment + 1)..separator], specification[(separator + 1)..]));
                    break;
                case "-L": searchPaths.Add(Next()); break;
                case "-l": libraries.Add(Next()); break;
                case "-I":
                case "-isystem":
                case "-idirafter":
                case "-iquote":
                case "-include":
                case "-imacros":
                case "-D":
                case "-U":
                case "-x":
                case "-MF":
                case "-MT":
                case "-MQ":
                case "--sysroot":
                case "-isysroot":
                case "-target":
                case "-Xclang":
                case "-J":
                case "-module-dir":
                    frontend.Add(argument);
                    frontend.Add(Next());
                    break;
                default:
                    if (argument.StartsWith("--runtime=", StringComparison.Ordinal)) options.Runtime = argument[10..];
                    else if (argument.StartsWith("-o", StringComparison.Ordinal) && argument.Length > 2) output = argument[2..];
                    else if (argument == "-Wl,--start-group" || argument == "-Wl,--end-group") { }
                    else if (argument.StartsWith("-L", StringComparison.Ordinal))
                        searchPaths.Add(argument[2..]);
                    else if (argument.StartsWith("-l", StringComparison.Ordinal))
                        libraries.Add(argument[2..]);
                    else if (argument == "-static") staticLink = true;
                    else if (argument.StartsWith("-Wl,", StringComparison.Ordinal) || argument is "-rdynamic")
                        throw new NotSupportedException($"Native linker option {argument} is not a CIL linker option; use --emit-library, --cil-reference or --native-import.");
                    else if (argument.StartsWith('-'))
                        frontend.Add(argument);
                    else
                        inputs.Add(argument);
                    break;
            }
        }
        if (nativeAot && (compileOnly || preprocess || dependencyOnly || syntaxOnly || emitLlvm || options.Library))
            throw new ArgumentException("--nativeaot requires an executable link, not -c, -E, -S, --emit-llvm or -shared.");
        if (!nativeAot && (runtimeId is not null || aotDebug || aotPreference != "Balanced" || aotInstructionSet is not null))
            throw new ArgumentException("NativeAOT output settings require --nativeaot.");
        if (options.Runtime == "managed-host")
            throw new ArgumentException("--runtime=managed-host was removed (ADR-04); use --runtime=portable for a platform-neutral .NET ABI or --runtime=system for the native Linux ABI.");
        if (options.Runtime is not ("system" or "portable")) throw new ArgumentException("--runtime requires portable or system.");
        // A portable program is always one statically linked image; system mode links the native C library dynamically.
        if (staticLink && options.Runtime == "system")
            throw new NotSupportedException("Native linker option -static is not a CIL linker option in system mode; system programs use the host's shared C library.");
        if (options.Runtime == "portable" && options.NativeImports.Count != 0)
            throw new ArgumentException("Portable ABI mode cannot contain native imports; use a managed CIL reference or --runtime=system.");
        if (options.Runtime != "system" && options.SystemLibraries.Count != 0) throw new ArgumentException("--system-library requires --runtime=system.");
        if (PortableAbi.SysrootOverride is not null && options.Runtime != "portable") throw new ArgumentException("--portable-sysroot requires --runtime=portable.");
        if (options.Runtime == "system" && runtimeId is not null && runtimeId != "linux-x64")
            throw new ArgumentException("The current system ABI profile requires NativeAOT RID linux-x64.");
        if (linkerVersion)
        {
            if (inputs.Count != 0 || output is not null || compileOnly || preprocess || dependencyOnly || emitLlvm || nativeAot || options.Library)
                throw new ArgumentException("-Wl,--version is an informational query, not a link operation.");
            Console.WriteLine("LLD-compatible llvmnet linker interface (LLVM 22.1.8; emits CIL, not native images)");
            return 0;
        }
        options.Verbose = verbose;
        options.NativeAot = nativeAot;
        if (options.Simd128)
            frontend.InsertRange(0, ["-D__LLVMNET_SIMD128__=1", "-I", Path.Combine(Toolchain.Root, "sysroot", "simd128", "include")]);
        string clang = Toolchain.Executable("clang-22", "LLVMNET_CLANG");
        // Preprocessing, dependency generation and -fsyntax-only run the frontend alone, with the mode's headers.
        if (preprocess || dependencyOnly || syntaxOnly)
        {
            List<string> preprocessing = [.. frontend];
            if (preprocess) preprocessing.Add("-E");
            preprocessing.AddRange(inputs);
            if (output is not null)
                preprocessing.AddRange(["-o", output]);
            bool fortranInput = driver == "flang" || inputs.Any(input => IsFortran(input));
            if (options.Runtime == "portable") preprocessing.InsertRange(0, PortableAbi.Arguments(frontend, fortranInput, driver == "clang++" || inputs.Any(IsCpp)));
            if (driver == "clang++") preprocessing.Insert(0, "--driver-mode=g++");
            return Toolchain.Run(fortranInput ? Toolchain.Executable("flang-22", "LLVMNET_FLANG") : clang, preprocessing, verbose, options.Runtime == "portable");
        }
        if (inputs.Count == 0)
            throw new ArgumentException("No input files.");
        if (compileOnly && inputs.Count != 1 && output is not null)
            throw new ArgumentException("Cannot use one -o with multiple inputs and -c.");

        string temporary = Path.Combine(Path.GetTempPath(), $"llvmnet-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporary);
        try
        {
            List<string> bitcode = [];
            foreach (string input in inputs)
            {
                string extension = Path.GetExtension(input);
                // With -c, LLVM IR is compiled like a source, which records the ABI tag that link inputs need.
                if (extension is ".o" or ".a" || extension is ".bc" or ".ll" && !compileOnly)
                {
                    if (compileOnly)
                        throw new NotSupportedException("-c requires a source or LLVM IR input, not an object or archive.");
                    bitcode.Add(input);
                    continue;
                }
                bool fortran = driver == "flang" || IsFortran(input);
                bool cpp = driver == "clang++" || IsCpp(input);
                if (!fortran && !frontend.Contains("-x") && extension is not (".c" or ".cc" or ".cpp" or ".cxx" or ".C" or ".i" or ".ii" or ".ll" or ".bc"))
                    throw new NotSupportedException($"Unsupported input kind: {input}");
                string destination = compileOnly ? output ?? Path.GetFileNameWithoutExtension(input) + (textual ? ".ll" : ".o") : Path.Combine(temporary, $"input{bitcode.Count}.bc");
                List<string> command = ["-O1", "-fno-vectorize", "-fno-slp-vectorize"];
                if (driver == "clang++") command.Add("--driver-mode=g++");
                if (options.Runtime == "portable") command.AddRange(PortableAbi.Arguments(frontend, fortran, cpp));
                if (fortran)
                {
                    if (!frontend.Any(argument => argument.StartsWith("-J", StringComparison.Ordinal) || argument == "-module-dir"))
                        command.AddRange(["-module-dir", compileOnly ? Path.GetDirectoryName(Path.GetFullPath(destination))! : temporary]);
                }
                else
                    command.Add("-fno-stack-protector");
                // Mixed-language builds pass Fortran module options to every input; Clang does not accept them.
                command.AddRange([.. fortran ? frontend : WithoutFortranOptions(frontend), "-emit-llvm", textual ? "-S" : "-c", input, "-o", destination]);
                int result = Toolchain.Run(fortran ? Toolchain.Executable("flang-22", "LLVMNET_FLANG") : clang, command, verbose, options.Runtime == "portable");
                if (result != 0)
                    return result;
                Llvm.StampAbi(destination, options.AbiTag, textual);
                bitcode.Add(destination);
            }
            if (compileOnly)
                return 0;
            foreach (string library in libraries)
            {
                // Parts of the C library in both modes; in portable mode the sysroot also supplies the C++ and Fortran runtimes.
                if (library is "c" or "m" or "pthread" or "dl" or "rt" or "util" or "crypt" or "resolv" ||
                    options.Runtime == "portable" && library is "c++" or "c++abi" or "stdc++" or "supc++" or "flang_rt.runtime" or "FortranRuntime" or "FortranDecimal")
                    continue;
                string? archive = searchPaths.Select(directory => Path.Combine(directory, $"lib{library}.a")).FirstOrDefault(File.Exists);
                if (archive is null)
                    throw new FileNotFoundException($"Cannot find LLVM bitcode archive lib{library}.a in the -L directories. Native libraries require --native-import.");
                bitcode.Add(archive);
            }
            // Portable executables link musl's start object and the sysroot archives, in dependency order. Libraries do
            // not: musl keeps per-process state (thread pointer, heap, stdio) that one image per process must own.
            if (options.Runtime == "portable" && !options.Library)
            {
                PortableAbi.RequireSysroot();
                if (startFiles) bitcode.Add(PortableAbi.Library("crt1.o"));
                if (defaultLibraries)
                    foreach (string archive in new[] { "libflang_rt.runtime.a", "libc++.a", "libc++abi.a", "libc.a", "libclang_rt.builtins.a" })
                        if (File.Exists(PortableAbi.Library(archive))) bitcode.Add(PortableAbi.Library(archive));
            }
            // System executables take the Fortran runtime from flang-rt built against glibc; the C library is native.
            string systemFortran = Path.Combine(Toolchain.Root, "sysroot", "system-linux-x64-v1", "lib", "libflang_rt.runtime.a");
            if (options.Runtime == "system" && !options.Library && defaultLibraries && File.Exists(systemFortran))
                bitcode.Add(systemFortran);
            string linked = Path.Combine(temporary, "linked.bc");
            if (bitcode.All(path => Path.GetExtension(path) == ".a"))
                throw new ArgumentException("Provide at least one explicit object or source as the link root.");
            Llvm.Link(bitcode.ToArray(), linked, options.AbiTag);
            if (keepIr is not null)
                File.Copy(linked, keepIr, true);
            output ??= options.Library ? "a.dll" : relocatable ? "a.o" : emitLlvm ? "a.bc" : "a.out";
            output = Path.GetFullPath(output);
            if (emitLlvm)
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
            if (!options.Library) optimization.Add("--internalize-public-api-list=main,_QQmain" + (options.Runtime == "portable" ? ",__llvmnet_libc_main,__llvmnet_register_finalizer,__llvmnet_cxx_can_catch" : ""));
            int optimizeResult = Toolchain.Run(Toolchain.Executable("opt-22", "LLVMNET_OPT"), optimization, verbose);
            if (optimizeResult != 0)
                return optimizeResult;
            string linkedInput = linked;
            linked = lowered;
            using var module = new LlvmModule(linked);
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
            if (nativeAot) return Toolchain.NativeAot(payload, output, temporary, runtimeId, aotPreference, aotInstructionSet ?? "baseline", verbose, aotDebug, options.SystemLibraries);
            if (launcher) Toolchain.Launcher(output, payload);
            return 0;
        }
        finally
        {
            Directory.Delete(temporary, true);
        }
    }

    private static List<string> WithoutFortranOptions(List<string> arguments)
    {
        List<string> result = [];
        for (int index = 0; index < arguments.Count; index++)
        {
            if (arguments[index] is "-module-dir" or "-J") { index++; continue; }
            if (arguments[index].StartsWith("-J", StringComparison.Ordinal) || arguments[index].StartsWith("-fintrinsic-modules-path", StringComparison.Ordinal)) continue;
            result.Add(arguments[index]);
        }
        return result;
    }

    private static bool IsFortran(string path) => Path.GetExtension(path).ToLowerInvariant() is ".f" or ".for" or ".f90" or ".f95" or ".f03" or ".f08";
    private static bool IsCpp(string path) => Path.GetExtension(path) is ".cc" or ".cpp" or ".cxx" or ".C" or ".ii";
}
