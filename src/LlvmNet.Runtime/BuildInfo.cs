using System.Reflection;

namespace LlvmNet.Runtime;

/// <summary>Version identity shared by the compiler and the runtime, which are always built together.</summary>
public static class BuildInfo
{
    /// <summary>Informational version, for example <c>0.3.0-dev+&lt;commit&gt;</c>.</summary>
    public static string Version { get; } =
        typeof(BuildInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0-unknown";

    /// <summary>
    /// The part of a version that must match between an emitted image and the runtime that loads it:
    /// <c>major.minor</c> while the major version is 0 (every 0.x minor release may break compatibility), otherwise <c>major</c>.
    /// </summary>
    public static string CompatibilityKey(string version)
    {
        string core = version.Split('+', '-')[0];
        string[] parts = core.Split('.');
        if (parts.Length < 2 || !int.TryParse(parts[0], out int major) || !int.TryParse(parts[1], out int minor))
            throw new FormatException($"Invalid llvmnet version '{version}'.");
        return major == 0 ? $"0.{minor}" : major.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public static bool Compatible(string version) => CompatibilityKey(version) == CompatibilityKey(Version);
}
