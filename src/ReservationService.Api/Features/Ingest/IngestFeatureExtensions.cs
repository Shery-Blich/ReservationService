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
        app.MapPost("/api/reservations/ingest", (HttpContext httpContext, IIngestService ingestService) =>
            ingestService.IngestAsync(httpContext.Request.Body, httpContext.RequestAborted));

        return app;
    }
}
