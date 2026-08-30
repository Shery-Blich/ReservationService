using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ReservationService.Api.Data;
using ReservationService.Api.Data.Models;
using ReservationService.Api.Data.Repositories;

namespace ReservationService.Api.Features.Ingest;

public sealed partial class IngestService : IIngestService
{
    private const double ThrottleThreshold = 100.0;

    private const long BucketSizeSeconds = 60;

    private static readonly JsonSerializerOptions RequestBodyJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IThrottleRepository throttleRepository;

    private readonly IReservationRepository reservationRepository;

    private readonly ISqliteConnectionFactory connectionFactory;

    private readonly IThrottleLatch throttleLatch;

    private readonly TimeProvider timeProvider;

    public IngestService(
        IThrottleRepository throttleRepository,
        IReservationRepository reservationRepository,
        ISqliteConnectionFactory connectionFactory,
        IThrottleLatch throttleLatch,
        TimeProvider timeProvider)
    {
        this.throttleRepository = throttleRepository;
        this.reservationRepository = reservationRepository;
        this.connectionFactory = connectionFactory;
        this.throttleLatch = throttleLatch;
        this.timeProvider = timeProvider;
    }

    public async Task<IResult> IngestAsync(Stream requestBody, CancellationToken cancellationToken)
    {
        IngestRequestBody body;

        try
        {
            body = await JsonSerializer.DeserializeAsync<IngestRequestBody>(requestBody, RequestBodyJsonOptions, cancellationToken)
                ?? new IngestRequestBody();
        }
        catch (JsonException)
        {
            return BuildSingleFieldValidationProblem("body", "The request body must be valid JSON.");
        }

        if (!TryNormalizeSupplierId(body.SupplierId, out var supplierId))
        {
            return BuildSingleFieldValidationProblem("supplierId", "supplierId is required and must be a non-blank string.");
        }

        var bucket = ComputeThrottleBucket();

        if (throttleLatch.IsBlocked(supplierId, bucket.CurrentBucketId))
        {
            return BuildThrottledResult(supplierId);
        }

        if (!TryValidateRemainingFields(body, out var fields, out var errors))
        {
            return Results.ValidationProblem(errors);
        }

        return await IngestValidatedReservationThroughGateAsync(supplierId, fields, bucket, cancellationToken);
    }

    private async Task<IResult> IngestValidatedReservationThroughGateAsync(string supplierId, ParsedReservationFields fields, ThrottleBucket bucket, CancellationToken cancellationToken)
    {
        await using var gate = await throttleLatch.AcquireGateAsync(supplierId);

        if (throttleLatch.IsBlocked(supplierId, bucket.CurrentBucketId))
        {
            return BuildThrottledResult(supplierId);
        }

        return await IngestValidatedReservationAsync(supplierId, fields, bucket, cancellationToken);
    }

    private async Task<IResult> IngestValidatedReservationAsync(string supplierId, ParsedReservationFields fields, ThrottleBucket bucket, CancellationToken cancellationToken)
    {
        using var connection = connectionFactory.CreateOpenConnection();
        using var transaction = connection.BeginTransaction();

        var throttleResult = await throttleRepository.IncrementAndEvaluateAsync(
            connection,
            transaction,
            supplierId,
            bucket.CurrentBucketId,
            bucket.PreviousBucketId,
            bucket.OverlapWeight,
            ThrottleThreshold,
            cancellationToken);

        if (throttleResult.IsThrottled)
        {
            transaction.Commit();
            throttleLatch.MarkBlocked(supplierId, bucket.CurrentBucketId);
            return BuildThrottledResult(supplierId);
        }

        var reservationRecord = new ReservationRecord
        {
            SupplierId = supplierId,
            ReservationId = fields.ReservationId,
            RoomId = fields.RoomId,
            CheckIn = fields.CheckIn,
            CheckOut = fields.CheckOut,
            Price = fields.Price,
            UpdatedAtUtc = fields.UpdatedAtUtc
        };

        var writeResult = await reservationRepository.UpsertAsync(connection, transaction, reservationRecord, cancellationToken);

        transaction.Commit();

        return BuildOutcomeResult(writeResult.Outcome, supplierId, fields.ReservationId);
    }

    private ThrottleBucket ComputeThrottleBucket()
    {
        var unixTimeSeconds = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        var currentBucketId = unixTimeSeconds / BucketSizeSeconds;
        var previousBucketId = currentBucketId - 1;
        var secondsElapsedIntoCurrentBucket = unixTimeSeconds - (currentBucketId * BucketSizeSeconds);
        var overlapWeight = (BucketSizeSeconds - secondsElapsedIntoCurrentBucket) / (double)BucketSizeSeconds;

        return new ThrottleBucket(currentBucketId, previousBucketId, overlapWeight);
    }

