using System.Globalization;

namespace ChopItUp.Core.Storage;

/// <summary>The single writer and reader for every persisted timestamp.</summary>
public static class Timestamps
{
    public static string Stamp(DateTimeOffset at) => at.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

    public static DateTimeOffset Parse(string stamp) =>
        DateTimeOffset.Parse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal).ToUniversalTime();
}
