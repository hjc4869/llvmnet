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

    /// <summary>
    /// The sysroot's ABI stamp of an ISA level (lib/abi/&lt;level&gt;.bc, scripts/build-sysroot.sh): a bitcode module with the
    /// portable target and nothing but the module flags that tag a portable object, llvmnet.abi and the level's
    /// llvmnet.isa. Clang links it into what it compiles (-mlink-builtin-bitcode), so the object comes out tagged and the
    /// driver does not load LLVM to parse and rewrite it, which took most of the driver's time for a compile on the
    /// managed in-process LLVM of a portable toolchain (XP-17). Null when the sysroot has none, as one that is being
    /// built or was built before the stamps; the driver then tags the object itself.
    /// </summary>
    internal static string? AbiStamp(string isa) => Library(Path.Combine("abi", isa + ".bc")) is string path && File.Exists(path) ? path : null;

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
        result.AddRange(["-isystem", Path.Combine(ResourceDirectory.Value, "include")]);
        return result;
    }

    /// <summary>
    /// Clang's resource directory, whose include/ holds the builtin headers: lib/clang/22 beside the frontend's own bin/
    /// (links resolved), as Clang finds it, which spares the frontend start that -print-resource-dir costs (seconds for a
    /// JIT-compiled llvmnet-built clang); else what the frontend prints.
    /// </summary>
    private static readonly Lazy<string> ResourceDirectory = new(() =>
    {
        string clang = Toolchain.Executable("clang-22", "LLVMNET_CLANG");
        string? located = File.Exists(clang) ? clang : Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator)
            .Select(directory => Path.Combine(directory, clang)).FirstOrDefault(File.Exists);
        if (located is not null && Path.GetDirectoryName(Path.GetDirectoryName(Runtime.Launchers.Resolve(located))) is string prefix &&
            Path.Combine(prefix, "lib", "clang", LlvmMajor) is string beside && File.Exists(Path.Combine(beside, "include", "stddef.h")))
            return beside;
        ProcessStartInfo start = Toolchain.Start(clang, ["-print-resource-dir"]);
        start.RedirectStandardOutput = true;
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Cannot find clang resource headers.");
        string resource = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        // A portable clang on Windows prints the directory in its own form (/c/...).
        if (OperatingSystem.IsWindows() && resource.Length > 2 && resource[0] == '/' && char.IsAsciiLetter(resource[1]) && (resource.Length == 2 || resource[2] == '/'))
            resource = resource[1] + ":" + resource[2..];
        if (process.ExitCode != 0 || !Directory.Exists(Path.Combine(resource, "include")))
            throw new DirectoryNotFoundException("Clang's builtin headers are missing.");
        return resource;
    });

    /// <summary>The LLVM major version llvmnet pins (ADR-06), which names Clang's resource directory.</summary>
    internal const string LlvmMajor = "22";
}