    private static IResult BuildThrottledResult(string supplierId)
        => Results.Problem(
            statusCode: StatusCodes.Status429TooManyRequests,
            title: "Too many requests",
            detail: $"Supplier '{supplierId}' has exceeded the allowed request rate.");

    private static bool TryNormalizeSupplierId(string? rawValue, out string supplierId)
    {
        var normalized = NormalizeIdentifier(rawValue);
        supplierId = normalized ?? string.Empty;

        return normalized is not null;
    }

    private static bool TryValidateRemainingFields(IngestRequestBody body, out ParsedReservationFields fields, out IDictionary<string, string[]> errors)
    {
        var errorMap = new Dictionary<string, List<string>>();

        var reservationId = ExtractNormalizedIdentifier(body.ReservationId, "reservationId", errorMap);
        var roomId = ExtractNormalizedIdentifier(body.RoomId, "roomId", errorMap);
        var checkIn = ExtractUtcDateTime(body.CheckIn, "checkIn", errorMap);
        var checkOut = ExtractUtcDateTime(body.CheckOut, "checkOut", errorMap);
        var updatedAtUtc = ExtractUtcDateTime(body.UpdatedAtUtc, "updatedAtUtc", errorMap);
        var price = ExtractNonNegativePrice(body.Price, "price", errorMap);

        if (checkIn.HasValue && checkOut.HasValue && checkIn.Value >= checkOut.Value)
        {
            AddError(errorMap, "checkIn", "checkIn must be earlier than checkOut.");
        }

        errors = errorMap.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());

        if (errors.Count > 0)
        {
            fields = default!;
            return false;
        }

        fields = new ParsedReservationFields(reservationId!, roomId!, checkIn!.Value, checkOut!.Value, price!.Value, updatedAtUtc!.Value);
        return true;
    }

    private static string? ExtractNormalizedIdentifier(string? rawValue, string propertyName, Dictionary<string, List<string>> errors)
    {
        var normalized = NormalizeIdentifier(rawValue);

        if (normalized is null)
        {
            AddError(errors, propertyName, $"{propertyName} is required and must be a non-blank string.");
            return null;
        }

        return normalized;
    }

    private static DateTime? ExtractUtcDateTime(string? rawValue, string propertyName, Dictionary<string, List<string>> errors)
    {
        if (string.IsNullOrWhiteSpace(rawValue) ||
            !OffsetDateTimePattern().IsMatch(rawValue) ||
            !DateTimeOffset.TryParse(rawValue, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            AddError(errors, propertyName, $"{propertyName} must be an ISO-8601 date-time with an explicit UTC offset (Z or +/-hh:mm).");
            return null;
        }

        return parsed.UtcDateTime;
    }

    private static decimal? ExtractNonNegativePrice(decimal? rawPrice, string propertyName, Dictionary<string, List<string>> errors)
    {
        if (!rawPrice.HasValue)
        {
            AddError(errors, propertyName, $"{propertyName} is required and must be a non-negative number.");
            return null;
        }

        if (rawPrice.Value < 0)
        {
            AddError(errors, propertyName, $"{propertyName} must be non-negative.");
            return null;
        }

        return Math.Round(rawPrice.Value, 2, MidpointRounding.AwayFromZero);
    }

    private static string? NormalizeIdentifier(string? rawValue)
    {
        var trimmed = rawValue?.Trim();

        return string.IsNullOrEmpty(trimmed) ? null : trimmed.ToUpperInvariant();
    }

    private static void AddError(Dictionary<string, List<string>> errors, string field, string message)
    {
        if (!errors.TryGetValue(field, out var messages))
        {
            messages = [];
            errors[field] = messages;
        }

        messages.Add(message);
    }

    private static IResult BuildSingleFieldValidationProblem(string field, string message)
        => Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });

    private static IResult BuildOutcomeResult(ReservationWriteOutcome outcome, string supplierId, string reservationId)
    {
        var response = new IngestResponse(MapOutcomeStatus(outcome), supplierId, reservationId);
        var statusCode = outcome == ReservationWriteOutcome.Created ? StatusCodes.Status201Created : StatusCodes.Status200OK;

        return Results.Json(response, statusCode: statusCode);
    }

    private static string MapOutcomeStatus(ReservationWriteOutcome outcome) => outcome switch
    {
        ReservationWriteOutcome.Created => "created",
        ReservationWriteOutcome.Updated => "updated",
        ReservationWriteOutcome.Duplicate => "duplicate",
        ReservationWriteOutcome.StaleIgnored => "stale_ignored",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome))
    };

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?(Z|z|[+-]\d{2}:\d{2})$")]
    private static partial Regex OffsetDateTimePattern();

    private readonly record struct ThrottleBucket(long CurrentBucketId, long PreviousBucketId, double OverlapWeight);
}
