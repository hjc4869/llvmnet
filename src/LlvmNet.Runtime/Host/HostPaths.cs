using System.Diagnostics.CodeAnalysis;

namespace LlvmNet.Runtime;

/// <summary>
/// The path forms a portable program sees. Programs use Linux paths: absolute paths start with "/", and musl
/// (getcwd, realpath, dirname, glob, tmpfile) assumes that. On Linux and macOS the host's paths already have that form.
/// On Windows the host layer presents a drive as a root directory named by its letter ("C:\Users\x" is "/c/Users/x",
/// "\\server\share\x" is "//server/share/x") and accepts both forms, plus "/tmp" for the host's temporary directory,
/// as documented in docs/dev/host-abi.md.
/// </summary>
internal static class HostPaths
{
    /// <summary>A path the program passed, in the form the host's file APIs take.</summary>
    internal static string ToHost(string path) => ToHost(path, OperatingSystem.IsWindows(), OperatingSystem.IsWindows() ? Path.GetTempPath() : "/tmp");

    internal static string ToHost(string path, bool windows, string temporaryDirectory)
    {
        if (!windows || path.Length == 0) return path;
        path = path.Replace('\\', '/');
        if (path[0] != '/') return path;
        if (path.Length >= 2 && char.IsAsciiLetter(path[1]) && (path.Length == 2 || path[2] == '/'))
            return char.ToUpperInvariant(path[1]) + ":/" + (path.Length > 3 ? path[3..].TrimStart('/') : "");
        if (path.StartsWith("//", StringComparison.Ordinal) && path.Length > 2 && path[2] != '/')
            return path;                              // a UNC path; .NET accepts the forward-slash spelling
        if (path == "/tmp" || path.StartsWith("/tmp/", StringComparison.Ordinal))
            return temporaryDirectory.Replace('\\', '/').TrimEnd('/') + path[4..];
        return path;                                  // the root of the current drive, as Windows itself reads "/x"
    }

    /// <summary>A path from the host (the current directory, a link target, the executable) in the program's form.</summary>
    [return: NotNullIfNotNull(nameof(path))]
    internal static string? ToProgram(string? path) => ToProgram(path, OperatingSystem.IsWindows());

    [return: NotNullIfNotNull(nameof(path))]
    internal static string? ToProgram(string? path, bool windows)
    {
        if (!windows || path is null) return path;
        path = path.Replace('\\', '/');
        if (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':')
            return "/" + char.ToLowerInvariant(path[0]) + (path.Length > 2 && path[2] == '/' ? path[2..] : "/" + path[2..]);
        return path;                                  // UNC "//server/share/x" and relative paths keep their spelling
    }

    private static readonly string processExecutableLink = $"/proc/{Environment.ProcessId}/exe";

    /// <summary>
    /// /proc/self/exe, the link to the program's executable (ProcessRuntime.Executable), which the host layer serves on
    /// every host; also under the process's own id, where realpath arrives after reading the /proc/self link on Linux.
    /// </summary>
    internal static bool IsExecutableLink(string path) => path == "/proc/self/exe" || path == processExecutableLink;

    private static readonly string processDirectory = $"/proc/{Environment.ProcessId}";

    /// <summary>
    /// On a host without /proc (macOS, Windows), the directories above /proc/self/exe, which musl's realpath reads as
    /// links on its way: /proc/self is a link to the process's id there as on Linux, and the others are directories.
    /// </summary>
    internal static bool IsProcessDirectory(string path) =>
        !OperatingSystem.IsLinux() && (path == "/proc" || path == "/proc/self" || path == processDirectory);

    /// <summary>Paths the host layer answers itself on every host, which must not be read as host paths (on Windows, "/dev" would name a directory on the current drive).</summary>
    internal static bool IsVirtual(string path) =>
        IsExecutableLink(path) || IsProcessDirectory(path) || path == "/etc/localtime" || path == "/etc/passwd" || path == "/etc/group" ||
        path.StartsWith("/dev/", StringComparison.Ordinal) || path.StartsWith("/usr/share/zoneinfo/", StringComparison.Ordinal) ||
        path.StartsWith("/share/zoneinfo/", StringComparison.Ordinal) || path.StartsWith("/etc/zoneinfo/", StringComparison.Ordinal);

    /// <summary>The components after the root, which resolves ".." without a textual shortcut (see HostSyscalls.ResolveParents).</summary>
    internal static (string Root, string[] Components) Split(string hostPath)
    {
        string root = Path.GetPathRoot(hostPath) is { Length: > 0 } found ? found : "/";
        return (root, hostPath[root.Length..].Split(OperatingSystem.IsWindows() ? ['/', '\\'] : ['/'], StringSplitOptions.RemoveEmptyEntries));
    }
}
