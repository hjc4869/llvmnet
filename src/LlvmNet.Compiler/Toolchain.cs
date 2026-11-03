using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;

namespace LlvmNet;

internal static class Toolchain
{
    internal static string Root
    {
        get
        {
            string? configured = Environment.GetEnvironmentVariable("LLVMNET_ROOT");
            if (!string.IsNullOrEmpty(configured)) return Path.GetFullPath(configured);
            for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
                if (Directory.Exists(Path.Combine(directory.FullName, "sysroot")) || File.Exists(Path.Combine(directory.FullName, "llvmnet.slnx")))
                    return directory.FullName;
            return AppContext.BaseDirectory;
        }
    }

    internal static string Executable(string name, string? variable = null)
    {
        string? configured = variable is null ? null : Environment.GetEnvironmentVariable(variable);
        if (!string.IsNullOrEmpty(configured)) return configured;
        string bundled = Path.Combine(Root, "libexec", "llvm", "bin", name);
        return File.Exists(bundled) ? bundled : name;
    }

    internal static string[] ExpandResponseFiles(string[] arguments, string? directory = null, HashSet<string>? active = null)
    {
        directory ??= Environment.CurrentDirectory;
        active ??= new(StringComparer.Ordinal);
        List<string> expanded = [];
        foreach (string argument in arguments)
        {
            if (!argument.StartsWith('@')) { expanded.Add(argument); continue; }
            string path = Path.GetFullPath(argument[1..], directory);
            if (!active.Add(path)) throw new ArgumentException($"Recursive response file: {path}");
            if (active.Count > 32) throw new ArgumentException("Response file nesting exceeds 32 levels.");
            expanded.AddRange(ExpandResponseFiles(Tokenize(File.ReadAllText(path)), directory, active));
            active.Remove(path);
        }
        return expanded.ToArray();
    }

    internal static string[] Tokenize(string text)
    {
        List<string> result = [];
        var token = new StringBuilder();
        char quote = '\0';
        bool started = false;
        for (int index = 0; index < text.Length; index++)
        {
            char character = text[index];
            if (character == '\\' && index + 1 < text.Length)
            {
                token.Append(text[++index]);
                started = true;
            }
            else if (quote != '\0')
            {
                if (character == quote) quote = '\0';
                else token.Append(character);
            }
            else if (character is '\'' or '"') { quote = character; started = true; }
            else if (char.IsWhiteSpace(character))
            {
                if (started) { result.Add(token.ToString()); token.Clear(); started = false; }
            }
            else { token.Append(character); started = true; }
        }
        if (quote != '\0') throw new ArgumentException("Unclosed quote in response file.");
        if (started) result.Add(token.ToString());
        return result.ToArray();
    }

    internal static void Launcher(string output, string assembly)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("Use --nativeaot or a .dll output on this host; shell launchers require Unix.");
        string relative = Path.GetRelativePath(Path.GetDirectoryName(output)!, assembly);
        if (relative.Contains('\n')) throw new ArgumentException("Newlines are not supported in executable output paths.");
        string quoted = relative.Replace("'", "'\"'\"'", StringComparison.Ordinal);
        File.WriteAllText(output, "#!/bin/sh\nroot=$(CDPATH= cd -- \"$(dirname -- \"$0\")\" && pwd) || exit 1\nexec \"${LLVMNET_DOTNET:-dotnet}\" \"$root\"/'" + quoted + "' \"$@\"\n");
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            File.SetUnixFileMode(output, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }

    /// <summary>
    /// Writes the NativeAOT project for an image into <paramref name="projectDirectory"/>: NativeHost.csproj, a stub
    /// source, and copies of the image, its managed references and system-mode bridge archives, referenced relative to
    /// the project. The directory is relocatable: "dotnet publish NativeHost.csproj -c Release -r &lt;rid&gt;" builds it
    /// on any host with the .NET 10 SDK and that host's native toolchain, which is how portable images are published
    /// for Windows and macOS from a Linux build (docs/user/deployment.md). Returns the assembly name, which names the executable.
    /// </summary>
    internal static string AotProject(string assembly, string projectDirectory, string preference, string instructionSet, bool debug, IReadOnlyList<string> systemLibraries)
    {
        Directory.CreateDirectory(projectDirectory);
        string projectPath = Path.Combine(projectDirectory, "NativeHost.csproj");
        string assemblyName = AssemblyName.GetAssemblyName(assembly).Name!;
        string sourceDirectory = Path.GetDirectoryName(Path.GetFullPath(assembly))!;
        string image = Path.GetFileName(assembly);
        Copy(assembly);
        var references = new XElement("ItemGroup");
        foreach (string dependency in Directory.EnumerateFiles(sourceDirectory, "*.dll"))
        {
            if (Path.GetFullPath(dependency) == Path.GetFullPath(assembly)) continue;
            Copy(dependency);
            references.Add(new XElement("Reference", new XAttribute("Include", Path.GetFileNameWithoutExtension(dependency)),
                new XElement("HintPath", "$(MSBuildThisFileDirectory)" + Path.GetFileName(dependency))));
        }
        foreach (string archive in Directory.EnumerateFiles(sourceDirectory, "*.llvmnet-host.a"))
        {
            Copy(archive);
            references.Add(new XElement("NativeLibrary", new XAttribute("Include", "$(MSBuildThisFileDirectory)" + Path.GetFileName(archive))));
            references.Add(new XElement("DirectPInvoke", new XAttribute("Include", Path.GetFileNameWithoutExtension(archive) + ".so")));
        }
        foreach (string library in systemLibraries)
            references.Add(Path.IsPathRooted(library)
                ? new XElement("NativeLibrary", new XAttribute("Include", library))
                : new XElement("LinkerArg", new XAttribute("Include", "-l:" + library)));
        var project = new XDocument(new XElement("Project", new XAttribute("Sdk", "Microsoft.NET.Sdk"),
            new XElement("PropertyGroup",
                new XElement("TargetFramework", "net10.0"), new XElement("OutputType", "Exe"),
                new XElement("AssemblyName", assemblyName), new XElement("PublishAot", "true"),
                new XElement("AllowUnsafeBlocks", "true"), new XElement("StripSymbols", debug ? "false" : "true"),
                new XElement("DebugType", debug ? "portable" : "none"),
                new XElement("IlcGenerateMapFile", debug ? "true" : "false"),
                new XElement("IlcOptimizationPreference", preference),
                // No ICU on Linux and macOS, where the host needs none; Windows needs it to resolve IANA time-zone
                // names (TZ=Europe/London) to its own zones, so a win-* executable keeps it (PF-10).
                new XElement("InvariantGlobalization", new XAttribute("Condition", "!$(RuntimeIdentifier.StartsWith('win'))"), "true"),
                // x86-64-v3 is an x64 level; a relocatable project published for another architecture keeps its default.
                instructionSet == "x86-64-v3"
                    ? new XElement("IlcInstructionSet", new XAttribute("Condition", "$(RuntimeIdentifier.EndsWith('-x64'))"), "x86-64-v3")
                    : new XElement("IlcInstructionSet", instructionSet == "native" ? "native" : ""),
                new XElement("EnableDefaultCompileItems", "false"),
                // Cross-publishing from Linux x64 to linux-arm64 (XP-05) links with clang and the aarch64 cross sysroot
                // (Debian's libc6-dev-arm64-cross and libgcc-*-dev-arm64-cross) and strips with the cross objcopy.
                new XElement("ObjCopyName", new XAttribute("Condition", "'$(RuntimeIdentifier)' == 'linux-arm64' and '$(NETCoreSdkRuntimeIdentifier)' == 'linux-x64'"), "aarch64-linux-gnu-objcopy")),
            new XElement("ItemGroup", new XElement("Compile", new XAttribute("Include", "NativeHost.cs"))), references,
            new XElement("Target", new XAttribute("Name", "UseEmittedCil"), new XAttribute("AfterTargets", "CoreCompile"),
                new XElement("Copy", new XAttribute("SourceFiles", "$(MSBuildThisFileDirectory)" + image), new XAttribute("DestinationFiles", "@(IntermediateAssembly)")))));
        project.Save(projectPath);
        File.WriteAllText(Path.Combine(projectDirectory, "NativeHost.cs"), "internal static class NativeHost\n{\n    private static int Main(string[] args) => 0;\n}\n");
        // The project must build the same wherever it is placed: stop MSBuild from importing a Directory.Build.props or
        // .targets of an enclosing tree (a repository's TreatWarningsAsErrors would turn the trim warnings into errors).
        foreach (string name in new[] { "Directory.Build.props", "Directory.Build.targets" })
            File.WriteAllText(Path.Combine(projectDirectory, name), "<Project />\n");
        // An offline feed (scripts/nativeaot-feed.sh) replaces every configured NuGet source for the restore.
        if (NuGetFeed is string feed)
            new XDocument(new XElement("configuration", new XElement("packageSources", new XElement("clear"),
                new XElement("add", new XAttribute("key", "llvmnet"), new XAttribute("value", feed))))).Save(Path.Combine(projectDirectory, "nuget.config"));
        return assemblyName;

        void Copy(string file)
        {
            string destination = Path.Combine(projectDirectory, Path.GetFileName(file));
            if (Path.GetFullPath(file) != Path.GetFullPath(destination)) File.Copy(file, destination, true);
        }
    }

