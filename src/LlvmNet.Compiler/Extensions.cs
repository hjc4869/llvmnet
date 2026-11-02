using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LlvmNet;

/// <summary>
/// A native extension package (ADR-15, docs/plan/11-native-extensions.md): the directory ext/&lt;name&gt;/ of the portable
/// sysroot, with manifest.json, neutral headers under include/ and the extension assembly lib/LlvmNet.Ext.&lt;Name&gt;.dll.
/// The manifest names the extension and its version, the host libraries it stands for on each operating system (one name
/// per library; system mode links the Linux ones), the -l names it answers and its headers.
/// </summary>
internal sealed record ExtensionPackage(string Name, string Version, string Directory, IReadOnlyList<string> Linux,
    IReadOnlyList<string> Windows, IReadOnlyList<string> MacOS, IReadOnlyList<string> Link, IReadOnlyList<string> Headers)
{
    internal string Include => Path.Combine(Directory, "include");

    /// <summary>lib/LlvmNet.Ext.&lt;Name&gt;.dll, the name in Pascal case: gles is LlvmNet.Ext.Gles, open-xr LlvmNet.Ext.OpenXr.</summary>
    internal string Assembly => Path.Combine(Directory, "lib", "LlvmNet.Ext." + string.Concat(Name.Split('-', StringSplitOptions.RemoveEmptyEntries).Select(part => char.ToUpperInvariant(part[0]) + part[1..])) + ".dll");

    internal string Tag => $"{Name}/{Version}";
}

internal static partial class Extensions
{
    /// <summary>The portable sysroot's ext/ directory; system mode reads the toolchain's own.</summary>
    internal static string Root => Path.Combine(PortableAbi.Sysroot, "ext");

    [GeneratedRegex("^[a-z][a-z0-9]*(-[a-z0-9]+)*$")]
    private static partial Regex NamePattern();

    /// <summary>The include directories of the installed extensions, in name order: they are on the portable include path,
    /// after the sysroot's own headers, as a -dev package's headers would be.</summary>
    internal static IEnumerable<string> IncludeDirectories() => !System.IO.Directory.Exists(Root) ? []
        : System.IO.Directory.GetDirectories(Root).Order(StringComparer.Ordinal).Select(directory => Path.Combine(directory, "include")).Where(System.IO.Directory.Exists);

    /// <summary>The extension named by --extension, or a usage error naming where extensions are installed.</summary>
    internal static ExtensionPackage Find(string name)
    {
        string directory = Path.Combine(Root, name);
        if (!NamePattern().IsMatch(name) || !File.Exists(Path.Combine(directory, "manifest.json")))
            throw new ArgumentException($"native extension '{name}' is not installed in {Root}; llvmnet --list-extensions lists the installed ones.");
        return Read(directory);
    }

    /// <summary>The installed extension whose manifest answers -l<paramref name="library"/>, or null.</summary>
    internal static ExtensionPackage? ForLibrary(string library) => Installed().FirstOrDefault(package => package.Link.Contains(library));

    /// <summary>The installed extensions whose manifests can be read; --list-extensions reports the others.</summary>
    private static List<ExtensionPackage> Installed()
    {
        List<ExtensionPackage> packages = [];
        if (System.IO.Directory.Exists(Root))
            foreach (string directory in System.IO.Directory.GetDirectories(Root).Order(StringComparer.Ordinal))
            {
                try { packages.Add(Read(directory)); }
                catch (Exception error) when (error is InvalidDataException or IOException) { }
            }
        return packages;
    }

