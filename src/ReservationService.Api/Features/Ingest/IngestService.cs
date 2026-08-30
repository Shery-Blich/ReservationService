using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ReservationService.Api.Data.Models;
using ReservationService.Api.Data.Repositories;

namespace ReservationService.Api.Features.Ingest;

public sealed partial class IngestService : IIngestService
{
    private const double ThrottleThreshold = 100.0;

    private const long BucketSizeSeconds = 60;

    private readonly IThrottleRepository throttleRepository;

    private readonly IReservationRepository reservationRepository;

    private readonly ISupplierStatsRepository supplierStatsRepository;

    private readonly TimeProvider timeProvider;

    public IngestService(
        IThrottleRepository throttleRepository,
        IReservationRepository reservationRepository,
        ISupplierStatsRepository supplierStatsRepository,
        TimeProvider timeProvider)
    {
        this.throttleRepository = throttleRepository;
        this.reservationRepository = reservationRepository;
        this.supplierStatsRepository = supplierStatsRepository;
        this.timeProvider = timeProvider;
    }

    public async Task<IResult> IngestAsync(Stream requestBody, CancellationToken cancellationToken)
    {
        JsonDocument document;

        try
        {
            document = await JsonDocument.ParseAsync(requestBody, cancellationToken: cancellationToken);
        }
        catch (JsonException)
        {
            return BuildSingleFieldValidationProblem("body", "The request body must be valid JSON.");
        }

        using (document)
        {
            var root = document.RootElement;

            if (!TryExtractNormalizedSupplierId(root, out var supplierId))
            {
                return BuildSingleFieldValidationProblem("supplierId", "supplierId is required and must be a non-blank string.");
            }

            var throttleResult = await EvaluateThrottleAsync(supplierId, cancellationToken);

            if (throttleResult.IsThrottled)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status429TooManyRequests,
                    title: "Too many requests",
                    detail: $"Supplier '{supplierId}' has exceeded the allowed request rate.");
            }

            if (!TryValidateRemainingFields(root, out var fields, out var errors))
            {
                await supplierStatsRepository.IncrementInvalidAsync(supplierId, cancellationToken);
                return Results.ValidationProblem(errors);
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

            var writeResult = await reservationRepository.UpsertAsync(reservationRecord, cancellationToken);

            return BuildOutcomeResult(writeResult.Outcome, supplierId, fields.ReservationId);
        }
    }

    private async Task<ThrottleEvaluationResult> EvaluateThrottleAsync(string supplierId, CancellationToken cancellationToken)
    {
        var unixTimeSeconds = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        var currentBucketId = unixTimeSeconds / BucketSizeSeconds;
        var previousBucketId = currentBucketId - 1;
        var secondsElapsedIntoCurrentBucket = unixTimeSeconds - (currentBucketId * BucketSizeSeconds);
        var overlapWeight = (BucketSizeSeconds - secondsElapsedIntoCurrentBucket) / (double)BucketSizeSeconds;

        return await throttleRepository.IncrementAndEvaluateAsync(
            supplierId,
            currentBucketId,
            previousBucketId,
            overlapWeight,
            ThrottleThreshold,
            cancellationToken);
    }

    private static bool TryExtractNormalizedSupplierId(JsonElement root, out string supplierId)
    {
        supplierId = string.Empty;

        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!root.TryGetProperty("supplierId", out var supplierIdElement) || supplierIdElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var normalized = NormalizeIdentifier(supplierIdElement.GetString());

        if (normalized is null)
        {
            return false;
        }

        supplierId = normalized;
        return true;
    }

    private static bool TryValidateRemainingFields(JsonElement root, out ParsedReservationFields fields, out IDictionary<string, string[]> errors)
    {
        var errorMap = new Dictionary<string, List<string>>();

        var reservationId = ExtractNormalizedIdentifier(root, "reservationId", errorMap);
        var roomId = ExtractNormalizedIdentifier(root, "roomId", errorMap);
        var checkIn = ExtractUtcDateTime(root, "checkIn", errorMap);
        var checkOut = ExtractUtcDateTime(root, "checkOut", errorMap);
        var updatedAtUtc = ExtractUtcDateTime(root, "updatedAtUtc", errorMap);
        var price = ExtractNonNegativePrice(root, "price", errorMap);

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

    private static string? ExtractNormalizedIdentifier(JsonElement root, string propertyName, Dictionary<string, List<string>> errors)
    {
        if (!root.TryGetProperty(propertyName, out var element) || element.ValueKind != JsonValueKind.String)
        {
            AddError(errors, propertyName, $"{propertyName} is required and must be a non-blank string.");
            return null;
        }

        var normalized = NormalizeIdentifier(element.GetString());

        if (normalized is null)
        {
            AddError(errors, propertyName, $"{propertyName} is required and must be a non-blank string.");
            return null;
        }

        return normalized;
    }

    private static DateTime? ExtractUtcDateTime(JsonElement root, string propertyName, Dictionary<string, List<string>> errors)
    {
        if (!root.TryGetProperty(propertyName, out var element) || element.ValueKind != JsonValueKind.String)
        {
            AddError(errors, propertyName, $"{propertyName} is required and must be an ISO-8601 date-time string with an explicit UTC offset.");
            return null;
        }

        var raw = element.GetString();

        if (string.IsNullOrWhiteSpace(raw) ||
            !OffsetDateTimePattern().IsMatch(raw) ||
            !DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            AddError(errors, propertyName, $"{propertyName} must be an ISO-8601 date-time with an explicit UTC offset (Z or +/-hh:mm).");
            return null;
        }

        return parsed.UtcDateTime;
    }

    private static decimal? ExtractNonNegativePrice(JsonElement root, string propertyName, Dictionary<string, List<string>> errors)
    {
        if (!root.TryGetProperty(propertyName, out var element) || element.ValueKind != JsonValueKind.Number || !element.TryGetDecimal(out var rawPrice))
        {
            AddError(errors, propertyName, $"{propertyName} is required and must be a non-negative number.");
            return null;
        }

        if (rawPrice < 0)
        {
            AddError(errors, propertyName, $"{propertyName} must be non-negative.");
            return null;
        }

        return Math.Round(rawPrice, 2, MidpointRounding.AwayFromZero);
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
}
