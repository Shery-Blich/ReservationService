namespace ReservationService.Api.Features.Stats;

public sealed record StatsResponse(string SupplierId, long Ingested, long Throttled);
