using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace LlvmNet.Runtime;

/// <summary>
/// Zoneinfo for hosts without it. musl reads time zones from TZif files (/etc/localtime and the zoneinfo directories),
/// which Linux and macOS hosts have and Windows does not. Where the host has no such file, the host layer serves a TZif
/// image generated from .NET's TimeZoneInfo: the transitions .NET knows between 1900 and 2037, each with its UTC offset
/// and daylight flag, and a POSIX rule footer for later years. Abbreviations are numeric (+01, -0330), the form tzdata
/// itself uses for zones without a conventional name, because .NET exposes none.
/// LLVMNET_ZONEINFO=managed serves generated images even where the host has files (the conformance test compares
/// the two); LLVMNET_ZONEINFO=host never generates one.
/// </summary>
internal static class HostZoneInfo
{
    private const string LocalTime = "/etc/localtime";
    private static readonly string[] directories = ["/usr/share/zoneinfo/", "/share/zoneinfo/", "/etc/zoneinfo/"];
    private static readonly string? mode = Environment.GetEnvironmentVariable("LLVMNET_ZONEINFO");
    private static readonly ConcurrentDictionary<string, byte[]?> images = new();

    /// <summary>The generated TZif image for a zoneinfo path the host cannot serve, or null when the host's own file (or ENOENT) answers.</summary>
    internal static byte[]? Image(string path)
    {
        if (mode == "host") return null;
        string? name = null;
        if (path == LocalTime) name = "";
        else
            foreach (string directory in directories)
                if (path.StartsWith(directory, StringComparison.Ordinal)) { name = path[directory.Length..]; break; }
        if (name is null || mode != "managed" && File.Exists(path)) return null;
        // musl's own restrictions on a TZ name, plus nothing that escapes the directory.
        if (name.Length > 0 && (name.Contains("..", StringComparison.Ordinal) || name.StartsWith('/') || name.Contains('\\'))) return null;
        return images.GetOrAdd(name, static name =>
        {
            try
            {
                return Generate(name.Length == 0 ? TimeZoneInfo.Local : TimeZoneInfo.FindSystemTimeZoneById(name));
            }
            catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
            {
                return null;
            }
        });
    }

