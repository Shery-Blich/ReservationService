using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ReservationService.IntegrationTests.Contracts;
using ReservationService.IntegrationTests.Infrastructure;

namespace ReservationService.IntegrationTests;

public sealed class IngestOutcomeTests : IClassFixture<ReservationApiFactory>
{
    private readonly HttpClient client;
    private readonly string databasePath;

    public IngestOutcomeTests(ReservationApiFactory factory)
    {
        client = factory.CreateClient();
        databasePath = factory.DatabasePath;
    }

    [Fact]
    public async Task NewReservation_ReturnsCreatedAndPersistsRow()
    {
        var supplierId = TestIdentifiers.GuidLike();
        var reservationId = TestIdentifiers.GuidLike();
        var request = IngestRequestFactory.CreateDefault(supplierId, reservationId);

        var response = await client.PostIngestAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<IngestResponse>(JsonOptions.Default);
        Assert.Equal("created", body!.Status);
        Assert.Equal(supplierId.Trim().ToUpperInvariant(), body.SupplierId);
        Assert.Equal(reservationId.Trim().ToUpperInvariant(), body.ReservationId);
        var row = await ReservationDbAssertions.FindReservationAsync(databasePath, supplierId.ToUpperInvariant(), reservationId.ToUpperInvariant());
        Assert.NotNull(row);
        Assert.Equal(request.RoomId.ToUpperInvariant(), row!.RoomId);
        Assert.Equal(100.00m, row.Price, 2);
    }

    [Fact]
    public async Task ExactResend_ReturnsDuplicateAndDoesNotChangeStoredUpdatedAtUtc()
    {
        var supplierId = TestIdentifiers.GuidLike();
        var reservationId = TestIdentifiers.GuidLike();
        var original = IngestRequestFactory.CreateDefault(supplierId, reservationId);
        await client.PostIngestAsync(original);
        var resend = original with { UpdatedAtUtc = DateTimeOffset.UtcNow.AddDays(30).ToString("O") };

        var response = await client.PostIngestAsync(resend);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<IngestResponse>(JsonOptions.Default);
        Assert.Equal("duplicate", body!.Status);
        var row = await ReservationDbAssertions.FindReservationAsync(databasePath, supplierId.ToUpperInvariant(), reservationId.ToUpperInvariant());
        Assert.NotNull(row);
        var originalInstant = DateTimeOffset.Parse(original.UpdatedAtUtc).UtcDateTime;
        var storedInstant = DateTimeOffset.Parse(row!.UpdatedAtUtc).UtcDateTime;
        Assert.Equal(originalInstant, storedInstant);
    }

    [Fact]
    public async Task ChangedFieldsWithNewerUpdatedAtUtc_ReturnsUpdatedAndOverwritesRow()
    {
        var supplierId = TestIdentifiers.GuidLike();
        var reservationId = TestIdentifiers.GuidLike();
        var original = IngestRequestFactory.CreateDefault(supplierId, reservationId);
        await client.PostIngestAsync(original);
        var newerTimestamp = DateTimeOffset.Parse(original.UpdatedAtUtc).AddDays(1);
        var update = original with { Price = 250.50m, UpdatedAtUtc = newerTimestamp.ToString("O") };

        var response = await client.PostIngestAsync(update);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<IngestResponse>(JsonOptions.Default);
        Assert.Equal("updated", body!.Status);
        var row = await ReservationDbAssertions.FindReservationAsync(databasePath, supplierId.ToUpperInvariant(), reservationId.ToUpperInvariant());
        Assert.NotNull(row);
        Assert.Equal(250.50m, row!.Price, 2);
        Assert.Equal(newerTimestamp.UtcDateTime, DateTimeOffset.Parse(row.UpdatedAtUtc).UtcDateTime);
    }

    [Fact]
    public async Task ChangedFieldsWithEqualUpdatedAtUtc_ReturnsUpdated()
    {
        var supplierId = TestIdentifiers.GuidLike();
        var reservationId = TestIdentifiers.GuidLike();
        var original = IngestRequestFactory.CreateDefault(supplierId, reservationId);
        await client.PostIngestAsync(original);
        var sameTimestampDifferentRoom = original with { RoomId = $"ROOM-{Guid.NewGuid():N}" };

        var response = await client.PostIngestAsync(sameTimestampDifferentRoom);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<IngestResponse>(JsonOptions.Default);
        Assert.Equal("updated", body!.Status);
        var row = await ReservationDbAssertions.FindReservationAsync(databasePath, supplierId.ToUpperInvariant(), reservationId.ToUpperInvariant());
        Assert.Equal(sameTimestampDifferentRoom.RoomId.ToUpperInvariant(), row!.RoomId);
    }

    [Fact]
    public async Task ChangedFieldsWithOlderUpdatedAtUtc_ReturnsStaleIgnoredAndDoesNotChangeRow()
    {
        var supplierId = TestIdentifiers.GuidLike();
        var reservationId = TestIdentifiers.GuidLike();
        var original = IngestRequestFactory.CreateDefault(supplierId, reservationId);
        await client.PostIngestAsync(original);
        var olderTimestamp = DateTimeOffset.Parse(original.UpdatedAtUtc).AddDays(-1);
        var staleUpdate = original with { Price = 999.99m, RoomId = $"ROOM-{Guid.NewGuid():N}", UpdatedAtUtc = olderTimestamp.ToString("O") };

        var response = await client.PostIngestAsync(staleUpdate);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<IngestResponse>(JsonOptions.Default);
        Assert.Equal("stale_ignored", body!.Status);
        var row = await ReservationDbAssertions.FindReservationAsync(databasePath, supplierId.ToUpperInvariant(), reservationId.ToUpperInvariant());
        Assert.Equal(100.00m, row!.Price, 2);
        Assert.Equal(original.RoomId.ToUpperInvariant(), row.RoomId);
    }

    [Fact]
    public async Task NewReservationIdForExistingSupplier_ReturnsCreated()
    {
        var supplierId = TestIdentifiers.GuidLike();
        var firstReservationId = TestIdentifiers.GuidLike();
        var secondReservationId = TestIdentifiers.GuidLike();
        await client.PostIngestAsync(IngestRequestFactory.CreateDefault(supplierId, firstReservationId));
        var secondRequest = IngestRequestFactory.CreateDefault(supplierId, secondReservationId);

        var response = await client.PostIngestAsync(secondRequest);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var firstRow = await ReservationDbAssertions.FindReservationAsync(databasePath, supplierId.ToUpperInvariant(), firstReservationId.ToUpperInvariant());
        var secondRow = await ReservationDbAssertions.FindReservationAsync(databasePath, supplierId.ToUpperInvariant(), secondReservationId.ToUpperInvariant());
        Assert.NotNull(firstRow);
        Assert.NotNull(secondRow);
    }

    [Fact]
    public async Task IngestResponseBody_ContainsOnlyStatusSupplierIdReservationId()
    {
        var supplierId = TestIdentifiers.GuidLike();
        var reservationId = TestIdentifiers.GuidLike();
        var request = IngestRequestFactory.CreateDefault(supplierId, reservationId);

        var response = await client.PostIngestAsync(request);

        var document = await response.Content.ReadFromJsonAsync<JsonDocument>();
        var propertyNames = document!.RootElement.EnumerateObject().Select(property => property.Name).OrderBy(name => name).ToArray();
        Assert.Equal(new[] { "reservationId", "status", "supplierId" }, propertyNames);
    }
}
