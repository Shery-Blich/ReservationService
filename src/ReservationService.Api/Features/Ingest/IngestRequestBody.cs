namespace ReservationService.Api.Features.Ingest;

internal sealed record IngestRequestBody
{
    public string? SupplierId { get; init; }

    public string? ReservationId { get; init; }

    public string? RoomId { get; init; }

    public string? CheckIn { get; init; }

    public string? CheckOut { get; init; }

    public decimal? Price { get; init; }

    public string? UpdatedAtUtc { get; init; }
}
