namespace SuperSemantics.Vsl;

/// <summary>
/// Wall-clock time as seconds since the Unix epoch, as a double: the same representation vsl-core
/// uses (Python's <c>time.time()</c>), so timestamps hash identically in both languages.
/// </summary>
public static class VslClock
{
    private static TimeProvider _provider = TimeProvider.System;

    /// <summary>The time source. Replace it in tests with a fake <see cref="TimeProvider"/>.</summary>
    public static TimeProvider Provider
    {
        get => _provider;
        set => _provider = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>Now, in seconds since the Unix epoch.</summary>
    public static double Now() => ToUnixSeconds(_provider.GetUtcNow());

    /// <summary>Converts a point in time to seconds since the Unix epoch.</summary>
    public static double ToUnixSeconds(DateTimeOffset value) =>
        (value.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / (double)TimeSpan.TicksPerSecond;
}
