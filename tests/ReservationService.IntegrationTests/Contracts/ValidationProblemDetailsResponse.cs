namespace ReservationService.IntegrationTests.Contracts;

public sealed record ValidationProblemDetailsResponse
{
    public string? Type { get; init; }
    public string? Title { get; init; }
    public int? Status { get; init; }
    public string? Detail { get; init; }
    public Dictionary<string, string[]>? Errors { get; init; }
}
