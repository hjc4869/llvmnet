using System.Diagnostics;

namespace LlvmNet;

internal static class PortableAbi
{
    /// <summary>Experimental (plan item LC-01): a musl prefix built by scripts/build-portable-libc.sh replaces the hand-written C headers and runtime.</summary>
    internal static string? Libc { get; set; }

    /// <summary>True when the musl prefix also has flang-rt (FT-01); the C# Fortran runtime is then hidden.</summary>
    internal static bool FlangRuntime => Libc is not null && File.Exists(Path.Combine(Libc, "lib", "libflang_rt.runtime.a"));

    internal static List<string> Arguments(IReadOnlyList<string> frontend, bool fortran, bool cpp)
    {
        foreach (string argument in frontend)
            if (argument is "-target" or "--sysroot" or "-isysroot" or "-fshort-wchar" or "-fshort-enums" or "-funsigned-char" or "-fno-signed-char" || argument.StartsWith("-fpack-struct", StringComparison.Ordinal) || argument.StartsWith("--target=", StringComparison.Ordinal) || argument.StartsWith("--sysroot=", StringComparison.Ordinal) || argument.StartsWith("-m", StringComparison.Ordinal) && !argument.StartsWith("-module", StringComparison.Ordinal))
                throw new ArgumentException($"{argument} cannot change the versioned portable ABI; use --runtime=system for a native target.");
        string include = Libc is null ? Path.Combine(Toolchain.Root, "sysroot", "dotnet64-v1", "include") : Path.Combine(Libc, "include");
        if (!File.Exists(Path.Combine(include, Libc is null ? "__llvmnet_types.h" : "bits/alltypes.h")))
            throw new DirectoryNotFoundException($"Portable ABI sysroot is missing from {include}");
        List<string> result = ["--target=x86_64-unknown-none", "-D__LLVMNET__=1", "-D__LLVMNET_PORTABLE__=1", "-D__LLVMNET_ABI_VERSION=1"];
        if (fortran) return result;
        result.AddRange(["-nostdinc", "-U__x86_64__", "-U__amd64__", "-U__amd64", "-U__SSE__", "-U__SSE2__"]);
        if (cpp)
        {
            string cppInclude = Libc is not null && Directory.Exists(Path.Combine(Libc, "include", "c++", "v1"))
                ? Path.Combine(Libc, "include", "c++", "v1") : Path.Combine(Toolchain.Root, "sysroot", "dotnet64-v1", "include", "c++", "v1");
            if (!Directory.Exists(cppInclude))
                throw new DirectoryNotFoundException("Portable C++ requires the bundled libc++ headers; run scripts/install-libcxx-headers.sh before packaging the toolchain.");
            // clang++ defines _GNU_SOURCE on Linux targets and libc++ relies on it; the portable target is freestanding.
            if (Libc is not null && cppInclude.StartsWith(Libc, StringComparison.Ordinal))
                result.AddRange(["-D_GNU_SOURCE", "-nostdinc++", "-isystem", cppInclude]);
            else
                result.AddRange(["-nostdinc++", "-isystem", Path.Combine(Toolchain.Root, "sysroot", "dotnet64-v1", "cxx-config"), "-isystem", cppInclude]);
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
