using ReservationService.Api.Data.Models;

namespace ReservationService.Api.Data.Repositories;

public interface ISupplierStatsRepository
{
    Task<SupplierStatsRecord?> GetAsync(string supplierId, CancellationToken cancellationToken = default);

    Task IncrementInvalidAsync(string supplierId, CancellationToken cancellationToken = default);
}
