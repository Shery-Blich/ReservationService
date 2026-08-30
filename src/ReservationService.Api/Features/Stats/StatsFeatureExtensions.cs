namespace ReservationService.Api.Features.Stats;

public static class StatsFeatureExtensions
{
    public static IServiceCollection AddStatsFeature(this IServiceCollection services)
    {
        services.AddScoped<IStatsService, StatsService>();

        return services;
    }

    public static WebApplication MapStatsEndpoint(this WebApplication app)
    {
        app.MapGet("/api/reservations/stats/{supplierId}", HandleGetStatsAsync);

        return app;
    }

    private static async Task<IResult> HandleGetStatsAsync(string? supplierId, IStatsService statsService, CancellationToken cancellationToken)
    {
        var result = await statsService.GetStatsAsync(supplierId, cancellationToken);

        return result.Status switch
        {
            StatsResultStatus.MissingSupplierId => Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "supplierId must not be blank."),
            StatsResultStatus.NotFound => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Supplier not found."),
            _ => Results.Ok(result.Response)
        };
    }
}
