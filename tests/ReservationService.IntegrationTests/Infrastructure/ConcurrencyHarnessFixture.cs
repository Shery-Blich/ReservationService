namespace ReservationService.IntegrationTests.Infrastructure;

public sealed class ConcurrencyHarnessFixture : IAsyncLifetime
{
    public ThreeInstanceHarness Harness { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Harness = await ThreeInstanceHarness.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (Harness is not null)
        {
            await Harness.DisposeAsync();
        }
    }
}
