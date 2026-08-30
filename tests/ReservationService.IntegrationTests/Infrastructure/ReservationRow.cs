namespace ReservationService.IntegrationTests.Infrastructure;

public sealed record ReservationRow
{
    public required string SupplierId { get; init; }
    public required string ReservationId { get; init; }
    public required string RoomId { get; init; }
    public required string CheckIn { get; init; }
    public required string CheckOut { get; init; }
    public required decimal Price { get; init; }
    public required string UpdatedAtUtc { get; init; }
}
