namespace ReservationService.Api.Features.Stats;

public static class StatsFeatureExtensions
{
    public static IServiceCollection AddStatsFeature(this IServiceCollection services)
    {
        return services;
    }

    public static WebApplication MapStatsEndpoint(this WebApplication app)
    {
        app.MapGet("/api/reservations/stats/{supplierId}", () => Results.StatusCode(StatusCodes.Status501NotImplemented));

        return app;
    }
}
