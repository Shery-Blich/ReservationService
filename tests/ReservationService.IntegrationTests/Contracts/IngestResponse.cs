namespace ReservationService.IntegrationTests.Contracts;

public sealed record IngestResponse
{
    public required string Status { get; init; }
    public required string SupplierId { get; init; }
    public required string ReservationId { get; init; }
}
