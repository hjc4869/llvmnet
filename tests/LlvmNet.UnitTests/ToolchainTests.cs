namespace LlvmNet.UnitTests;

public sealed class ToolchainTests
{
    [Fact]
    public void ResponseFileTokens() =>
        Assert.Equal(["-DNAME=a b", "single quoted", @"back\slash", "x", "y z"],
            Toolchain.Tokenize("\"-DNAME=a b\" 'single quoted'  back\\\\slash\n x y\\ z"));

    [Fact]
    public void UnclosedQuoteIsRejected() => Assert.Throws<ArgumentException>(() => Toolchain.Tokenize("\"open"));

    [Fact]
    public void ResponseFilesExpandRecursivelyAndRejectCycles()
    {
        string directory = Directory.CreateTempSubdirectory("llvmnet-unit-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(directory, "outer.rsp"), "-O2 @inner.rsp -c");
            File.WriteAllText(Path.Combine(directory, "inner.rsp"), "-DX=1");
            Assert.Equal(["-O2", "-DX=1", "-c", "main.c"], Toolchain.ExpandResponseFiles(["@outer.rsp", "main.c"], directory));
            File.WriteAllText(Path.Combine(directory, "inner.rsp"), "@outer.rsp");
            Assert.Throws<ArgumentException>(() => Toolchain.ExpandResponseFiles(["@outer.rsp"], directory));
        }
        finally { Directory.Delete(directory, true); }
    }
}
