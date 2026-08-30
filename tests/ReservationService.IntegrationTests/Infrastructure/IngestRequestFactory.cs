using ReservationService.IntegrationTests.Contracts;

namespace ReservationService.IntegrationTests.Infrastructure;

public static class IngestRequestFactory
{
    public static IngestRequest Create(
        string supplierId,
        string reservationId,
        string roomId,
        DateTimeOffset checkIn,
        DateTimeOffset checkOut,
        decimal price,
        DateTimeOffset updatedAtUtc)
    {
        return new IngestRequest
        {
            SupplierId = supplierId,
            ReservationId = reservationId,
            RoomId = roomId,
            CheckIn = checkIn.ToString("O"),
            CheckOut = checkOut.ToString("O"),
            Price = price,
            UpdatedAtUtc = updatedAtUtc.ToString("O")
        };
    }

    public static IngestRequest CreateDefault(string supplierId, string reservationId)
    {
        var baseline = new DateTimeOffset(2026, 1, 10, 0, 0, 0, TimeSpan.Zero);

        return Create(
            supplierId,
            reservationId,
            roomId: $"ROOM-{Guid.NewGuid():N}",
            checkIn: baseline,
            checkOut: baseline.AddDays(2),
            price: 100.00m,
            updatedAtUtc: baseline);
    }
}
