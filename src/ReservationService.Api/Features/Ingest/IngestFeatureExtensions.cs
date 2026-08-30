namespace ReservationService.Api.Features.Ingest;

public static class IngestFeatureExtensions
{
    public static IServiceCollection AddIngestFeature(this IServiceCollection services)
    {
        services.AddScoped<IIngestService, IngestService>();

        return services;
    }

    public static WebApplication MapIngestEndpoint(this WebApplication app)
    {
        app.MapPost("/api/reservations/ingest", (IngestRequestBody body, IIngestService ingestService, CancellationToken cancellationToken) =>
            ingestService.IngestAsync(body, cancellationToken));

        return app;
    }
}
