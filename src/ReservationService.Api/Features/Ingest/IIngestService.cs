namespace ReservationService.Api.Features.Ingest;

public interface IIngestService
{
    Task<IResult> IngestAsync(IngestRequestBody body, CancellationToken cancellationToken);
}
