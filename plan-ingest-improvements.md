# Plan: Ingest pipeline hardening (throttle latch, atomic DAL step, storage format, cleanup)

Scope of this plan only — do not touch anything outside what's listed below. Originated from a full-repo review; each item was discussed and confirmed before being added here.

## 1. Drop `InvalidCount` tracking entirely

Invalid requests never reach the DB today in any meaningful sense (no reservation write), so tracking them in `SupplierStats` isn't worth keeping. `InvalidCount` is also never exposed by the stats endpoint already.

**Files:**
- `src/ReservationService.Api/Data/DatabaseInitializer.cs` — remove the `InvalidCount` column from the `SupplierStats` table definition.
- `src/ReservationService.Api/Data/SupplierStatsWriter.cs` — remove `IncrementInvalidAsync` and the `invalidDelta` parameter/column from the upsert.
- `src/ReservationService.Api/Data/Repositories/ISupplierStatsRepository.cs` / `SupplierStatsRepository.cs` — remove `IncrementInvalidAsync`.
- `src/ReservationService.Api/Data/Models/SupplierStatsRecord.cs` — drop the `InvalidCount` property.
- `src/ReservationService.Api/Features/Ingest/IngestService.cs` — remove the call site (superseded anyway by item 2's reordering, since invalid requests won't touch any repository at all going forward).

**Tests to update:**
- `tests/.../Infrastructure/SupplierStatsRow.cs` — drop `InvalidCount`.
- `tests/.../Infrastructure/ReservationDbAssertions.cs` — drop `InvalidCount` from the `SupplierStats` select list.
- `tests/.../IngestValidationTests.cs` — `MissingRoomId_Returns400AndIncrementsInvalidCount` loses its `InvalidCount` assertion and should be renamed (e.g. `MissingRoomId_Returns400`); `InvalidRequests_DoNotAffectIngestedOrThrottledCount` loses its `InvalidCount` assertion but keeps the `Ingested`/`Throttled` ones.
- `tests/.../MixedOutcomeReconciliationTests.cs` — drop the `InvalidCount` assertion (see item 4 below for the other change needed in this same test).

## 2. Reorder the ingest pipeline so invalid requests never touch the DB, and the throttle DB write only happens once, atomically with the reservation write

**Problem being fixed:** today, the throttle counter is incremented in its own DB transaction *before* the request body is validated — so a request that ultimately fails validation (400) still consumes the supplier's rate-limit budget. Separately, the throttle-evaluate step and the reservation-upsert step are two independent transactions/connections, so they aren't atomic with each other.

