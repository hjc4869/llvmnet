using System.Diagnostics;
using System.Text;

namespace LlvmNet.Runtime;

/// <summary>
/// Launchers: what an executable output (-o name) is besides its payload, name.llvmnet/program.dll. The compiler writes a
/// POSIX shell script, and on Windows also name.cmd; both name their payload on their second line after
/// <see cref="Marker"/>. A host cannot always run the script itself (Windows has no /bin/sh), so the host layer's
/// posix_spawn and the compiler start a launcher's payload with dotnet directly, passing what the script passes:
/// LLVMNET_ARGV0, the name the program was started by, and LLVMNET_EXE, the launcher's path with links resolved
/// (ProcessRuntime.ProgramName and Executable). That also spares a shell per start where the script would run.
/// </summary>
public static class Launchers
{
    /// <summary>The text before the payload's path on a launcher's second line, after "# " or "rem ".</summary>
    public const string Marker = "llvmnet-payload: ";

    /// <summary>
    /// The payload of the launcher at <paramref name="path"/>, or null when it is not an llvmnet launcher: a file whose
    /// second line names a payload that exists, relative to the launcher's directory with links resolved, or a shell
    /// script beside a <c>&lt;name&gt;.llvmnet/program.dll</c> (launchers written before the marker).
    /// </summary>
    public static string? Payload(string path)
    {
        try
        {
            string launcher = Resolve(path);
            Span<byte> head = stackalloc byte[1024];
            int read;
            using (FileStream stream = File.OpenRead(launcher))
                read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
            string text = Encoding.UTF8.GetString(head[..read]);
            string[] lines = text.Split('\n', 3);
            if (lines.Length >= 2)
            {
                string line = lines[1].TrimEnd('\r');
                int marker = line.IndexOf(Marker, StringComparison.Ordinal);
                if (marker >= 0 && line[..marker] is "# " or "rem ")
                {
                    string payload = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(launcher)!, line[(marker + Marker.Length)..].Replace('\\', '/')));
                    return File.Exists(payload) ? payload : null;
                }
            }
            if (!text.StartsWith("#!/bin/sh", StringComparison.Ordinal)) return null;
            string conventional = Path.Combine(Path.GetDirectoryName(launcher)!, Path.GetFileName(launcher) + ".llvmnet", "program.dll");
            return File.Exists(conventional) ? conventional : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>The path with every symbolic link resolved, as the kernel resolves /proc/self/exe.</summary>
    public static string Resolve(string path)
    {
        string full = Path.GetFullPath(path);
        return File.ResolveLinkTarget(full, returnFinalTarget: true)?.FullName ?? full;
    }

    /// <summary>
    /// The dotnet host that runs payloads: LLVMNET_DOTNET when set, else this process's own host when it runs under one
    /// (so a toolchain started through a dotnet that is not on PATH keeps using it), else DOTNET_ROOT's, else dotnet from PATH.
    /// </summary>
    public static string Dotnet
    {
        get
        {
            if (Environment.GetEnvironmentVariable("LLVMNET_DOTNET") is { Length: > 0 } configured) return configured;
            string executable = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
            if (Environment.ProcessPath is string host && Path.GetFileName(host).Equals(executable, StringComparison.OrdinalIgnoreCase)) return host;
            if (Environment.GetEnvironmentVariable("DOTNET_ROOT") is { Length: > 0 } root && File.Exists(Path.Combine(root, executable))) return Path.Combine(root, executable);
            return "dotnet";
        }
    }

    /// <summary>
    /// How to start the launcher at <paramref name="launcher"/> with <paramref name="payload"/> (see <see cref="Payload"/>):
    /// dotnet with the payload, or the payload itself when it is a native executable rather than an image (.dll), and the
    /// two variables a launcher passes. <paramref name="argv0"/> is the name it is started by. A native payload is how a
    /// package precompiled for Windows gives a NativeAOT tool its other names (clang++ for clang-22.exe, flang for
    /// flang-22.exe), which Windows cannot link to it.
    /// </summary>
    public static ProcessStartInfo Start(string launcher, string payload, string argv0)
    {
        bool image = payload.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
        var start = new ProcessStartInfo(image ? Dotnet : payload) { UseShellExecute = false };
        if (image) start.ArgumentList.Add(payload);
        start.Environment["LLVMNET_ARGV0"] = argv0;
        // A Windows .cmd launcher stands beside the shell script of the same name, which is the program's own path.
        string self = launcher.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) && File.Exists(launcher[..^4]) ? launcher[..^4] : launcher;
        start.Environment["LLVMNET_EXE"] = Resolve(self);
        return start;
    }
}
