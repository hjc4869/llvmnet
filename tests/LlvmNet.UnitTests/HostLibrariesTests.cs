using System.Runtime.InteropServices;
using LlvmNet.Runtime;

namespace LlvmNet.UnitTests;

/// <summary>
/// The runtime support of generated native extensions (NX-04): how a host library is found and reported, how stubs bind
/// through exports and the API's lookup function, reverse-thunk slots, and the &lt;llvmnet/ext.h&gt; answers.
/// tests/ext-gen.sh runs generated extensions end to end.
/// </summary>
public sealed unsafe class HostLibrariesTests
{
    /// <summary>A library every test host has, under its name for the running operating system, and one of its exports.</summary>
    private static (string Name, string Export) SystemLibrary =>
        OperatingSystem.IsWindows() ? ("kernel32.dll", "GetTickCount") : OperatingSystem.IsMacOS() ? ("libSystem.dylib", "strlen") : ("libc.so.6", "strlen");

    private static HostLibrary Available(string extension, params HostLibrary[] preload)
    {
        string name = SystemLibrary.Name;
        return new HostLibrary(extension, "System", [name], [name], [name], preload);
    }

    [Fact]
    public void AMissingLibraryNamesEveryCandidateAndTheVariable()
    {
        var library = new HostLibrary("unit-missing", "Missing", ["libunit-missing.so.1"], ["unit-missing.dll"], ["libunit-missing.dylib"]);
        Assert.Equal(0, library.Handle);
        Assert.Equal(0, library.Export("anything"));
        Assert.Null(library.LoadedFrom);
        string file = OperatingSystem.IsWindows() ? "unit-missing.dll" : OperatingSystem.IsMacOS() ? "libunit-missing.dylib" : "libunit-missing.so.1";
        string problem = library.Problem!;
        Assert.StartsWith($"the host library Missing of the native extension unit-missing is not available (tried {Path.Combine(AppContext.BaseDirectory, file)}, {file}; LLVMNET_EXT_UNIT_MISSING_DIR names another directory)", problem);
        Assert.Equal("LLVMNET_EXT_UNIT_MISSING_DIR", library.DirectoryVariable);
    }

    [Fact]
    public void ALibraryWithoutNamesForThisSystemDoesNotExistHere()
    {
        var library = new HostLibrary("unit-nowhere", "Nowhere", [], [], []);
        Assert.Equal(0, library.Handle);
        Assert.Equal("the host library Nowhere of the native extension unit-nowhere does not exist on this operating system", library.Problem);
    }

