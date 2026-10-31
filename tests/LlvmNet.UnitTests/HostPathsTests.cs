using LlvmNet.Runtime;

namespace LlvmNet.UnitTests;

/// <summary>The path forms portable programs see on Windows (docs/dev/host-abi.md); other hosts pass paths through.</summary>
public sealed class HostPathsTests
{
    [Theory]
    [InlineData("/c/Users/x/file.txt", "C:/Users/x/file.txt")]
    [InlineData("/C", "C:/")]
    [InlineData("/d/", "D:/")]
    [InlineData("C:\\Users\\x", "C:/Users/x")]
    [InlineData("C:/already", "C:/already")]
    [InlineData("//server/share/dir", "//server/share/dir")]
    [InlineData("/tmp", "C:/Users/x/AppData/Local/Temp")]
    [InlineData("/tmp/work/a", "C:/Users/x/AppData/Local/Temp/work/a")]
    [InlineData("/other/root", "/other/root")]
    [InlineData("relative/path", "relative/path")]
    [InlineData("", "")]
    public void WindowsProgramPathsBecomeHostPaths(string program, string host) =>
        Assert.Equal(host, HostPaths.ToHost(program, windows: true, "C:\\Users\\x\\AppData\\Local\\Temp\\"));

    [Theory]
    [InlineData("C:\\Users\\x\\dir", "/c/Users/x/dir")]
    [InlineData("C:\\", "/c/")]
    [InlineData("D:relative", "/d/relative")]
    [InlineData("\\\\server\\share\\dir", "//server/share/dir")]
    [InlineData("sub\\dir", "sub/dir")]
    public void WindowsHostPathsBecomeProgramPaths(string host, string program) =>
        Assert.Equal(program, HostPaths.ToProgram(host, windows: true));

    [Fact]
    public void OtherHostsPassPathsThrough()
    {
        Assert.Equal("/c/Users/x", HostPaths.ToHost("/c/Users/x", windows: false, "/tmp"));
        Assert.Equal("/tmp/x", HostPaths.ToHost("/tmp/x", windows: false, "/var/tmp"));
        Assert.Equal("/home/x/a", HostPaths.ToProgram("/home/x/a", windows: false));
    }

    [Theory]
    [InlineData("/dev/null", true)]
    [InlineData("/dev/urandom", true)]
    [InlineData("/proc/self/exe", true)]
    [InlineData("/etc/localtime", true)]
    [InlineData("/usr/share/zoneinfo/Europe/Berlin", true)]
    [InlineData("/etc/hosts", false)]
    [InlineData("/proc/cpuinfo", false)]
    [InlineData("dev/null", false)]
    public void VirtualPathsAreRecognized(string path, bool expected) => Assert.Equal(expected, HostPaths.IsVirtual(path));
}