    /// <summary>LLVMNET_NUGET_FEED: a folder feed or URL that the NativeAOT project restores from instead of the configured sources.</summary>
    internal static string? NuGetFeed => Environment.GetEnvironmentVariable("LLVMNET_NUGET_FEED") is { Length: > 0 } feed ? feed : null;

    private static string Dotnet => Environment.GetEnvironmentVariable("LLVMNET_DOTNET") is { Length: > 0 } dotnet ? dotnet : "dotnet";

    /// <summary>
    /// Checks what "dotnet publish" with PublishAot needs before it runs, so a missing prerequisite is one line naming
    /// the fix instead of the SDK's own error from deep inside the publish (TC-12): the .NET 10 SDK, the ILCompiler
    /// package in an offline feed, and the target's native toolchain (docs/user/deployment.md).
    /// </summary>
    internal static void CheckNativeAotPrerequisites(string rid, bool verbose)
    {
        string host = RuntimeInformation.RuntimeIdentifier;
        if (!(OperatingSystem.IsLinux() && rid.StartsWith("linux-", StringComparison.Ordinal) || rid == host))
            throw new InvalidOperationException($"NativeAOT prerequisite missing: {rid} executables cannot be published from {host}. Write the project with --aot-project <dir> and run \"dotnet publish NativeHost.csproj -c Release -r {rid}\" on a {(rid.StartsWith("win-", StringComparison.Ordinal) ? "Windows" : rid.StartsWith("osx-", StringComparison.Ordinal) ? "macOS" : rid)} host.");
        string sdks;
        try
        {
            sdks = Capture(Dotnet, ["--list-sdks"], verbose);
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or FileNotFoundException or InvalidOperationException)
        {
            throw new InvalidOperationException($"NativeAOT prerequisite missing: the .NET 10 SDK. '{Dotnet}' cannot be run ({error.Message}); install the SDK (https://dotnet.microsoft.com/download/dotnet/10.0) or set LLVMNET_DOTNET to it.");
        }
        if (!sdks.Split('\n').Any(line => line.StartsWith("10.", StringComparison.Ordinal)))
            throw new InvalidOperationException($"NativeAOT prerequisite missing: the .NET 10 SDK. '{Dotnet} --list-sdks' reports {(sdks.Trim().Length == 0 ? "no SDK (only a runtime is installed)" : sdks.Trim().Replace('\n', ';'))}; install the .NET 10 SDK, not just the runtime.");
        if (NuGetFeed is string feed && Directory.Exists(feed))
        {
            foreach (string package in new[] { "microsoft.dotnet.ilcompiler", $"runtime.{rid}.microsoft.dotnet.ilcompiler", $"microsoft.netcore.app.runtime.{rid}" })
                if (!Directory.EnumerateFiles(feed, package + ".*.nupkg").Any())
                    throw new InvalidOperationException($"NativeAOT prerequisite missing: the offline feed {feed} has no {package} package; fill it with \"scripts/nativeaot-feed.sh {feed} {rid}\" on a host with access to NuGet.");
        }
        if (!OperatingSystem.IsLinux()) return;
        string? compiler = Environment.GetEnvironmentVariable("CppCompilerAndLinker") is { Length: > 0 } configured ? configured : new[] { "clang", "gcc" }.FirstOrDefault(name => OnPath(name));
        if (compiler is null)
            throw new InvalidOperationException($"NativeAOT prerequisite missing: a C compiler and linker for {rid}. Install clang (Debian and Ubuntu: apt-get install clang zlib1g-dev); the SDK runs 'clang' or 'gcc' from PATH, or the one named by CppCompilerAndLinker.");
        bool cross = rid != host;
        List<string> probe = ["-x", "c", "-", "-o", OperatingSystem.IsWindows() ? "NUL" : "/dev/null", "-lz"];
        if (cross)
        {
            if (rid != "linux-arm64") throw new InvalidOperationException($"NativeAOT prerequisite missing: cross-publishing to {rid} from {host} is not supported; linux-arm64 is the only cross target (docs/user/deployment.md).");
            probe.InsertRange(0, ["--target=aarch64-linux-gnu"]);
            if (!OnPath("aarch64-linux-gnu-objcopy"))
                throw new InvalidOperationException("NativeAOT prerequisite missing: aarch64-linux-gnu-objcopy, which strips the cross-published executable (Debian: apt-get install binutils-aarch64-linux-gnu).");
        }
        string linkerOutput = Capture(compiler, probe, verbose, "int main(void) { return 0; }\n", out int linkerStatus);
        if (linkerStatus != 0)
        {
            string hint = linkerOutput.Contains("-lz", StringComparison.Ordinal) ? "zlib's development files are missing (Debian: apt-get install zlib1g-dev)"
                : cross ? "the aarch64 cross sysroot is missing (Debian: apt-get install libc6-dev-arm64-cross libgcc-<version>-dev-arm64-cross)" : $"'{compiler}' cannot link a program";
            throw new InvalidOperationException($"NativeAOT prerequisite missing: {hint}. The linker probe said: {linkerOutput.Trim().Split('\n').LastOrDefault(line => line.Contains("error", StringComparison.OrdinalIgnoreCase)) ?? linkerOutput.Trim()}");
        }
    }

