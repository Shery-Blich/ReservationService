namespace ReservationService.Api.Features.Stats;

public interface IStatsService
{
    Task<StatsResult> GetStatsAsync(string? supplierId, CancellationToken cancellationToken = default);
}