**New order of operations in `IngestService.IngestAsync`:**
1. Parse JSON → 400 on failure (unchanged).
2. Extract/normalize `supplierId` → 400 on failure (unchanged).
3. Compute the current throttle bucket id from `TimeProvider` (same formula as today, just moved earlier).
4. Check the in-memory throttle latch (item 3) for this supplier/bucket — if it says "already known blocked for this window," return 429 immediately. No DB access of any kind happens on this path.
5. Validate the remaining fields (business-value validation only, per item 4's simplification) → 400 on failure, no DB access at all.
6. Only now, open a single DB connection/transaction that does both of the following atomically:
   - Increment the throttle bucket counter, read the previous bucket, evaluate against the threshold.
   - If over threshold: increment `ThrottledCount`, commit, mark the in-memory latch for this supplier/bucket, return 429. The reservation is **not** written.
   - If under threshold: upsert the reservation row, increment `IngestedCount`, commit, return the outcome (201/200).

This means the throttle DB counter and the `IngestedCount`/`ThrottledCount` stats are updated in exactly one transaction per request, and only for requests that passed validation — never for malformed/invalid ones.

**Note on ordering (explicit design decision, not incidental):** the in-memory latch check happens *before* field validation. So once a supplier is latched as blocked for the current window, every subsequent request from them gets 429 immediately — even one with an otherwise-invalid body — rather than 400. This is intentional: once blocked, there's no reason to spend any CPU on validating a request that's going to be rejected regardless.

**Files:**
- `src/ReservationService.Api/Features/Ingest/IngestService.cs` — restructure per the order above.
- `src/ReservationService.Api/Data/Repositories/IThrottleRepository.cs` / `ThrottleRepository.cs` — stop opening/committing its own connection+transaction; accept an ambient connection+transaction supplied by the caller so it can participate in the shared transaction from step 6.
- `src/ReservationService.Api/Data/Repositories/IReservationRepository.cs` / `ReservationRepository.cs` — same: accept an ambient connection+transaction instead of opening its own.
- `src/ReservationService.Api/Data/ISqliteConnectionFactory.cs` / `SqliteConnectionFactory.cs` — no interface change expected; `IngestService` will call `CreateOpenConnection()` once per request and own the transaction lifetime.

**Tests to update (behavior-reversing, not mechanical):**
- `tests/.../SupplierIdShortCircuitTests.cs` — `InvalidBodyWithValidSupplierId_StillCountsTowardThrottleWindow` currently asserts that 100 invalid bodies + 1 more trips the throttle. Under the new design this must never throttle (invalid bodies never reach the DAL). Rewrite to assert the opposite — e.g. rename to `InvalidBodyWithValidSupplierId_NeverCountsTowardThrottleWindow` and assert all 150+ attempts return 400, never 429.
- `tests/.../MixedOutcomeReconciliationTests.cs` — `MixedSequenceOfOutcomes_ReconcilesExactlyAgainstStats` was tuned against the *old* semantics: 5 valid DAL-touching requests + 3 invalid (which used to also count toward the throttle bucket) + 92 fill requests = 100 bucket touches, so the 101st (overflow) request was the one that tripped the throttle. Under the new semantics the 3 invalid requests no longer count toward the bucket at all, so the fill loop must go from **92 → 95** iterations (5 + 95 = 100, making the overflow request the true 101st DAL touch), and the expected `Ingested` stat changes from **97 → 100** (`Throttled` stays `1`).

## 3. Add a per-instance in-memory throttle latch

**Problem being fixed:** every single request currently hits the DB to check/increment the throttle counter, even long after a supplier is already known to be over the limit for the current window — so a supplier hammering one instance with (say) 10,000 requests/minute produces roughly 10,000 DB writes, all but ~100 of them pointless, since once the combined count crosses the threshold it can only stay crossed for the rest of that window (counts are monotonic within a bucket).

**Design:** a new singleton component holds, per supplier, the id of the bucket it last confirmed (via the DB, in step 6 of item 2) is over the threshold. On each request, before touching the DB, the pipeline asks this component "is this supplier already known-blocked for the current bucket?" — if yes, reject immediately with no DB access; if no (or the bucket has since rolled over), proceed to the normal DB-backed check, and if that check comes back over threshold, tell this component to remember it for that bucket.

Because this state is per-process/in-memory, each running instance independently discovers the block the first time DB-backed evaluation crosses the threshold for it, then self-latches — it does not attempt to share this cached state across instances (only the underlying DB counter is shared). Bucket-scoping matters: the latch must be checked against the *current* bucket id, not treated as a permanent flag, so that a fresh window (bucket rollover) always re-checks the DB, matching the existing rollover test's expectation that a supplier can send successfully again once the window advances.

**Files:**
- New: `src/ReservationService.Api/Features/Ingest/IThrottleLatch.cs`, `ThrottleLatch.cs` — a small thread-safe (`ConcurrentDictionary`-backed) supplier → last-known-blocked-bucket-id cache, with an `IsBlocked(supplierId, bucketId)` query and a `MarkBlocked(supplierId, bucketId)` update.
- `src/ReservationService.Api/Program.cs` — register `IThrottleLatch` as a singleton.
- `src/ReservationService.Api/Features/Ingest/IngestService.cs` — call it per item 2's ordering.

**Expected behavior change to `ThrottledCount` — confirmed with you already:** this stat now means "number of times an instance's DB check independently discovered the supplier was over the limit," not "number of 429 responses served." Under a serial/sequential hammering pattern against one instance it converges to 1 per instance (matches your 10k-requests example: 1 instance → `Throttled` ends at 1; 3 instances → ends at 3, assuming traffic reaches all three). Under a genuinely concurrent burst, a small number of requests already in flight past the latch check before it was set can still reach the DB, so the number can run a bit higher than the instance count.

**Tests to update:**
- `tests/.../ConcurrencyTests.cs` — `RogueSupplierSplitAcrossInstances_IsCappedAtCombinedLimitNotPerInstance` currently asserts `stats.Throttled` exactly equals the number of 429 responses served. Change this to a range assertion: `stats.Throttled` between 1 and 10 inclusive (confirmed bound), rather than exact equality with the 429 count. The `successCount <= 100` / `>= 99` assertions are unaffected.
- `tests/.../ThrottleWindowTests.cs` — `ThrottledCount_OnlyIncrementsOnActualThrottleRejection` sends exactly one extra request past the limit on a single instance, so it should still pass unchanged with the exact `Throttled == 1` assertion; confirm during implementation rather than assume.

## 4. Replace manual `JsonElement` walking with `JsonSerializer.Deserialize` into a DTO

**Problem being fixed:** `IngestService` currently parses the body into a `JsonDocument` and manually walks each property (`TryGetProperty` + `ValueKind` checks) to extract and validate every field — around 150 lines to do what built-in deserialization mostly already does.

**Design:** deserialize the body once into a new internal DTO with loosely-typed nullable properties (`string?` for `supplierId`/`reservationId`/`roomId`/`checkIn`/`checkOut`/`updatedAtUtc`, `decimal?` for `price`), via `JsonSerializer.DeserializeAsync`. Catch `JsonException` from that call and return the existing single generic 400 (malformed JSON, or a field with the structurally wrong JSON type — e.g. `supplierId` sent as a number). Then run the existing business-value validation (blank/whitespace checks, trim+uppercase normalization, the explicit-UTC-offset regex + parse, non-negative price + rounding, `checkIn < checkOut`) against the DTO's properties instead of against raw `JsonElement`s — this part keeps collecting *all* simultaneous business-value errors into one response exactly like today.

**Accepted, scoped-down tradeoff (confirmed with you):** if a body has *multiple* fields with the wrong JSON *type* at once (e.g. both `supplierId` and `price` sent as the wrong kind of value), you'll get one generic structural error instead of a message naming each offending field — `JsonSerializer` throws on the first type mismatch it hits and stops. Multiple simultaneous *value*-level problems (missing field, bad date format, negative price, `checkIn >= checkOut`, etc.) are unaffected and still all reported together, since those all run after a successful deserialization. No existing test asserts on which specific field name appears in a type-mismatch error, so this doesn't break test coverage — confirm during implementation that this still holds.

**Files:**
- New: `src/ReservationService.Api/Features/Ingest/IngestRequestBody.cs` — the loosely-typed DTO.
- `src/ReservationService.Api/Features/Ingest/IngestService.cs` — replace the `JsonDocument`/`JsonElement` extraction helpers (`TryExtractNormalizedSupplierId`, `ExtractNormalizedIdentifier`, `ExtractUtcDateTime`, `ExtractNonNegativePrice`'s property-lookup portions) with equivalents that read from the DTO's already-typed properties; keep the normalization/format/range logic itself.
- `src/ReservationService.Api/Features/Ingest/ParsedReservationFields.cs` — unchanged in shape; remains the "validated" output type.

## 5. Replace the dummy no-op `UPDATE` existence check with a real `SELECT`

**Problem being fixed:** `ReservationRepository`'s duplicate-classification step runs an `UPDATE ... SET RoomId = RoomId WHERE ...` purely to use its affected-row-count as an existence check — a real write statement whose only purpose is to avoid writing anything, which is confusing to read and unnecessary now that this step is already running inside a shared transaction (item 2) regardless of statement type.

**Files:**
- `src/ReservationService.Api/Data/Repositories/ReservationRepository.cs` — replace `DuplicateCheckSql`'s no-op `UPDATE` with a `SELECT`-based existence check (matching the same `WHERE` predicate) executed via a scalar query instead of `ExecuteAsync`'s affected-row-count.

No test changes expected here — this is an internal implementation detail with no observable behavior change.

## 6. Move date/time storage from formatted ISO-8601 strings to Unix milliseconds

**Decision (confirmed with you):** store `CheckIn`, `CheckOut`, and `UpdatedAtUtc` as `INTEGER` (Unix milliseconds since epoch) instead of formatted `TEXT` strings. Chosen over `DateTime.Ticks` for consistency with `ThrottleWindowCounters.BucketId`'s existing epoch-based convention and because it's a broadly recognized format that's readable/convertible by any tool, including SQLite's own `datetime()`/`strftime()` for ad-hoc debugging — at the accepted cost of truncating anything finer than millisecond precision (today's string format preserves full 100ns precision, and the ingest validation regex technically allows a supplier to send that much). This is written down as an explicit disagree-with-Claude point in the README (see below), including the fallback: if a real need for sub-millisecond precision ever surfaces, switch this column to ticks.

**Files:**
- `src/ReservationService.Api/Data/SqliteDateTimeFormat.cs` — `ToStorageValue`/`FromStorageValue` convert `DateTime` ↔ `long` (Unix milliseconds) instead of a formatted string.
- `src/ReservationService.Api/Data/DatabaseInitializer.cs` — change `Reservations.CheckIn`, `CheckOut`, `UpdatedAtUtc` column types from `TEXT` to `INTEGER` in the schema DDL.
- `src/ReservationService.Api/Data/Repositories/ReservationRepository.cs` — `ReservationSqlParameters`'s `CheckIn`/`CheckOut`/`UpdatedAtUtc` become `long` instead of `string`; the comparison/ordering SQL logic (`<>`, `>=`) is unchanged in shape, now numeric instead of lexical.
- `README.md` — add a bullet under "Where I changed direction from what Claude Code suggested (and vice versa)": Claude recommended `DateTime.Ticks` for lossless precision matching what's stored today; you chose Unix milliseconds instead, prioritizing a recognized convention and human/tool readability, on the assumption that sub-millisecond precision is never meaningful for a reservation timestamp — with a note to switch to ticks if that assumption is ever violated in practice.

**Operational note (no code change, just awareness):** there's no migration mechanism in this project — any existing local `reservationservice.db` file predates this schema and must be deleted before running against the new code, since `CREATE TABLE IF NOT EXISTS` won't alter an existing table's column types.

**Tests to update:**
- `tests/.../Infrastructure/ReservationRow.cs` — `CheckIn`/`CheckOut`/`UpdatedAtUtc` become `long` instead of `string`.
- `tests/.../NormalizationTests.cs` — `NonZeroOffsetTimestamp_ParsesToSameInstantAsEquivalentZOffset`, `NegativeOffsetTimestamp_ParsesToSameInstantAsEquivalentZOffset` currently do `DateTimeOffset.Parse(row.CheckIn/UpdatedAtUtc)`; convert to reading the `long` millisecond value directly (e.g. via `DateTimeOffset.FromUnixTimeMilliseconds`).
- `tests/.../IngestOutcomeTests.cs` — same conversion needed wherever `row.UpdatedAtUtc` is parsed as a string today.

## Verification

After all of the above, run the full test suite (`dotnet test`) including the 3-process `ConcurrencyTests`, which require `dotnet build` first since they shell out to the built `ReservationService.Api.dll`. Iterate until everything is green, updating any test expectations this plan didn't anticipate exactly (the arithmetic in `MixedOutcomeReconciliationTests` and the `ConcurrencyTests` range bound are the two most likely to need a second look in practice).
