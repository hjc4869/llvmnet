using System.Xml.Linq;

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

    /// <summary>
    /// NativeAOT projects drop ICU on Linux and macOS but keep it for win-* runtime identifiers, where .NET needs it to
    /// resolve IANA time-zone names (PF-10); `dotnet msbuild -getProperty` evaluates the condition the same way.
    /// </summary>
    [Fact]
    public void NativeAotProjectsKeepIcuOnWindows()
    {
        string directory = Directory.CreateTempSubdirectory("llvmnet-unit-").FullName;
        try
        {
            string assembly = Path.Combine(directory, "input", "program.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(assembly)!);
            File.Copy(typeof(Toolchain).Assembly.Location, assembly);   // any managed assembly: the project names it
            string project = Path.Combine(directory, "project");
            Directory.CreateDirectory(project);
            Toolchain.AotProject(assembly, project, "Speed", "", false, []);
            XElement setting = XDocument.Load(Path.Combine(project, "NativeHost.csproj")).Descendants("InvariantGlobalization").Single();
            Assert.Equal("true", setting.Value);
            Assert.Equal("!$(RuntimeIdentifier.StartsWith('win'))", setting.Attribute("Condition")?.Value);
        }
        finally { Directory.Delete(directory, true); }
    }
}
