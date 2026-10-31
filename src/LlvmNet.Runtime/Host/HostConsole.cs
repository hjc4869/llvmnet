using System.Text;

namespace LlvmNet.Runtime;

/// <summary>Host preparation at program start.</summary>
public static unsafe partial class HostSyscalls
{
    /// <summary>
    /// Portable programs write UTF-8 bytes to the standard streams without translation. A Windows console renders bytes
    /// in its own code page, so a console attached to a standard stream is switched to UTF-8 for the program's lifetime
    /// and restored at exit; redirected streams and other hosts need nothing.
    /// </summary>
    public static void InitializeHost()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            if (!Console.IsOutputRedirected || !Console.IsErrorRedirected)
            {
                Encoding previous = Console.OutputEncoding;
                if (previous.CodePage != 65001)
                {
                    Console.OutputEncoding = new UTF8Encoding(false);
                    AppDomain.CurrentDomain.ProcessExit += (_, _) => Restore(() => Console.OutputEncoding = previous);
                }
            }
            if (!Console.IsInputRedirected)
            {
                Encoding previous = Console.InputEncoding;
                if (previous.CodePage != 65001)
                {
                    Console.InputEncoding = new UTF8Encoding(false);
                    AppDomain.CurrentDomain.ProcessExit += (_, _) => Restore(() => Console.InputEncoding = previous);
                }
            }
        }
        catch (Exception error) when (error is IOException or PlatformNotSupportedException or UnauthorizedAccessException) { }

        static void Restore(Action action)
        {
            try { action(); }
            catch (Exception error) when (error is IOException or PlatformNotSupportedException or UnauthorizedAccessException) { }
        }
    }
}