    private static ExtensionPackage Read(string directory)
    {
        string manifest = Path.Combine(directory, "manifest.json");
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(manifest));
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("libraries", out JsonElement libraries) || libraries.ValueKind != JsonValueKind.Object)
                throw new FormatException("\"libraries\" must be an object of \"linux\", \"windows\" and \"macos\" name lists");
            return new ExtensionPackage(Text(root, "name"), Text(root, "version"), directory, Names(libraries, "linux"),
                Names(libraries, "windows"), Names(libraries, "macos"), Names(root, "link"), Names(root, "headers"));
        }
        catch (Exception error) when (error is JsonException or FormatException or InvalidOperationException)
        {
            throw new InvalidDataException($"{manifest} is not a valid extension manifest: {error.Message}");
        }

        static string Text(JsonElement element, string property) =>
            element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
                ? text : throw new FormatException($"\"{property}\" must be a non-empty string");

        static string[] Names(JsonElement element, string property) =>
            !element.TryGetProperty(property, out JsonElement value) ? []
            : value.ValueKind == JsonValueKind.Array && value.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 })
                ? [.. value.EnumerateArray().Select(item => item.GetString()!)]
                : throw new FormatException($"\"{property}\" must be a list of non-empty strings");
    }

    /// <summary>
    /// Why an installed extension cannot be used: a manifest that does not match its directory, missing parts, and names
    /// that clash with the sysroot (a header it would shadow or be shadowed by, an -l name the sysroot answers) or with
    /// another installed extension. Empty when it can be used.
    /// </summary>
    internal static List<string> Problems(ExtensionPackage package)
    {
        List<string> problems = [];
        if (package.Name != Path.GetFileName(package.Directory) || !NamePattern().IsMatch(package.Name))
            problems.Add($"its manifest names it '{package.Name}', but it is installed as {Path.GetFileName(package.Directory)} (names are lowercase letters, digits and dashes)");
        if (!File.Exists(package.Assembly))
            problems.Add($"its assembly {package.Assembly} is missing");
        foreach (string header in package.Headers.Where(header => !File.Exists(Path.Combine(package.Include, header))))
            problems.Add($"its header {header} is missing from {package.Include}");
        foreach (string header in Headers(package))
            if (File.Exists(Path.Combine(PortableAbi.Sysroot, "include", header)) || File.Exists(Path.Combine(PortableAbi.Sysroot, "include", "c++", "v1", header)))
                problems.Add($"its header {header} clashes with the sysroot's {header}");
        foreach (string library in package.Link.Where(SysrootLibraries().Contains))
            problems.Add($"-l{library} clashes with the sysroot's library of that name");
        foreach (ExtensionPackage other in Installed().Where(other => other.Name != package.Name))
        {
            foreach (string library in package.Link.Intersect(other.Link))
                problems.Add($"-l{library} is also answered by the extension {other.Name}");
            foreach (string library in package.Linux.Concat(package.Windows).Concat(package.MacOS).Intersect(other.Linux.Concat(other.Windows).Concat(other.MacOS)))
                problems.Add($"its host library {library} is also served by the extension {other.Name}");
            foreach (string header in Headers(package).Intersect(Headers(other)))
                problems.Add($"its header {header} is also installed by the extension {other.Name}");
        }
        return problems;
    }

    /// <summary>Every header file under the extension's include directory, relative to it, with / separators.</summary>
    private static IEnumerable<string> Headers(ExtensionPackage package) => !System.IO.Directory.Exists(package.Include) ? []
        : System.IO.Directory.EnumerateFiles(package.Include, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(package.Include, file).Replace('\\', '/')).Order(StringComparer.Ordinal);

    /// <summary>The -l names the sysroot answers: the C library parts, the runtimes and the archives in lib/.</summary>
    private static HashSet<string> SysrootLibraries()
    {
        HashSet<string> names = [.. Driver.CLibraryNames, .. Driver.PortableRuntimeNames];
        string lib = Path.Combine(PortableAbi.Sysroot, "lib");
        if (System.IO.Directory.Exists(lib))
            foreach (string archive in System.IO.Directory.EnumerateFiles(lib, "lib*.a"))
                names.Add(Path.GetFileNameWithoutExtension(archive)[3..]);
        return names;
    }

    /// <summary>Checks the extensions a link uses and fails with every problem of each, before anything is linked.</summary>
    internal static void Require(IEnumerable<ExtensionPackage> packages)
    {
        foreach (ExtensionPackage package in packages)
            if (Problems(package) is { Count: > 0 } problems)
                throw new InvalidOperationException($"native extension '{package.Name}' cannot be used: {string.Join("; ", problems)}.");
    }

    /// <summary>The --list-extensions text: each installed extension with its names, host libraries, headers and problems.</summary>
    internal static string Listing()
    {
        var text = new StringBuilder();
        string[] directories = System.IO.Directory.Exists(Root) ? [.. System.IO.Directory.GetDirectories(Root).Order(StringComparer.Ordinal)] : [];
        if (directories.Length == 0) return $"No native extensions are installed in {Root}.\n";
        text.AppendLine($"Native extensions installed in {Root}:");
        foreach (string directory in directories)
        {
            ExtensionPackage package;
            try { package = Read(directory); }
            catch (Exception error) when (error is InvalidDataException or IOException)
            {
                text.AppendLine($"{Path.GetFileName(directory)}: cannot be used: {error.Message}");
                continue;
            }
            text.AppendLine($"{package.Name} {package.Version}");
            text.AppendLine($"  -l names:  {(package.Link.Count == 0 ? "none" : string.Join(" ", package.Link.Select(name => "-l" + name)))}");
            text.AppendLine($"  Linux:     {string.Join(" ", package.Linux.DefaultIfEmpty("not available"))}");
            text.AppendLine($"  Windows:   {string.Join(" ", package.Windows.DefaultIfEmpty("not available"))}");
            text.AppendLine($"  macOS:     {string.Join(" ", package.MacOS.DefaultIfEmpty("not available"))}");
            text.AppendLine($"  headers:   {string.Join(" ", package.Headers.DefaultIfEmpty("none"))}");
            foreach (string problem in Problems(package))
                text.AppendLine($"  problem:   {problem}");
        }
        return text.ToString();
    }
}
