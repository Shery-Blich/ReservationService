namespace ReservationService.Api.Data.Models;

public sealed record ThrottleEvaluationResult(int CurrentBucketCount, int PreviousBucketCount, double EstimatedCount, bool IsThrottled);
