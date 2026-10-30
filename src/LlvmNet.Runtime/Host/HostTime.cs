using System.Diagnostics;
using System.Security.Cryptography;
using static LlvmNet.Runtime.Linux;

namespace LlvmNet.Runtime;

/// <summary>
/// Clocks, sleeping and entropy (plan items LC-09, LC-11). .NET has no per-thread CPU clock, so
/// CLOCK_THREAD_CPUTIME_ID reports the process's CPU time. Time zones come from TZ and the zoneinfo files musl reads.
/// </summary>
public static unsafe partial class HostSyscalls
{
    private static TimeSpan Monotonic => TimeSpan.FromTicks((long)(Stopwatch.GetTimestamp() * (10_000_000.0 / Stopwatch.Frequency)));

    private static TimeSpan? ClockValue(int clock)
    {
        switch (clock)
        {
            case CLOCK_REALTIME or CLOCK_REALTIME_COARSE:
                return DateTime.UtcNow - DateTime.UnixEpoch;
            case CLOCK_MONOTONIC or CLOCK_MONOTONIC_RAW or CLOCK_MONOTONIC_COARSE or CLOCK_BOOTTIME:
                return Monotonic;
            case CLOCK_PROCESS_CPUTIME_ID or CLOCK_THREAD_CPUTIME_ID:
                using (Process process = Process.GetCurrentProcess()) return process.TotalProcessorTime;
            default:
                return null;
        }
    }

    private static long ClockTime(int clock, Timespec* result)
    {
        if (ClockValue(clock) is not TimeSpan value) return -EINVAL;
        *result = Timespec.From(value);
        return 0;
    }

    private static long ClockResolution(int clock, Timespec* result)
    {
        if (ClockValue(clock) is null) return -EINVAL;
        if (result != null) *result = new Timespec { Seconds = 0, Nanoseconds = 100 };   // the 100 ns tick of .NET clocks
        return 0;
    }

    private static long Sleep(Timespec* request)
    {
        if (request->Duration is not TimeSpan duration) return -EINVAL;
        Thread.Sleep(duration);
        return 0;
    }

    private static long ClockSleep(int clock, int flags, Timespec* request)
    {
        if (clock is CLOCK_THREAD_CPUTIME_ID || ClockValue(clock) is not TimeSpan now) return -EINVAL;
        if (request->Duration is not TimeSpan duration) return -EINVAL;
        if ((flags & TIMER_ABSTIME) != 0) duration -= now;
        if (duration > TimeSpan.Zero) Thread.Sleep(duration);
        return 0;
    }

    private static long TimeOfDay(long* result)
    {
        if (result == null) return 0;
        long ticks = (DateTime.UtcNow - DateTime.UnixEpoch).Ticks;
        result[0] = Math.DivRem(ticks, TimeSpan.TicksPerSecond, out long remainder);
        result[1] = remainder / 10;
        return 0;
    }

    private static long Time(long* result)
    {
        long seconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (result != null) *result = seconds;
        return seconds;
    }

    private static long Random(byte* buffer, long length)
    {
        if (length < 0) return -EINVAL;
        int count = (int)Math.Min(length, 33554431);
        RandomNumberGenerator.Fill(new Span<byte>(buffer, count));
        return count;
    }
}
