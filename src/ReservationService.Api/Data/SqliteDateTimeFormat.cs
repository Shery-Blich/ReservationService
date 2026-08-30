using System.Globalization;

namespace ReservationService.Api.Data;

internal static class SqliteDateTimeFormat
{
    private const string Format = "yyyy-MM-ddTHH:mm:ss.fffffffZ";

    public static string ToStorageValue(DateTime value)
        => value.ToUniversalTime().ToString(Format, CultureInfo.InvariantCulture);

    public static DateTime FromStorageValue(string value)
        => DateTime.ParseExact(value, Format, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
}
