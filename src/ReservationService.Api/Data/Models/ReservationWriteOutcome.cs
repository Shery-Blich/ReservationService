namespace ReservationService.Api.Data.Models;

public enum ReservationWriteOutcome
{
    Created,
    Updated,
    Duplicate,
    StaleIgnored
}
