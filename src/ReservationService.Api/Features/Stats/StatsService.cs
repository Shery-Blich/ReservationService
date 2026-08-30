using ReservationService.Api.Data.Repositories;

namespace ReservationService.Api.Features.Stats;

public sealed class StatsService : IStatsService
{
    private readonly ISupplierStatsRepository supplierStatsRepository;

    public StatsService(ISupplierStatsRepository supplierStatsRepository)
    {
        this.supplierStatsRepository = supplierStatsRepository;
    }

    public async Task<StatsResult> GetStatsAsync(string? supplierId, CancellationToken cancellationToken = default)
    {
        var trimmedSupplierId = supplierId?.Trim();

        if (string.IsNullOrEmpty(trimmedSupplierId))
        {
            return StatsResult.MissingSupplierId();
        }

        var normalizedSupplierId = trimmedSupplierId.ToUpperInvariant();
        var record = await supplierStatsRepository.GetAsync(normalizedSupplierId, cancellationToken);

        if (record is null)
        {
            return StatsResult.NotFound();
        }

        var response = new StatsResponse(normalizedSupplierId, record.IngestedCount, record.ThrottledCount);

        return StatsResult.Success(response);
    }
}
