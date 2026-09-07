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

    internal static int NativeAot(string assembly, string output, string temporary, string? rid, string preference, string instructionSet, bool verbose, bool debug, IReadOnlyList<string> systemLibraries)
    {
        if (instructionSet == "native" && rid is not null && rid != RuntimeInformation.RuntimeIdentifier)
            throw new ArgumentException("Host CPU instruction selection requires the current host runtime identifier.");
        string projectDirectory = Path.Combine(temporary, "aot");
        Directory.CreateDirectory(projectDirectory);
        string projectPath = Path.Combine(projectDirectory, "NativeHost.csproj");
        string assemblyName = AssemblyName.GetAssemblyName(assembly).Name!;
        var references = new XElement("ItemGroup");
        foreach (string dependency in Directory.EnumerateFiles(Path.GetDirectoryName(assembly)!, "*.dll"))
        {
            if (Path.GetFullPath(dependency) == Path.GetFullPath(assembly)) continue;
            references.Add(new XElement("Reference", new XAttribute("Include", Path.GetFileNameWithoutExtension(dependency)), new XElement("HintPath", dependency)));
        }
        foreach (string archive in Directory.EnumerateFiles(Path.GetDirectoryName(assembly)!, "*.llvmnet-host.a"))
        {
            references.Add(new XElement("NativeLibrary", new XAttribute("Include", archive)));
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
                new XElement("IlcOptimizationPreference", preference), new XElement("InvariantGlobalization", "true"),
                new XElement("IlcInstructionSet", instructionSet == "native" ? "native" : ""),
                new XElement("EnableDefaultCompileItems", "false")),
            new XElement("ItemGroup", new XElement("Compile", new XAttribute("Include", "NativeHost.cs"))), references,
            new XElement("Target", new XAttribute("Name", "UseEmittedCil"), new XAttribute("AfterTargets", "CoreCompile"),
                new XElement("Copy", new XAttribute("SourceFiles", assembly), new XAttribute("DestinationFiles", "@(IntermediateAssembly)")))));
        project.Save(projectPath);
        File.WriteAllText(Path.Combine(projectDirectory, "NativeHost.cs"), "internal static class NativeHost\n{\n    private static int Main(string[] args) => 0;\n}\n");
        string publish = Path.Combine(projectDirectory, "publish");
        List<string> arguments = ["publish", projectPath, "-c", "Release", "-r", rid ?? RuntimeInformation.RuntimeIdentifier, "-o", publish, "--nologo", "-v", verbose ? "normal" : "minimal"];
        int result = Run(Environment.GetEnvironmentVariable("LLVMNET_DOTNET") ?? "dotnet", arguments, verbose);
        if (result != 0) return result;
        string native = Path.Combine(publish, assemblyName + ((rid ?? RuntimeInformation.RuntimeIdentifier).StartsWith("win-", StringComparison.Ordinal) ? ".exe" : ""));
        if (!File.Exists(native)) throw new FileNotFoundException("NativeAOT did not produce the requested executable.", native);
        File.Copy(native, output, true);
        foreach (string sidecar in Directory.EnumerateFiles(Path.GetDirectoryName(assembly)!, "*.so"))
            File.Copy(sidecar, Path.Combine(Path.GetDirectoryName(output)!, Path.GetFileName(sidecar)), true);
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            File.SetUnixFileMode(output, File.GetUnixFileMode(native));
        return 0;
    }

    internal static int Run(string executable, IEnumerable<string> arguments, bool verbose = false, bool isolatedHeaders = false)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        if (isolatedHeaders)
            foreach (string name in new[] { "CPATH", "C_INCLUDE_PATH", "CPLUS_INCLUDE_PATH", "OBJC_INCLUDE_PATH", "SDKROOT" })
                start.Environment.Remove(name);
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        if (verbose) Console.Error.WriteLine(executable + " " + string.Join(" ", start.ArgumentList.Select(argument => System.Text.Json.JsonSerializer.Serialize(argument))));
        using Process process = Process.Start(start) ?? throw new InvalidOperationException($"Cannot start {executable}");
        process.WaitForExit();
        return process.ExitCode;
    }
}