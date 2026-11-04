using System.Diagnostics;

namespace LlvmNet;

internal static class PortableAbi
{
    /// <summary>ABI tag of portable objects and images, versioned with the musl-based sysroot.</summary>
    internal const string Tag = "dotnet64-v2";

    /// <summary>A sysroot other than the toolchain's own (--portable-sysroot), used by the sysroot build scripts.</summary>
    internal static string? SysrootOverride { get; set; }

    /// <summary>musl, libc++/libc++abi and flang-rt as LLVM bitcode with their headers, built by scripts/build-sysroot.sh.</summary>
    internal static string Sysroot => SysrootOverride ?? Path.Combine(Toolchain.Root, "sysroot", Tag);

    internal static string Library(string name) => Path.Combine(Sysroot, "lib", name);

    /// <summary>Fails with build guidance when the sysroot has not been built.</summary>
    internal static void RequireSysroot()
    {
        if (!File.Exists(Path.Combine(Sysroot, "include", "bits", "alltypes.h")))
            throw new DirectoryNotFoundException($"The portable sysroot is missing from {Sysroot}; build it with scripts/build-sysroot.sh.");
    }

    /// <summary>The target triple the frontends compile portable code for; -dumpmachine reports it in portable mode.</summary>
    internal const string Triple = "x86_64-unknown-none";

    internal static List<string> Arguments(IReadOnlyList<string> frontend, bool fortran, bool cpp, string isa)
    {
        // The driver's option table rejects the options that would change the ABI (-target, --sysroot, -m<...>,
        // -fshort-wchar and the others); this is the backstop for a caller that bypasses it. Plain char's signedness
        // changes no layout, so -funsigned-char objects link with the signed-char sysroot as they do natively; each
        // side reads a char argument or result with its own signedness (ADR-09).
        foreach (string argument in frontend)
            if (argument is "-target" or "--sysroot" or "-isysroot" or "-fshort-wchar" or "-fshort-enums" || argument.StartsWith("-fpack-struct", StringComparison.Ordinal) || argument.StartsWith("--target=", StringComparison.Ordinal) || argument.StartsWith("--sysroot=", StringComparison.Ordinal))
                throw new ArgumentException($"{argument} cannot change the versioned portable ABI; use --runtime=system for a native target.");
        RequireSysroot();
        string include = Path.Combine(Sysroot, "include");
        List<string> result = ["--target=" + Triple, "-D__LLVMNET__=1", "-D__LLVMNET_PORTABLE__=1", "-D__LLVMNET_ABI_VERSION=2"];
        if (fortran) return result;
        // The fixed x86-64 data layout must not imply x86 instruction sets: libraries select x86 code paths by these
        // macros. --isa=x86-64-v2 and x86-64-v3 keep them, with the SSE3 to AVX2 ones -march adds, because the runtime
        // translates those intrinsics, and the MMX ones, which Clang implements with SSE2 (ADR-16 Level 2); the layouts
        // do not depend on them.
        result.Add("-nostdinc");
        if (isa is not ("x86-64-v2" or "x86-64-v3"))
            result.AddRange(["-U__x86_64__", "-U__amd64__", "-U__amd64", "-U__SSE__", "-U__SSE2__", "-U__MMX__", "-U__SSE_MATH__", "-U__SSE2_MATH__", "-U__FXSR__"]);
        if (cpp)
        {
            string cppInclude = Path.Combine(Sysroot, "include", "c++", "v1");
            if (!Directory.Exists(cppInclude))
                throw new DirectoryNotFoundException($"The portable sysroot {Sysroot} has no libc++ headers; build it with scripts/build-sysroot.sh.");
            // clang++ defines _GNU_SOURCE on Linux targets and libc++ relies on it; the portable target is freestanding.
            result.AddRange(["-D_GNU_SOURCE", "-nostdinc++", "-isystem", cppInclude]);
        }
        result.AddRange(["-isystem", include, "-isystem", Extensions.InterfaceInclude]);
        foreach (string directory in Extensions.IncludeDirectories())
            result.AddRange(["-isystem", directory]);
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
