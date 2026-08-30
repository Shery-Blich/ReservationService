namespace ReservationService.IntegrationTests.Infrastructure;

public sealed class ControllableTimeProvider : TimeProvider
{
    private DateTimeOffset utcNow;

    public ControllableTimeProvider(DateTimeOffset initialUtcNow)
    {
        utcNow = initialUtcNow;
    }

    public override DateTimeOffset GetUtcNow()
    {
        return utcNow;
    }

    public void Advance(TimeSpan amount)
    {
        utcNow = utcNow.Add(amount);
    }

    public void JumpToNextBucketStart()
    {
        var currentBucketStart = DateTimeOffset.FromUnixTimeSeconds(utcNow.ToUnixTimeSeconds() / 60 * 60);
        var nextBucketStart = currentBucketStart.AddSeconds(60);
        utcNow = nextBucketStart.AddSeconds(1);
    }
}