    private static readonly DateTimeOffset first = new(1900, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset last = new(2038, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly record struct ZoneState(int Offset, bool Daylight);

    private static ZoneState StateAt(TimeZoneInfo zone, DateTimeOffset moment) =>
        new((int)zone.GetUtcOffset(moment).TotalSeconds, zone.IsDaylightSavingTime(moment));

    /// <summary>A version 2 TZif image of the zone: the transitions between 1900 and 2037, and a POSIX rule for later years.</summary>
    internal static byte[] Generate(TimeZoneInfo zone)
    {
        List<ZoneState> types = [];
        List<(long Time, byte Type)> transitions = [];
        byte TypeOf(ZoneState state)
        {
            int index = types.IndexOf(state);
            if (index < 0) { index = types.Count; types.Add(state); }
            return checked((byte)index);
        }
        // The state before the first transition is type 0. Transitions are found by sampling every week and bisecting
        // each change to the second; no zone changes its clock twice within a week.
        ZoneState previous = StateAt(zone, first);
        TypeOf(previous);
        TimeSpan step = TimeSpan.FromDays(7);
        for (DateTimeOffset low = first, high = first + step; low < last; low = high, high += step)
        {
            if (high > last) high = last;
            ZoneState state = StateAt(zone, high);
            if (state == previous) continue;
            DateTimeOffset before = low, after = high;
            while (after - before > TimeSpan.FromSeconds(1))
            {
                DateTimeOffset middle = before + TimeSpan.FromSeconds(Math.Floor((after - before).TotalSeconds / 2));
                if (StateAt(zone, middle) == previous) before = middle;
                else { after = middle; state = StateAt(zone, middle); }
            }
            transitions.Add((after.ToUnixTimeSeconds(), TypeOf(state)));
            previous = state;
        }
        return Build(transitions, types, Footer(zone, previous));
    }

    /// <summary>tzdata's numeric abbreviation for an offset: +01, -0330, +0545.</summary>
    private static string Abbreviation(int offset)
    {
        int magnitude = Math.Abs(offset), hours = magnitude / 3600, minutes = magnitude / 60 % 60;
        return (offset < 0 ? "-" : "+") + hours.ToString("00", CultureInfo.InvariantCulture) + (minutes == 0 ? "" : minutes.ToString("00", CultureInfo.InvariantCulture));
    }

    /// <summary>h[:mm[:ss]] for a number of seconds.</summary>
    private static string Clock(int seconds)
    {
        var text = new StringBuilder();
        text.Append(seconds / 3600);
        if (seconds % 3600 != 0) text.Append(':').Append((seconds / 60 % 60).ToString("00", CultureInfo.InvariantCulture));
        if (seconds % 60 != 0) text.Append(':').Append((seconds % 60).ToString("00", CultureInfo.InvariantCulture));
        return text.ToString();
    }

    /// <summary>A POSIX offset, which counts west of Greenwich as positive: UTC+1 is -1, UTC-3:30 is 3:30.</summary>
    private static string PosixOffset(int offset) => (offset > 0 ? "-" : "") + Clock(Math.Abs(offset));

    /// <summary>A POSIX Mm.w.d[/time] rule; the time is the wall clock before the transition, as .NET also states it, and 02:00 is the default.</summary>
    private static string PosixRule(TimeZoneInfo.TransitionTime transition)
    {
        string rule = $"M{transition.Month}.{transition.Week}.{(int)transition.DayOfWeek}";
        int time = (int)transition.TimeOfDay.TimeOfDay.TotalSeconds;
        return time == 7200 ? rule : rule + "/" + Clock(time);
    }

    /// <summary>
    /// The POSIX rule musl applies after the last transition: the zone's final adjustment rule when it is a recurring
    /// (month, week, day) rule, otherwise the final standard offset without daylight time.
    /// </summary>
    private static string Footer(TimeZoneInfo zone, ZoneState final)
    {
        TimeZoneInfo.AdjustmentRule[] rules = zone.GetAdjustmentRules();
        if (rules.Length > 0 && rules[^1] is { } rule && rule.DateEnd >= last.Date && rule.DaylightDelta != TimeSpan.Zero &&
            !rule.DaylightTransitionStart.IsFixedDateRule && !rule.DaylightTransitionEnd.IsFixedDateRule)
        {
            int standard = (int)(zone.BaseUtcOffset + rule.BaseUtcOffsetDelta).TotalSeconds;
            int daylight = standard + (int)rule.DaylightDelta.TotalSeconds;
            return $"<{Abbreviation(standard)}>{PosixOffset(standard)}<{Abbreviation(daylight)}>{PosixOffset(daylight)}," +
                $"{PosixRule(rule.DaylightTransitionStart)},{PosixRule(rule.DaylightTransitionEnd)}";
        }
        return $"<{Abbreviation(final.Offset)}>{PosixOffset(final.Offset)}";
    }

    private static byte[] Build(List<(long Time, byte Type)> transitions, List<ZoneState> types, string footer)
    {
        // The abbreviation table, one entry per distinct name.
        List<string> names = [];
        var nameIndex = new byte[types.Count];
        var characters = new StringBuilder();
        for (int index = 0; index < types.Count; index++)
        {
            string name = Abbreviation(types[index].Offset);
            int position = names.IndexOf(name);
            if (position < 0) { position = names.Count; names.Add(name); }
            nameIndex[index] = checked((byte)names.Take(position).Sum(n => n.Length + 1));
        }
        foreach (string name in names) characters.Append(name).Append('\0');
        byte[] abbreviations = Encoding.ASCII.GetBytes(characters.ToString());
        var output = new MemoryStream();
        void Header(int count)
        {
            output.Write("TZif2"u8);
            output.Write(new byte[15]);
            foreach (int value in new[] { 0, 0, 0, count, types.Count, abbreviations.Length }) Write32(value);
        }
        void Write32(long value)
        {
            Span<byte> bytes = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes, (int)value);
            output.Write(bytes);
        }
        void Write64(long value)
        {
            Span<byte> bytes = stackalloc byte[8];
            System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(bytes, value);
            output.Write(bytes);
        }
        void Types()
        {
            for (int index = 0; index < types.Count; index++)
            {
                Write32(types[index].Offset);
                output.WriteByte(types[index].Daylight ? (byte)1 : (byte)0);
                output.WriteByte(nameIndex[index]);
            }
            output.Write(abbreviations);
        }
        // The version 1 block holds the transitions that fit 32 bits; readers of version 2 skip it.
        List<(long Time, byte Type)> narrow = transitions.FindAll(t => t.Time >= int.MinValue && t.Time <= int.MaxValue);
        Header(narrow.Count);
        foreach ((long time, _) in narrow) Write32(time);
        foreach ((_, byte type) in narrow) output.WriteByte(type);
        Types();
        Header(transitions.Count);
        foreach ((long time, _) in transitions) Write64(time);
        foreach ((_, byte type) in transitions) output.WriteByte(type);
        Types();
        output.Write(Encoding.ASCII.GetBytes("\n" + footer + "\n"));
        return output.ToArray();
    }
}
