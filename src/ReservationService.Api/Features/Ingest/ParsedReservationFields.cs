namespace ReservationService.Api.Features.Ingest;

internal sealed record ParsedReservationFields(
    string ReservationId,
    string RoomId,
    DateTime CheckIn,
    DateTime CheckOut,
    decimal Price,
    DateTime UpdatedAtUtc);
