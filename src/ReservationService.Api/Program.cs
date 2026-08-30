using Microsoft.AspNetCore.Diagnostics;
using ReservationService.Api.Data;
using ReservationService.Api.Data.Repositories;
using ReservationService.Api.Features.Ingest;
using ReservationService.Api.Features.Stats;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ISqliteConnectionFactory, SqliteConnectionFactory>();
builder.Services.AddSingleton<IDatabaseInitializer, DatabaseInitializer>();
builder.Services.AddScoped<IReservationRepository, ReservationRepository>();
builder.Services.AddScoped<IThrottleRepository, ThrottleRepository>();
builder.Services.AddScoped<ISupplierStatsRepository, SupplierStatsRepository>();
builder.Services.AddSingleton<IThrottleLatch, ThrottleLatch>();

builder.Services.AddIngestFeature();
builder.Services.AddStatsFeature();

var app = builder.Build();

app.UseExceptionHandler(new ExceptionHandlerOptions
{
    StatusCodeSelector = exception => exception is BadHttpRequestException badHttpRequestException
        ? badHttpRequestException.StatusCode
        : StatusCodes.Status500InternalServerError
});
app.UseStatusCodePages();

using (var startupScope = app.Services.CreateScope())
{
    var databaseInitializer = startupScope.ServiceProvider.GetRequiredService<IDatabaseInitializer>();
    await databaseInitializer.InitializeAsync();
}

app.MapIngestEndpoint();
app.MapStatsEndpoint();

app.Run();

