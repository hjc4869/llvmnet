using System.Diagnostics;

namespace LlvmNet;

internal static class PortableAbi
{
    /// <summary>ABI tag of portable objects and images. v2 is the musl-based sysroot (ADR-09, LC-26); v1 objects are rejected.</summary>
    internal const string Tag = "dotnet64-v2";

    /// <summary>A sysroot other than the toolchain's own (--portable-sysroot); scripts/build-sysroot.sh uses it while the sysroot is built.</summary>
    internal static string? SysrootOverride { get; set; }

    /// <summary>musl, libc++/libc++abi and flang-rt as LLVM bitcode with their headers, built by scripts/build-sysroot.sh.</summary>
    internal static string Sysroot => SysrootOverride ?? Path.Combine(Toolchain.Root, "sysroot", Tag);

    internal static string Library(string name) => Path.Combine(Sysroot, "lib", name);

    /// <summary>True when the sysroot has flang-rt (FT-01); the C# Fortran runtime is then hidden.</summary>
    internal static bool FlangRuntime => File.Exists(Library("libflang_rt.runtime.a"));

    /// <summary>Fails with build guidance when the sysroot has not been built.</summary>
    internal static void RequireSysroot()
    {
        if (!File.Exists(Path.Combine(Sysroot, "include", "bits", "alltypes.h")))
            throw new DirectoryNotFoundException($"The portable sysroot is missing from {Sysroot}; build it with scripts/build-sysroot.sh.");
    }

    internal static List<string> Arguments(IReadOnlyList<string> frontend, bool fortran, bool cpp)
    {
        foreach (string argument in frontend)
            if (argument is "-target" or "--sysroot" or "-isysroot" or "-fshort-wchar" or "-fshort-enums" or "-funsigned-char" or "-fno-signed-char" || argument.StartsWith("-fpack-struct", StringComparison.Ordinal) || argument.StartsWith("--target=", StringComparison.Ordinal) || argument.StartsWith("--sysroot=", StringComparison.Ordinal) || argument.StartsWith("-m", StringComparison.Ordinal) && !argument.StartsWith("-module", StringComparison.Ordinal))
                throw new ArgumentException($"{argument} cannot change the versioned portable ABI; use --runtime=system for a native target.");
        RequireSysroot();
        string include = Path.Combine(Sysroot, "include");
        List<string> result = ["--target=x86_64-unknown-none", "-D__LLVMNET__=1", "-D__LLVMNET_PORTABLE__=1", "-D__LLVMNET_ABI_VERSION=2"];
        if (fortran) return result;
        result.AddRange(["-nostdinc", "-U__x86_64__", "-U__amd64__", "-U__amd64", "-U__SSE__", "-U__SSE2__"]);
        if (cpp)
        {
            string cppInclude = Path.Combine(Sysroot, "include", "c++", "v1");
            if (!Directory.Exists(cppInclude))
                throw new DirectoryNotFoundException($"The portable sysroot {Sysroot} has no libc++ headers; build it with scripts/build-sysroot.sh.");
            // clang++ defines _GNU_SOURCE on Linux targets and libc++ relies on it; the portable target is freestanding.
            result.AddRange(["-D_GNU_SOURCE", "-nostdinc++", "-isystem", cppInclude]);
        }
        result.AddRange(["-isystem", include]);
        string clang = Toolchain.Executable("clang-22", "LLVMNET_CLANG");
        var start = new ProcessStartInfo(clang) { UseShellExecute = false, RedirectStandardOutput = true };
        start.ArgumentList.Add("-print-resource-dir");
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Cannot find clang resource headers.");
        string resource = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        if (process.ExitCode != 0 || !Directory.Exists(Path.Combine(resource, "include")))
            throw new DirectoryNotFoundException("Clang's builtin headers are missing.");
        result.AddRange(["-isystem", Path.Combine(resource, "include")]);
        return result;
    }
}
