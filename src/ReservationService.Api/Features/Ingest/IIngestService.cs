namespace ReservationService.Api.Features.Ingest;

public interface IIngestService
{
    Task<IResult> IngestAsync(Stream requestBody, CancellationToken cancellationToken);
}
