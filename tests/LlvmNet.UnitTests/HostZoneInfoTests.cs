using System.Buffers.Binary;
using System.Text;
using LlvmNet.Runtime;

namespace LlvmNet.UnitTests;

/// <summary>
/// The TZif images the host layer generates for hosts without zoneinfo: every instant resolves to the offset and
/// daylight flag TimeZoneInfo reports, and the POSIX footer states tzdata's current rule.
/// </summary>
public sealed class HostZoneInfoTests
{
    private sealed record Image(long[] Times, byte[] Index, (int Offset, bool Daylight)[] Types, string Footer);

    private static Image Parse(byte[] image)
    {
        Assert.Equal("TZif2", Encoding.ASCII.GetString(image, 0, 5));
        int[] Counts(int at) => Enumerable.Range(0, 6).Select(i => BinaryPrimitives.ReadInt32BigEndian(image.AsSpan(at + 20 + 4 * i))).ToArray();
        int[] v1 = Counts(0);
        // isutcnt, isstdcnt, leapcnt, timecnt, typecnt, charcnt: the version 1 block is skipped by its counts.
        int v2 = 44 + v1[0] + v1[1] + 8 * v1[2] + 5 * v1[3] + 6 * v1[4] + v1[5];
        Assert.Equal("TZif2", Encoding.ASCII.GetString(image, v2, 5));
        int[] counts = Counts(v2);
        int position = v2 + 44;
        long[] times = new long[counts[3]];
        for (int i = 0; i < times.Length; i++, position += 8) times[i] = BinaryPrimitives.ReadInt64BigEndian(image.AsSpan(position));
        byte[] index = image[position..(position + counts[3])];
        position += counts[3];
        var types = new (int, bool)[counts[4]];
        for (int i = 0; i < types.Length; i++, position += 6) types[i] = (BinaryPrimitives.ReadInt32BigEndian(image.AsSpan(position)), image[position + 4] != 0);
        position += counts[5];
        string footer = Encoding.ASCII.GetString(image, position, image.Length - position);
        Assert.StartsWith("\n", footer);
        Assert.EndsWith("\n", footer);
        return new Image(times, index, types, footer.Trim('\n'));
    }

    private static TimeZoneInfo? Zone(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (TimeZoneNotFoundException) { return null; }
    }

    [Theory]
    [InlineData("America/New_York", "<-05>5<-04>4,M3.2.0,M11.1.0")]
    [InlineData("Europe/Berlin", "<+01>-1<+02>-2,M3.5.0,M10.5.0/3")]
    [InlineData("Australia/Sydney", "<+10>-10<+11>-11,M10.1.0,M4.1.0/3")]
    [InlineData("America/St_Johns", "<-0330>3:30<-0230>2:30,M3.2.0,M11.1.0")]
    [InlineData("Asia/Kolkata", "<+0530>-5:30")]
    [InlineData("Asia/Tokyo", "<+09>-9")]
    [InlineData("UTC", "<+00>0")]
    public void FooterIsTzdatasCurrentRule(string id, string footer)
    {
        if (Zone(id) is not TimeZoneInfo zone) return;   // the host has no data for this zone
        Assert.Equal(footer, Parse(HostZoneInfo.Generate(zone)).Footer);
    }

    [Theory]
    [InlineData("America/New_York")]
    [InlineData("Europe/Berlin")]
    [InlineData("Australia/Lord_Howe")]
    [InlineData("Pacific/Apia")]
    [InlineData("America/Sao_Paulo")]
    [InlineData("Asia/Kathmandu")]
    public void EveryInstantResolvesAsTimeZoneInfoDoes(string id)
    {
        if (Zone(id) is not TimeZoneInfo zone) return;
        Image image = Parse(HostZoneInfo.Generate(zone));
        Assert.True(image.Times.SequenceEqual(image.Times.Order()), "transitions are chronological");
        var random = new Random(id.Length);
        long start = new DateTimeOffset(1900, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(), end = new DateTimeOffset(2038, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        IEnumerable<long> instants = Enumerable.Range(0, 2000).Select(_ => start + random.NextInt64(end - start))
            .Concat(image.Times).Concat(image.Times.Select(t => t - 1));
        foreach (long instant in instants)
        {
            // As musl reads the image: the type of the last transition at or before the instant, type 0 before the first.
            int at = Array.BinarySearch(image.Times, instant);
            if (at < 0) at = ~at - 1;
            (int offset, bool daylight) = at < 0 ? image.Types[0] : image.Types[image.Index[at]];
            var moment = DateTimeOffset.FromUnixTimeSeconds(instant);
            Assert.Equal((zone.GetUtcOffset(moment).TotalSeconds, zone.IsDaylightSavingTime(moment)), (offset, daylight));
        }
    }

    [Fact]
    public void LocalZoneGenerates()
    {
        Image image = Parse(HostZoneInfo.Generate(TimeZoneInfo.Local));
        Assert.NotEmpty(image.Types);
        Assert.Matches(@"^<[-+]\d{2}(\d{2})?>-?\d+(:\d{2})*(<[-+]\d{2}(\d{2})?>-?\d+(:\d{2})*,M\d+\.\d\.\d(/\S+)?,M\d+\.\d\.\d(/\S+)?)?$", image.Footer);
    }
}
