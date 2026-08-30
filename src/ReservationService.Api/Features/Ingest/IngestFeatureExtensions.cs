namespace ReservationService.Api.Features.Ingest;

public static class IngestFeatureExtensions
{
    public static IServiceCollection AddIngestFeature(this IServiceCollection services)
    {
        return services;
    }

    public static WebApplication MapIngestEndpoint(this WebApplication app)
    {
        app.MapPost("/api/reservations/ingest", () => Results.StatusCode(StatusCodes.Status501NotImplemented));

        return app;
    }
}
