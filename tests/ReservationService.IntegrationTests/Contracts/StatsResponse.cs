namespace ReservationService.IntegrationTests.Contracts;

public sealed record StatsResponse
{
    public required string SupplierId { get; init; }
    public required int Ingested { get; init; }
    public required int Throttled { get; init; }
}
