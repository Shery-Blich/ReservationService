namespace ReservationService.Api.Data;

internal static class SqliteDateTimeFormat
{
    public static long ToStorageValue(DateTime value)
        => new DateTimeOffset(value.ToUniversalTime(), TimeSpan.Zero).ToUnixTimeMilliseconds();

    public static DateTime FromStorageValue(long value)
        => DateTimeOffset.FromUnixTimeMilliseconds(value).UtcDateTime;
}
