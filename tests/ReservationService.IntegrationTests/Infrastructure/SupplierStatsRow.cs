namespace ReservationService.IntegrationTests.Infrastructure;

public sealed record SupplierStatsRow
{
    public required string SupplierId { get; init; }
    public required int IngestedCount { get; init; }
    public required int ThrottledCount { get; init; }
    public required int InvalidCount { get; init; }
}
