namespace ReservationService.Api.Data.Models;

public sealed record ReservationRecord
{
    public required string SupplierId { get; init; }

    public required string ReservationId { get; init; }

    public required string RoomId { get; init; }

    public required DateTime CheckIn { get; init; }

    public required DateTime CheckOut { get; init; }

    public required decimal Price { get; init; }

    public required DateTime UpdatedAtUtc { get; init; }
}