    [Fact]
    public void TheDirectoryVariableIsSearchedFirst()
    {
        string directory = Path.Combine(Path.GetTempPath(), "llvmnet-unit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string library = Path.Combine(directory, "libunit-copy.so");
            File.Copy(typeof(HostLibrariesTests).Assembly.Location, library);
            Environment.SetEnvironmentVariable("LLVMNET_EXT_UNIT_FIRST_DIR", directory);
            // A managed assembly is not a native library: the candidate in the directory is tried, and the loader's
            // message for it is reported, before the names on the default path.
            var host = new HostLibrary("unit-first", "Copy", ["libunit-copy.so"], ["libunit-copy.so"], ["libunit-copy.so"]);
            Assert.Equal(0, host.Handle);
            Assert.Contains($"(tried {library}, ", host.Problem);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LLVMNET_EXT_UNIT_FIRST_DIR", null);
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void ALoadedLibraryReportsItsPathAndExports()
    {
        HostLibrary library = Available("unit-loaded");
        Assert.NotEqual(0, library.Handle);
        Assert.Equal(SystemLibrary.Name, library.LoadedFrom);
        Assert.Null(library.Problem);
        Assert.NotEqual(0, library.Export(SystemLibrary.Export));
        Assert.Equal(0, library.Export("llvmnet_unit_no_such_function"));
    }

    [Theory]
    [InlineData("Unable to load shared library 'libEGL.so.1' or one of its dependencies. In order to help diagnose loading problems, consider using a tool like strace. If you're using glibc, consider setting the LD_DEBUG environment variable: \nlibEGL.so.1: cannot open shared object file: No such file or directory\n",
        "libEGL.so.1: cannot open shared object file: No such file or directory")]
    [InlineData("Unable to load shared library 'libEGL.dylib' or one of its dependencies. In order to help diagnose loading problems, consider setting the DYLD_PRINT_LIBRARIES environment variable: \ndlopen(libEGL.dylib, 0x0001): tried: 'libEGL.dylib' (no such file)\n  next line",
        "dlopen(libEGL.dylib, 0x0001): tried: 'libEGL.dylib' (no such file); next line")]
    [InlineData("Unable to load DLL 'libEGL.dll' or one of its dependencies: The specified module could not be found. (0x8007007E)",
        "The specified module could not be found. (0x8007007E)")]
    [InlineData("plain", "plain")]
    public void LoaderMessagesKeepTheSystemLoadersWords(string message, string expected) => Assert.Equal(expected, HostLibrary.LoaderMessage(message));

    [Fact]
    public void StubsBindThroughExportsThenTheLookupFunction()
    {
        HostLibrary library = Available("unit-bind");
        List<string> asked = [];
        var host = new HostEntryPoints("unit-bind", [library], name =>
        {
            asked.Add(name);
            return name == "unit_extension_function" ? 0x1234 : 0;
        });
        nint slot = 0;
        nint export = host.Bind(ref slot, library, SystemLibrary.Export);
        Assert.Equal(library.Export(SystemLibrary.Export), export);
        Assert.Equal(export, slot);
        Assert.Empty(asked);
        slot = 0;
        Assert.Equal(0x1234, host.TryBind(ref slot, library, "unit_extension_function"));
        Assert.Equal(0x1234, slot);
        slot = 0;
        Assert.Equal(0, host.TryBind(ref slot, library, "unit_absent_function"));
        Assert.Equal(0, slot);
        Assert.Equal(new[] { "unit_extension_function", "unit_absent_function" }, asked);
    }

    [Fact]
    public void AvailabilityNamesTheFirstMissingPiece()
    {
        HostLibrary library = Available("unit-required");
        var missing = new HostLibrary("unit-required", "Missing", ["libunit-required.so.1"], ["unit-required.dll"], ["libunit-required.dylib"]);
        var complete = new HostEntryPoints("unit-required", [library]);
        Assert.Null(complete.Unavailable((library, SystemLibrary.Export)));
        Assert.Equal($"the host library System of the native extension unit-required ({SystemLibrary.Name}) does not provide unit_required_function",
            complete.Unavailable((library, SystemLibrary.Export), (library, "unit_required_function")));
        Assert.Equal($"System: {SystemLibrary.Name}", complete.Report());
        var incomplete = new HostEntryPoints("unit-required", [library, missing]);
        Assert.StartsWith("the host library Missing of the native extension unit-required is not available", incomplete.Unavailable());
        Assert.StartsWith($"System: {SystemLibrary.Name}\nMissing: the host library Missing", incomplete.Report());
    }

    [Fact]
    public void PreloadedLibrariesLoadFirst()
    {
        HostLibrary first = Available("unit-preload");
        HostLibrary second = Available("unit-preload", first);
        Assert.NotEqual(0, second.Handle);
        Assert.Equal(SystemLibrary.Name, first.LoadedFrom);
    }

    [Fact]
    public void AFunctionKeepsItsThunkSlot()
    {
        nint[] targets = new nint[3];
        Assert.Equal(0, NativeCallbacks.Slot(targets, 0x10, "unit", "UNITPROC"));
        Assert.Equal(1, NativeCallbacks.Slot(targets, 0x20, "unit", "UNITPROC"));
        Assert.Equal(0, NativeCallbacks.Slot(targets, 0x10, "unit", "UNITPROC"));
        Assert.Equal(2, NativeCallbacks.Slot(targets, 0x30, "unit", "UNITPROC"));
        Assert.Equal(1, NativeCallbacks.Slot(targets, 0x20, "unit", "UNITPROC"));
        Assert.Equal(new nint[] { 0x10, 0x20, 0x30 }, targets);
    }

    private sealed class Reporting(string name, string? problem) : NativeExtension(name, $"lib{name}.so.1")
    {
        public override nint Resolve(string library, string symbol) => 0;

        public override string? Unavailable() => problem;

        public override string Report() => problem ?? $"{Name}: loaded";
    }

    private static nint Text(string text) => Marshal.StringToCoTaskMemUTF8(text);

    [Fact]
    public void TheInterfaceHeaderAnswersForRegisteredExtensions()
    {
        NativeExtensions.Register(new Reporting("unit-ext-ready", null));
        NativeExtensions.Register(new Reporting("unit-ext-absent", "the host library is not available"));
        NativeExtensions.RegisterSystem("unit-ext-system", "libunit.so.1 libunit-other.so.2");
        nint ready = Text("unit-ext-ready"), absent = Text("unit-ext-absent"), system = Text("unit-ext-system"), unknown = Text("unit-ext-unknown");
        try
        {
            Assert.Equal(1, NativeExtensions.Available(ready));
            Assert.Equal(0, NativeExtensions.Available(absent));
            Assert.Equal(1, NativeExtensions.Available(system));
            Assert.Equal(0, NativeExtensions.Available(unknown));
            Assert.Equal(0, NativeExtensions.Available(0));
            Assert.Equal("unit-ext-ready: loaded", Marshal.PtrToStringUTF8(NativeExtensions.Library(ready)));
            Assert.Equal("the host library is not available", Marshal.PtrToStringUTF8(NativeExtensions.Library(absent)));
            Assert.Equal("unit-ext-system: linked in system mode with libunit.so.1 libunit-other.so.2", Marshal.PtrToStringUTF8(NativeExtensions.Library(system)));
            Assert.Equal(0, NativeExtensions.Library(unknown));
            Assert.Equal(NativeExtensions.Library(ready), NativeExtensions.Library(ready));
        }
        finally
        {
            foreach (nint text in new[] { ready, absent, system, unknown }) Marshal.FreeCoTaskMem(text);
        }
    }
}
