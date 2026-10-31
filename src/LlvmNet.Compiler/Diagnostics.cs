namespace LlvmNet;

/// <summary>
/// Prints llvmnet's own diagnostics in Clang's form: "location: error: message" with the location, the severity and the
/// message in Clang's colors when they go to a terminal, or when -fcolor-diagnostics or -fdiagnostics-color=always
/// asks for them; -fno-color-diagnostics and -fdiagnostics-color=never turn them off, as for the frontend (TC-05).
/// </summary>
internal sealed class Diagnostics
{
    private readonly bool color;

    internal Diagnostics(IReadOnlyList<string> arguments)
    {
        bool? requested = null;
        foreach (string argument in arguments)
            switch (argument)
            {
                case "-fcolor-diagnostics" or "-fdiagnostics-color" or "-fdiagnostics-color=always": requested = true; break;
                case "-fno-color-diagnostics" or "-fdiagnostics-color=never": requested = false; break;
                case "-fdiagnostics-color=auto": requested = null; break;
            }
        color = requested ?? (!Console.IsErrorRedirected && Environment.GetEnvironmentVariable("NO_COLOR") is null or "" && Environment.GetEnvironmentVariable("TERM") is not (null or "" or "dumb"));
    }

    internal void Error(string? location, string message) => Write(location, "error", "0;1;31", message);

    internal void Note(string? location, string message) => Write(location, "note", "0;1;30", message);

    internal void Warning(string? location, string message) => Write(location, "warning", "0;1;35", message);

    private void Write(string? location, string severity, string code, string message)
    {
        string prefix = location ?? "llvmnet";
        Console.Error.WriteLine(color
            ? $"\u001b[1m{prefix}: \u001b[{code}m{severity}: \u001b[0m\u001b[1m{message}\u001b[0m"
            : $"{prefix}: {severity}: {message}");
    }
}
