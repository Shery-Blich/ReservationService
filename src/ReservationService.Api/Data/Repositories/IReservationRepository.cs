using System.Data;
using ReservationService.Api.Data.Models;

namespace ReservationService.Api.Data.Repositories;

public interface IReservationRepository
{
    Task<ReservationWriteResult> UpsertAsync(IDbConnection connection, IDbTransaction transaction, ReservationRecord reservation, CancellationToken cancellationToken = default);
}
