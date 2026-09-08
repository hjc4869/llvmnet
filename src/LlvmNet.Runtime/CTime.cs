using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace LlvmNet.Runtime;

public static unsafe class CTime
{
    [ThreadStatic] private static nint calendar;
    private static readonly nint utcName = Marshal.StringToCoTaskMemUTF8("UTC");
    private static readonly nint localName = Marshal.StringToCoTaskMemUTF8(TimeZoneInfo.Local.StandardName);

    [CExport("time")]
    public static long Time(nint destination)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (destination != 0)
            *(long*)destination = now;
        return now;
    }

    [CExport("clock")]
    public static long Clock()
    {
        using Process process = Process.GetCurrentProcess();
        return process.TotalProcessorTime.Ticks / 10;
    }

    [CExport("difftime")]
    public static double Difftime(long end, long start) => (double)((Int128)end - start);

    [CExport("gettimeofday")]
    public static int Gettimeofday(nint result, nint zone)
    {
        long ticks = DateTime.UtcNow.Ticks - DateTime.UnixEpoch.Ticks;
        *(long*)result = ticks / TimeSpan.TicksPerSecond;
        *(long*)(result + 8) = ticks % TimeSpan.TicksPerSecond / 10;
        if (zone != 0)
            NativeMemory.Clear((void*)zone, 8);
        return 0;
    }

    [CExport("clock_gettime")]
    public static int ClockGettime(int clock, nint result)
    {
        long seconds, nanoseconds;
        if (clock == 0)
        {
            long ticks = DateTime.UtcNow.Ticks - DateTime.UnixEpoch.Ticks;
            seconds = ticks / TimeSpan.TicksPerSecond;
            nanoseconds = ticks % TimeSpan.TicksPerSecond * 100;
        }
        else if (clock == 1)
        {
            long ticks = Stopwatch.GetTimestamp();
            seconds = ticks / Stopwatch.Frequency;
            nanoseconds = (long)((Int128)(ticks % Stopwatch.Frequency) * 1_000_000_000 / Stopwatch.Frequency);
        }
        else if (clock == 2)
        {
            long ticks = Clock();
            seconds = ticks / 1_000_000;
            nanoseconds = ticks % 1_000_000 * 1000;
        }
        else
            return ProcessRuntime.Error(22);
        *(long*)result = seconds;
        *(long*)(result + 8) = nanoseconds;
        return 0;
    }

    [CExport("nanosleep")]
    public static int Nanosleep(nint request, nint remaining)
    {
        long seconds = *(long*)request;
        long nanoseconds = *(long*)(request + 8);
        if (seconds < 0 || nanoseconds is < 0 or >= 1_000_000_000)
            return ProcessRuntime.Error(22);
        Thread.Sleep(TimeSpan.FromSeconds(seconds) + TimeSpan.FromTicks(nanoseconds / 100));
        if (remaining != 0)
            NativeMemory.Clear((void*)remaining, 16);
        return 0;
    }

    [CExport("usleep")]
    public static int Usleep(int microseconds)
    {
        if (microseconds < 0)
            return ProcessRuntime.Error(22);
        Thread.Sleep(TimeSpan.FromTicks(microseconds * 10L));
        return 0;
    }

    [CExport("gmtime")]
    public static nint Gmtime(nint time) => GmtimeR(time, Calendar());
    [CExport("localtime")]
    public static nint Localtime(nint time) => LocaltimeR(time, Calendar());
    [CExport("gmtime_r")]
    public static nint GmtimeR(nint time, nint result) => ConvertTime(time, result, false);
    [CExport("localtime_r")]
    public static nint LocaltimeR(nint time, nint result) => ConvertTime(time, result, true);

    private static nint Calendar()
    {
        if (calendar == 0)
            calendar = (nint)NativeMemory.AllocZeroed(56);
        return calendar;
    }

    private static nint ConvertTime(nint time, nint result, bool local)
    {
        try
        {
            DateTimeOffset date = DateTimeOffset.FromUnixTimeSeconds(*(long*)time);
            if (local)
                date = TimeZoneInfo.ConvertTime(date, TimeZoneInfo.Local);
            Fill(date, result, local);
            return result;
        }
        catch (ArgumentOutOfRangeException) { ProcessRuntime.Error(75); return 0; }
    }

    private static void Fill(DateTimeOffset date, nint result, bool local)
    {
        NativeMemory.Clear((void*)result, 56);
        int* fields = (int*)result;
        fields[0] = date.Second;
        fields[1] = date.Minute;
        fields[2] = date.Hour;
        fields[3] = date.Day;
        fields[4] = date.Month - 1;
        fields[5] = date.Year - 1900;
        fields[6] = (int)date.DayOfWeek;
        fields[7] = date.DayOfYear - 1;
        fields[8] = local && TimeZoneInfo.Local.IsDaylightSavingTime(date) ? 1 : 0;
        *(long*)(result + 40) = (long)date.Offset.TotalSeconds;
        *(nint*)(result + 48) = local ? localName : utcName;
    }

    [CExport("mktime")]
    public static long Mktime(nint value) => MakeTime(value, true);
    [CExport("timegm")]
    public static long Timegm(nint value) => MakeTime(value, false);

    private static long MakeTime(nint value, bool local)
    {
        try
        {
            int* fields = (int*)value;
            DateTime date = new DateTime(fields[5] + 1900, 1, 1).AddMonths(fields[4]).AddDays(fields[3] - 1)
                .AddHours(fields[2]).AddMinutes(fields[1]).AddSeconds(fields[0]);
            DateTime utc = local ? TimeZoneInfo.ConvertTimeToUtc(date, TimeZoneInfo.Local) : DateTime.SpecifyKind(date, DateTimeKind.Utc);
            var timestamp = new DateTimeOffset(utc);
            Fill(local ? TimeZoneInfo.ConvertTime(timestamp, TimeZoneInfo.Local) : timestamp, value, local);
            return timestamp.ToUnixTimeSeconds();
        }
        catch (ArgumentException) { ProcessRuntime.Error(75); return -1; }
    }

    [CExport("strftime")]
    public static long Strftime(nint destination, long size, nint format, nint calendarTime)
    {
        int* fields = (int*)calendarTime;
        DateTime date = new DateTime(fields[5] + 1900, fields[4] + 1, fields[3], fields[2], fields[1], fields[0]);
        var result = new StringBuilder();
        string pattern = CString.Text(format);
        CultureInfo culture = CultureInfo.InvariantCulture;
        for (int index = 0; index < pattern.Length; index++)
        {
            if (pattern[index] != '%') { result.Append(pattern[index]); continue; }
            if (++index == pattern.Length) return 0;
            if (pattern[index] is 'E' or 'O' && ++index == pattern.Length) return 0;
            char token = pattern[index];
            string? text = token switch
            {
                '%' => "%", 'n' => "\n", 't' => "\t",
                'Y' => date.ToString("yyyy", culture), 'y' => date.ToString("yy", culture),
                'C' => (date.Year / 100).ToString("D2", culture), 'm' => date.ToString("MM", culture),
                'd' => date.ToString("dd", culture), 'e' => date.Day.ToString(culture).PadLeft(2),
                'H' => date.ToString("HH", culture), 'I' => date.ToString("hh", culture),
                'M' => date.ToString("mm", culture), 'S' => date.ToString("ss", culture),
                'p' => date.ToString("tt", culture), 'P' => date.ToString("tt", culture).ToLowerInvariant(),
                'a' => date.ToString("ddd", culture), 'A' => date.ToString("dddd", culture),
                'b' or 'h' => date.ToString("MMM", culture), 'B' => date.ToString("MMMM", culture),
                'j' => date.DayOfYear.ToString("D3", culture), 'w' => ((int)date.DayOfWeek).ToString(culture),
                'u' => (date.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)date.DayOfWeek).ToString(culture),
                'F' => date.ToString("yyyy-MM-dd", culture), 'D' or 'x' => date.ToString("MM/dd/yy", culture),
                'T' or 'X' => date.ToString("HH:mm:ss", culture), 'R' => date.ToString("HH:mm", culture),
                'r' => date.ToString("hh:mm:ss tt", culture),
                'c' => date.ToString("ddd MMM ", culture) + date.Day.ToString(culture).PadLeft(2) + date.ToString(" HH:mm:ss yyyy", culture),
                'V' => ISOWeek.GetWeekOfYear(date).ToString("D2", culture),
                'G' => ISOWeek.GetYear(date).ToString("D4", culture),
                'g' => (ISOWeek.GetYear(date) % 100).ToString("D2", culture),
                'U' => ((date.DayOfYear - 1 + 7 - (int)date.DayOfWeek) / 7).ToString("D2", culture),
                'W' => ((date.DayOfYear - 1 + 7 - ((int)date.DayOfWeek + 6) % 7) / 7).ToString("D2", culture),
                'Z' => CString.Text(*(nint*)(calendarTime + 48)),
                'z' => Zone(*(long*)(calendarTime + 40)),
                _ => throw new NotSupportedException($"Unsupported strftime conversion %{token}")
            };
            result.Append(text);
        }
        byte[] bytes = Encoding.UTF8.GetBytes(result.ToString());
        if (bytes.Length >= size) return 0;
        bytes.CopyTo(new Span<byte>((void*)destination, bytes.Length));
        ((byte*)destination)[bytes.Length] = 0;
        return bytes.Length;
    }

    private static string Zone(long seconds) => (seconds < 0 ? "-" : "+") + (Math.Abs(seconds) / 3600).ToString("D2", CultureInfo.InvariantCulture) + (Math.Abs(seconds) / 60 % 60).ToString("D2", CultureInfo.InvariantCulture);
}