    private static bool OnPath(string name) => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
        .Any(directory => directory.Length > 0 && File.Exists(Path.Combine(directory, name)));

    private static string Capture(string executable, IEnumerable<string> arguments, bool verbose) => Capture(executable, arguments, verbose, null, out int status) is string text && status == 0 ? text
        : throw new InvalidOperationException($"'{executable} {string.Join(" ", arguments)}' failed");

    private static string Capture(string executable, IEnumerable<string> arguments, bool verbose, string? input, out int status)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = input is not null };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        if (verbose) Console.Error.WriteLine(executable + " " + string.Join(" ", start.ArgumentList.Select(argument => System.Text.Json.JsonSerializer.Serialize(argument))));
        using Process process = Process.Start(start) ?? throw new InvalidOperationException($"Cannot start {executable}");
        if (input is not null) { process.StandardInput.Write(input); process.StandardInput.Close(); }
        string error = process.StandardError.ReadToEnd();
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        status = process.ExitCode;
        return output + error;
    }

    internal static int NativeAot(string assembly, string output, string temporary, string? rid, string preference, string instructionSet, bool verbose, bool debug, IReadOnlyList<string> systemLibraries)
    {
        if (instructionSet == "native" && rid is not null && rid != RuntimeInformation.RuntimeIdentifier)
            throw new ArgumentException("Host CPU instruction selection requires the current host runtime identifier.");
        if (instructionSet == "x86-64-v3" && !(rid ?? RuntimeInformation.RuntimeIdentifier).EndsWith("-x64", StringComparison.Ordinal))
            throw new ArgumentException($"--aot-instruction-set x86-64-v3 is an x64 instruction set; the target is {rid ?? RuntimeInformation.RuntimeIdentifier}.");
        CheckNativeAotPrerequisites(rid ?? RuntimeInformation.RuntimeIdentifier, verbose);
        string projectDirectory = Path.Combine(temporary, "aot");
        string projectPath = Path.Combine(projectDirectory, "NativeHost.csproj");
        string assemblyName = AotProject(assembly, projectDirectory, preference, instructionSet, debug, systemLibraries);
        string publish = Path.Combine(projectDirectory, "publish");
        List<string> arguments = ["publish", projectPath, "-c", "Release", "-r", rid ?? RuntimeInformation.RuntimeIdentifier, "-o", publish, "--nologo", "-v", verbose ? "normal" : "minimal"];
        int result = Run(Dotnet, arguments, verbose);
        if (result != 0) return result;
        string native = Path.Combine(publish, assemblyName + ((rid ?? RuntimeInformation.RuntimeIdentifier).StartsWith("win-", StringComparison.Ordinal) ? ".exe" : ""));
        if (!File.Exists(native)) throw new FileNotFoundException("NativeAOT did not produce the requested executable.", native);
        // As for a JIT image, -o may name a directory that does not exist yet.
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.Copy(native, output, true);
        foreach (string sidecar in Directory.EnumerateFiles(Path.GetDirectoryName(assembly)!, "*.so"))
            File.Copy(sidecar, Path.Combine(Path.GetDirectoryName(output)!, Path.GetFileName(sidecar)), true);
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            File.SetUnixFileMode(output, File.GetUnixFileMode(native));
        return 0;
    }

    /// <summary>With -###, every command is printed instead of run.</summary>
    internal static bool DryRun { get; set; }

    internal static int Run(string executable, IEnumerable<string> arguments, bool verbose = false, bool isolatedHeaders = false)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        if (isolatedHeaders)
            foreach (string name in new[] { "CPATH", "C_INCLUDE_PATH", "CPLUS_INCLUDE_PATH", "OBJC_INCLUDE_PATH", "SDKROOT" })
                start.Environment.Remove(name);
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        if (verbose || DryRun) Console.Error.WriteLine(executable + " " + string.Join(" ", start.ArgumentList.Select(argument => System.Text.Json.JsonSerializer.Serialize(argument))));
        if (DryRun) return 0;
        using Process process = Process.Start(start) ?? throw new InvalidOperationException($"Cannot start {executable}");
        process.WaitForExit();
        return process.ExitCode;
    }
}
