using System.Text.Json;

namespace ReservationService.IntegrationTests.Infrastructure;

public static class JsonOptions
{
    public static readonly JsonSerializerOptions Default = new(JsonSerializerDefaults.Web);
}
