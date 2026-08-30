namespace ReservationService.Api.Features.Stats;

public enum StatsResultStatus
{
    MissingSupplierId,
    NotFound,
    Success
}

public sealed record StatsResult(StatsResultStatus Status, StatsResponse? Response)
{
    public static StatsResult MissingSupplierId()
        => new(StatsResultStatus.MissingSupplierId, Response: null);

    public static StatsResult NotFound()
        => new(StatsResultStatus.NotFound, Response: null);

    public static StatsResult Success(StatsResponse response)
        => new(StatsResultStatus.Success, response);
}
