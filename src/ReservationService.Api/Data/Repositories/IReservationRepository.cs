using ReservationService.Api.Data.Models;

namespace ReservationService.Api.Data.Repositories;

public interface IReservationRepository
{
    Task<ReservationWriteResult> UpsertAsync(ReservationRecord reservation, CancellationToken cancellationToken = default);
}
