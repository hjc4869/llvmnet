namespace LlvmNet.UnitTests;

/// <summary>Option combinations the driver must reject before invoking any frontend.</summary>
public sealed class DriverTests
{
    [Theory]
    [InlineData("--nativeaot", "-c", "input.c")]
    [InlineData("--aot-debug", "input.c")]
    [InlineData("--runtime=invalid", "input.c")]
    [InlineData("--runtime=portable", "--system-library", "libc.so.6", "input.c")]
    [InlineData("--runtime=system", "--nativeaot", "--runtime-id", "win-x64", "input.c")]
    [InlineData("--aot-instruction-set", "fastest", "input.c")]
    public void InvalidCombinationsAreRejected(params string[] arguments) =>
        Assert.Throws<ArgumentException>(() => Driver.Run(arguments));

    [Theory]
    [InlineData("'-rdynamic' is not accepted: a program's own symbols", "-rdynamic", "input.c")]
    [InlineData("'-Wl,--export-dynamic' is not accepted", "-Wl,--export-dynamic", "input.c")]
    [InlineData("'-Wl,--wrap=malloc' is not accepted: llvmnet links LLVM bitcode", "-Wl,-rpath,/opt,--wrap=malloc", "input.c")]
    [InlineData("'-Wl,-T' is not accepted", "-Xlinker", "-T", "-Xlinker", "script.ld", "input.c")]
    [InlineData("'-Wa,-mbig-obj' is not accepted: there is no assembler step", "-Wa,-mbig-obj", "input.c")]
    [InlineData("'-fsanitize=address' is not accepted: the sanitizer runtimes", "-fsanitize=address", "input.c")]
    [InlineData("'-m32' is not accepted in portable mode", "--runtime=portable", "-m32", "input.c")]
    [InlineData("'--target=x86_64-linux-gnu' is not accepted in portable mode", "--runtime=portable", "--target=x86_64-linux-gnu", "input.c")]
    [InlineData("'-static' is not accepted in system mode", "--runtime=system", "-static", "input.c")]
    [InlineData("'-o' requires a value (<file>)", "-o")]
    [InlineData("'-Wl,-rpath' requires a value", "-Wl,-rpath", "input.c")]
    [InlineData("--driver-mode requires gcc, g++, clang, clang++ or flang", "--driver-mode=rustc", "input.c")]
    [InlineData("'--native-import' is not accepted: it was removed", "--runtime=system", "--native-import", "abs=libc.so.6!abs", "input.c")]
    public void UnsupportedOptionsFailWithAReason(string expected, params string[] arguments)
    {
        var error = Assert.Throws<ArgumentException>(() => Driver.Run(arguments));
        Assert.Contains(expected, error.Message);
    }

    [Theory]
    [InlineData("--target=x86_64-unknown-none")]
    [InlineData("-target", "x86_64-unknown-none")]
    public void NamingThePortableTargetChangesNothing(params string[] target)
    {
        // Build systems pass back what -dumpmachine reported; in portable mode that is the portable triple itself.
        var invocation = new Invocation(DriverOptions.Parse(["--runtime=portable", .. target, "input.c"]), "clang");
        Assert.Empty(invocation.Frontend);
        Assert.Single(invocation.Inputs);
        Assert.Throws<ArgumentException>(() => new Invocation(DriverOptions.Parse(["--runtime=portable", "--target=x86_64-pc-linux-gnu", "input.c"]), "clang"));
    }

    [Fact]
    public void FilesThatNameNoLanguageAreLinkedByTheirContent()
    {
        string directory = Directory.CreateTempSubdirectory("llvmnet-inputs-").FullName;
        try
        {
            string bitcode = Path.Combine(directory, "object.obj"), wrapped = Path.Combine(directory, "wrapped.obj");
            string archive = Path.Combine(directory, "library.lib"), text = Path.Combine(directory, "notes.txt");
            File.WriteAllBytes(bitcode, [0x42, 0x43, 0xC0, 0xDE, 0x35, 0x14, 0x00, 0x00]);
            File.WriteAllBytes(wrapped, [0xDE, 0xC0, 0x17, 0x0B, 0x00, 0x00, 0x00, 0x00]);
            File.WriteAllText(archive, "!<arch>\n");
            File.WriteAllText(text, "not an object");
            Assert.Equal(".o", Driver.LinkerInputKind(bitcode));
            Assert.Equal(".o", Driver.LinkerInputKind(wrapped));
            Assert.Equal(".a", Driver.LinkerInputKind(archive));
            Assert.Null(Driver.LinkerInputKind(text));
            Assert.Null(Driver.LinkerInputKind(Path.Combine(directory, "missing.obj")));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("-o", "x", new[] { "-o" }, new[] { "x" })]
    [InlineData("-ox", "x", new[] { "-o" }, new[] { "x" })]
    [InlineData("--runtime=system", "system", new[] { "--runtime" }, new[] { "system" })]
    [InlineData("--runtime", "system", new[] { "--runtime" }, new[] { "system" })]
    [InlineData("-include", "f.h", new[] { "-include" }, new[] { "f.h" })]
    [InlineData("-object-file-name=x", "x", new[] { "-object-file-name=" }, new[] { "x" })]
    [InlineData("-Wl,-rpath,/opt", "-rpath,/opt", new[] { "-Wl," }, new[] { "-rpath,/opt" })]
    [InlineData("-l:libx.a", ":libx.a", new[] { "-l" }, new[] { ":libx.a" })]
    [InlineData("-Xarch_x86_64", "-foo", new[] { "-Xarch_" }, new[] { "x86_64\0-foo" })]
    public void OptionsAreMatchedByTheTable(string first, string second, string[] names, string[] values)
    {
        var parsed = DriverOptions.Parse([first, second]);
        Assert.Equal(names, parsed.Where(argument => argument.Spec is not null).Select(argument => argument.Spec!.Name).Take(names.Length));
        Assert.Equal(values, parsed.Where(argument => argument.Spec is not null).Select(argument => argument.Value).Take(values.Length));
    }

    [Fact]
    public void EveryTableRowIsDocumentedOnce()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (OptionSpec spec in DriverOptions.Table)
        {
            Assert.True(names.Add(spec.Name), $"{spec.Name} appears twice");
            Assert.True(spec.Group == DriverOptions.Hidden || spec.Help.Length > 0 || spec.Note is { Length: > 0 }, $"{spec.Name} has no help or note");
            Assert.True(spec.Portable != Handling.Rejected && spec.System != Handling.Rejected || spec.Note is { Length: > 0 }, $"{spec.Name} is rejected without a reason");
            Assert.True(spec.Portable != Handling.Ignored && spec.System != Handling.Ignored || spec.Note is { Length: > 0 }, $"{spec.Name} is ignored without a note");
            Assert.Contains(spec.Group, DriverOptions.Groups.Append(DriverOptions.Hidden));
        }
        Assert.Contains("--runtime", DriverOptions.Help("clang"));
        Assert.Contains("| `-Wl,--whole-archive` |", DriverOptions.Reference());
    }

    [Fact]
    public void FrontendSeparateValueOptionsAreKnown()
    {
        // The generated frontend lists are consulted after the hand-written rows, so no value becomes an input.
        var parsed = DriverOptions.Parse(["-Xclang", "-foo", "-isystem", "dir", "input.c"]);
        Assert.Equal(["input.c"], parsed.Where(argument => argument.Spec is null).Select(argument => argument.Text));
        Assert.All(parsed.Where(argument => argument.Spec is not null), argument => Assert.Equal(Handling.Forwarded, argument.Spec!.Portable));
    }
}
