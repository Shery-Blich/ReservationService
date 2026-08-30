namespace ReservationService.Api.Data.Models;

public sealed record SupplierStatsRecord(string SupplierId, long IngestedCount, long ThrottledCount, long InvalidCount);
