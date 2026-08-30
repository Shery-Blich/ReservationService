using ReservationService.Api.Data.Models;

namespace ReservationService.Api.Data.Repositories;

public interface ISupplierStatsRepository
{
    Task<SupplierStatsRecord?> GetAsync(string supplierId, CancellationToken cancellationToken = default);
}
