using System.Security.Cryptography;

namespace LlvmNet.Runtime;

/// <summary>
/// Random bytes for getrandom, /dev/urandom and the AT_RANDOM start-vector entry. On Unix hosts they are read from the
/// host's /dev/urandom, so a portable program does not depend on OpenSSL being installed (.NET's RandomNumberGenerator
/// loads it on Linux and aborts without it); Windows has no such device and uses the system generator.
/// </summary>
public static class HostRandom
{
    private static readonly object gate = new();
    private static FileStream? device;
    private static bool deviceUnavailable;

    public static void Fill(Span<byte> buffer)
    {
        if (buffer.Length == 0) return;
        if (!OperatingSystem.IsWindows() && FillFromDevice(buffer)) return;
        RandomNumberGenerator.Fill(buffer);
    }

    private static bool FillFromDevice(Span<byte> buffer)
    {
        lock (gate)
        {
            if (deviceUnavailable) return false;
            try
            {
                device ??= new FileStream("/dev/urandom", FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 0);
                while (buffer.Length > 0)
                {
                    int read = device.Read(buffer);
                    if (read <= 0) throw new IOException("/dev/urandom returned no data");
                    buffer = buffer[read..];
                }
                return true;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                deviceUnavailable = true;
                device?.Dispose();
                device = null;
                return false;
            }
        }
    }
}
