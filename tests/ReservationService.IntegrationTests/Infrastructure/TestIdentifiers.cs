using System.Security.Cryptography;
using System.Text;

namespace ReservationService.IntegrationTests.Infrastructure;

public static class TestIdentifiers
{
    private static long counter;

    public static string GuidLike()
    {
        return Guid.NewGuid().ToString();
    }

    public static string IntegerLike()
    {
        var sequence = Interlocked.Increment(ref counter);
        return $"{DateTime.UtcNow.Ticks}{sequence}";
    }

    public static string HashLike()
    {
        var bytes = Encoding.UTF8.GetBytes(Guid.NewGuid().ToString());
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    public static string MixedAlphanumeric()
    {
        var sequence = Interlocked.Increment(ref counter);
        return $"SUP{sequence}X{Guid.NewGuid():N}"[..16].ToUpperInvariant();
    }
}
