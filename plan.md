# Supplier Feed Throttle & Dedup Service — Implementation & Architecture Plan

## Audience & how to use this plan

This plan is written for two downstream agents working from the same document:

- **Coding agent** — implements the service against this plan.
- **Testing agent** — writes black-box integration tests against the running API, without seeing the coding agent's implementation.

**Both agents must read `AGENTS.md` at the project root before starting any work.** It defines the coding standards/conventions to follow; this plan does not repeat or override anything in it.

This plan spells out every decision that affects *observable behavior* (HTTP status codes, response bodies, when a write happens, what counts toward throttling/stats) precisely, because the testing agent must be able to derive correct expected outcomes for any test scenario without seeing the implementation. It does not include code, pseudo-code, or file/folder layout — those are the coding agent's call, within the constraints stated here.

---

## Scope & assumptions

These are deliberate scoping decisions made where the spec was silent or ambiguous. The coding agent should carry these into the submission's `README.md` under "assumptions made":

- **Multiple processes, single machine — not multiple machines.** The service must behave correctly when several instances of it run as separate local processes sharing one SQLite database file (tested locally). Genuine multi-machine deployment is explicitly out of scope: SQLite (and LocalDB) are not designed for reliable concurrent access from multiple machines over a network filesystem — that would require swapping to a network-accessible DB engine entirely, which is beyond this exercise's tech constraints. No message queue is used; correctness across instances is achieved entirely through atomic database operations (see Concurrency).
- **Reservation identity is `(supplierId, reservationId)`.** `reservationId` is only required to be unique *within* a given supplier — different suppliers are free to reuse the same `reservationId` value for entirely unrelated bookings. For example, `(supplier1, reservation1)` and `(supplier2, reservation1)` are two distinct, independent reservations, not a collision; nothing about them is compared or merged just because they share a `reservationId` value. Separately, within the *same* supplier, two different reservationIds are always treated as two different reservations too — no attempt is made to reconcile a case where one supplier issues multiple reservationIds for what a human would consider the same underlying booking. Out of scope.
- **`supplierId`, `reservationId`, and `roomId` are treated as opaque strings of unspecified format** — no assumption is made that any of them is numeric, GUID-shaped, or of any particular length. Different suppliers may use entirely different ID conventions (plain integers as strings, GUIDs, hashes, alphanumeric codes), and nothing in the system may silently assume one particular convention. No surrogate/internal ID is introduced for suppliers — the supplier-provided string is the identity, used directly (after normalization) as the key everywhere.
- **`updatedAtUtc` is excluded from the "is this a duplicate" comparison** but *is* used to detect and ignore stale (out-of-order) updates. Details in the Dedup/Update Logic section.
- **Throttle counters are persisted (DB-backed)**, not in-memory, for consistency with the rest of the persisted state and to survive process restarts.
- **Stats are lifetime cumulative totals per supplier**, not scoped to any time window (the endpoint takes no time range parameter).
- **An unknown/never-seen `supplierId` on the stats endpoint returns `404`**, not zero counts — there's no way for a caller to otherwise distinguish "a real supplier with zero activity so far" from "this identifier has never been seen," and `404` is the conventional REST signal that the requested resource doesn't exist.

---

## Tech stack

- ASP.NET Core Web API, .NET 8+.
- Dapper for data access.
- SQLite (cross-platform, file-based — no install dependency for whoever runs the tests).
- xUnit for the test project (idiomatic default for ASP.NET Core testing, supports in-process test hosting). Actual code style/conventions per `AGENTS.md`.

---

## Architecture: layered

**Controllers**
HTTP concerns only. Parses the request into normalized/canonical domain values (see Normalization below), calls the business layer, and maps the business layer's result to the HTTP response contract defined in this document. Contains no dedup, throttle, or business-rule logic itself.

**Business/Service layer**
Owns the actual decision logic: throttle check, dedup/update/stale-ignore decision, business-rule validation, and stats accounting. This is where the algorithms described below live.

**Data Access layer (Dapper)**
Repository-style access to three concerns: the Reservations table, the throttle window counters, and the per-supplier stats counters. All writes involving a race (see Concurrency) are implemented as single atomic statements, not read-then-decide-then-write sequences.

---

## Data model

**Reservations**
- `SupplierId`, `ReservationId`, `RoomId`, `CheckIn` (UTC), `CheckOut` (UTC), `Price` (decimal), `UpdatedAtUtc` (UTC).
- Unique constraint / primary key: `(SupplierId, ReservationId)`.

**ThrottleWindowCounters**
- `SupplierId`, `BucketId` (wall-clock-aligned 60s bucket, see Throttle Algorithm), `RequestCount`.
- Primary key: `(SupplierId, BucketId)`.
- Only the current and previous bucket per supplier are ever relevant; old buckets can be pruned opportunistically (implementation detail, coding agent's judgment on when/how).

**SupplierStats**
- `SupplierId` (primary key), `IngestedCount`, `ThrottledCount`, `InvalidCount`.
- Needed because the throttle counter table only retains ~2 buckets of history (60–120s) by design — it cannot answer "lifetime total ingested/throttled/invalid," so lifetime counters are tracked separately and incremented at the point each event occurs.
- `IngestedCount` increments on `created`, `updated`, `duplicate`, and `stale_ignored` outcomes — every outcome where the request was a legitimate, well-formed supplier update, whether or not it actually wrote anything. `ThrottledCount` increments **only** on an actual rate-limit rejection (429) — nothing else. `InvalidCount` increments on `400` (invalid input) responses. See the Ingest Endpoint response contract and Stats Endpoint sections for the precise per-outcome breakdown.
- `InvalidCount` is tracked here for completeness but is **not** part of the `GET /api/reservations/stats/{supplierId}` response body — that endpoint returns only `ingested` and `throttled`, matching the spec's literal wording. One consequence worth being explicit about: a supplier whose only activity so far was invalid requests still gets a row here (via the same atomic upsert-or-increment used for the other two counters), so `GET /stats` for that supplier returns `200` with `ingested: 0, throttled: 0` — not `404` — even though nothing meaningful has actually been ingested. `404` is reserved strictly for a `supplierId` with **no row at all**, i.e. one that has never sent *any* request, valid or not.

---

## Normalization (API boundary)

Applied at the point the incoming request is parsed into domain values, before the corresponding business logic runs. Every downstream comparison, storage operation, and echoed response field operates on already-canonical values — never on the raw as-received value:

- `supplierId` / `reservationId` / `roomId` → trimmed, then **converted to uppercase using invariant culture** (`ToUpperInvariant`). This is the one, single casing rule used everywhere a string identifier is compared, stored, or echoed back in a response. Two requests differing only in the casing/whitespace of these fields (e.g. `"acme-1"` vs `"ACME-1"` vs `" Acme-1 "`) are treated as the exact same identifier, and any value returned in a JSON response body echoes the **normalized (uppercase, trimmed)** form, not the literal string the caller sent.
- `checkIn` / `checkOut` / `updatedAtUtc` → parsed as UTC date-times (see Ingest request contract for the exact expected wire format). **Suppliers may send any valid ISO-8601 offset, not only `Z`** — e.g. a booking in India might arrive as `...+05:30`, one in Israel as `...+02:00`. The parser must respect whatever offset is present and convert it to the equivalent UTC instant; it must never assume the offset is always `Z`, and must never ignore or strip a non-`Z` offset. A timestamp with **no** offset/timezone information at all (a "local, unzoned" value) is ambiguous and is rejected as a body-validation failure (400) — there's no way to know what timezone it's relative to. Once converted, only the resulting UTC instant is stored/compared; the original offset the supplier used is not retained anywhere. Parsing a value into its canonical UTC representation *is* the "correctly typed" check for these fields — there is no separate parse step; a value that fails to parse (including a timestamp lacking any offset) is a body-validation failure (400), not a normalization failure.
- `price` → parsed as `decimal` (never floating point), then rounded to 2 decimal places using `MidpointRounding.AwayFromZero` at this boundary — this prevents floating-point/rounding noise from different suppliers producing false "changed" detections, and the fixed rounding mode removes ambiguity for callers that send exact half-cent values (e.g. `12.345` → `12.35`).

**Ordering caveat — `supplierId` is a special case.** `supplierId` must be extracted and normalized *before* the throttle check runs (see Throttle Algorithm and Ingest Endpoint pipeline below), because the throttle bucket is keyed by normalized `supplierId`. All other fields (`reservationId`, `roomId`, `checkIn`, `checkOut`, `price`, `updatedAtUtc`) are normalized together with body validation, which happens *after* the throttle check. This is a boundary-layer concern (Controller → Business handoff) in both cases, not something scattered through comparison logic downstream — it is simply split into two moments because `supplierId` alone is needed earlier than the rest of the payload.

---

## Dedup / update / stale-ignore logic

Applies whenever an incoming request's `(supplierId, reservationId)` already exists in `Reservations`.

**Business fields**, for comparison purposes, means: `roomId`, `checkIn`, `checkOut`, `price`. `updatedAtUtc` is deliberately excluded from this comparison (a supplier re-stamping an otherwise-identical booking with a new `updatedAtUtc` should still count as a duplicate, not a change).

Decision order:

1. **Business fields identical to stored** → **duplicate, no-op.** Nothing is written — not the business fields, and not `updatedAtUtc` either (the originally-recorded `updatedAtUtc` is preserved, not bumped to the latest resend's value).
2. **Business fields differ from stored, AND incoming `updatedAtUtc` is older than the stored `updatedAtUtc`** → **stale, ignored.** This protects against out-of-order delivery (a webhook retry arriving after a newer update already landed). Nothing is written.
3. **Business fields differ from stored, AND incoming `updatedAtUtc` is the same as or newer than stored** → **update applied.** All business fields and `updatedAtUtc` are overwritten with the incoming values.

If `(supplierId, reservationId)` does **not** exist yet → **created.** A new row is inserted with all fields as given.

---

## Concurrency

Multiple instances of this service (separate local processes, sharing one SQLite database file) may handle requests at the same time. Two requests racing for the same `(supplierId, reservationId)` — whether on the same instance or different instances — must not produce a duplicate row or a lost update. This is achieved entirely through atomic database operations; no application-level locking or coordination between instances is used.

- **SQLite must run in WAL (Write-Ahead Logging) mode.** Default SQLite journaling handles single-writer access reasonably but is more prone to lock contention/failures under concurrent writers from multiple processes; WAL mode is specifically designed for concurrent multi-process readers/writers against one file and is required for this design to hold up under the multi-process test scenario.
- **Reservations table**: the unique constraint on `(SupplierId, ReservationId)` prevents two concurrent "not found" paths (from any instance) from both inserting — the database itself serializes this; the losing insert is turned into the update path. The update path (case 3 above) is a **single atomic conditional statement** — the staleness condition (`incoming updatedAtUtc >= stored updatedAtUtc`) is evaluated as part of the same atomic write, not as a separate read-then-write step. This makes the write **order-independent**: regardless of which instance or which of two racing requests the database happens to process first, the final stored state converges to whichever request carried the newer `updatedAtUtc`.
- **ThrottleWindowCounters and SupplierStats must use the same atomic-write discipline.** These are counters that many instances increment concurrently — a naive "read current count, add one, write it back" from application code is a classic lost-update race under multi-process concurrency. Both must be incremented via a single atomic upsert-style statement (insert-a-row-if-absent, otherwise increment in place, as one statement) so that concurrent increments from different instances never silently overwrite each other.
- **A SQLite busy-timeout must be configured on every connection (e.g. several seconds, via `PRAGMA busy_timeout` or the equivalent connection-string setting).** WAL mode still allows only one writer at a time across the whole file; without a busy-timeout, a second process's writer that arrives while another process is mid-write receives an immediate `SQLITE_BUSY` error from SQLite rather than transparently waiting its turn. Left unhandled, that would surface to callers as a spurious `500` under real concurrent load — not a correctness violation of "no duplicate/no lost update" as such, but a reliability gap that would defeat the intent of this section during the 3-instance concurrency tests. A busy-timeout makes SQLite block-and-retry internally instead, so concurrent writers serialize transparently.
- **Writes that must take effect together are wrapped in a single database transaction.** Applying a reservation write (create/update) and incrementing `SupplierStats.IngestedCount`, or incrementing `ThrottleWindowCounters` and (on rejection) `SupplierStats.ThrottledCount`, are each pairs of statements that must both commit or neither commit — otherwise a crash or exception between the two statements would leave the persisted reservation state and the persisted stats counters inconsistent with each other. Each such pair is executed inside one transaction, in addition to each individual statement being atomic in the sense described above.
- **Classifying the response as `duplicate` vs. `stale_ignored` must use the same read the conditional write is evaluated against, not a separate query issued before or after it.** Both outcomes are no-op (nothing is written, and neither counts toward `IngestedCount`), but the response body's `status` text still must correctly report which of the two happened. Because another instance could concurrently apply an update to the same row between a "classify" read and a "write" statement issued as two separate round-trips, the classification must be derived from the same atomic operation used to decide whether to write (e.g. reading the affected-row-count/returned row of the conditional write itself, within the same transaction/connection) rather than from an independent SELECT that could observe a different state than the write actually acted on.

---

## Throttle algorithm

**Approach**: DB-backed sliding window counter (approximation), not an exact per-request log.

**Bucket alignment**: wall-clock aligned, not per-supplier-activity aligned. `BucketId = floor(unixTimeSeconds / 60)` — a fixed, global 60-second grid shared by all requests regardless of which supplier they belong to.

**Estimate formula**, evaluated at request time:

```
estimatedCount = currentBucketCount + previousBucketCount × overlapWeight
overlapWeight  = (60 − secondsElapsedIntoCurrentBucket) / 60
```

**Bucket key**: the normalized `supplierId` (trim + uppercase, see Normalization) extracted during the Supplier Identification step of the ingest pipeline — *before* the rest of the body is validated. This is why `supplierId` extraction/normalization is pulled out ahead of the rest of body validation: the throttle bucket must exist and be keyed consistently regardless of what casing/whitespace a given request happens to use, and regardless of whether the rest of the body turns out to be invalid.

**Order of operations per incoming request** (this ordering is required, not incidental, because the estimate must reflect *this* request too when deciding whether to reject it):

1. Increment `RequestCount` for this supplier's current bucket. This happens for **every** request that has a valid, non-blank `supplierId` — including ones that later turn out to be invalid for other reasons (missing `roomId`, `checkIn >= checkOut`, etc. → 400). The only requests that never reach this step are the ones rejected for missing/blank/unparseable `supplierId` itself (see Ingest Endpoint pipeline, step 2), since there is no supplier to attribute a bucket increment to.
2. Recompute `estimatedCount` using the formula above (now including this request).
3. If `estimatedCount > 100` → reject: increment `SupplierStats.ThrottledCount`, respond `429`.
4. Otherwise → proceed to body validation of the remaining fields.

Concretely: the 100th request in a rolling window is allowed (`estimatedCount == 100`); the 101st is the first one rejected (`estimatedCount == 101`).

Note for the testing agent: because this is an *approximation*, exact behavior at sub-bucket boundaries has a small, expected error margin relative to a perfect sliding log — tests should target clearly-inside/clearly-outside-the-window scenarios (e.g., well past 60s, or a clear burst of 101+ requests within one bucket) rather than asserting exact behavior at fractional-second boundaries.

**Cross-instance enforcement is the whole point of this being DB-backed rather than in-memory.** Because `ThrottleWindowCounters` lives in the one shared SQLite file (not a per-process in-memory counter) and every increment is a single atomic upsert, a supplier's limit is enforced on its *combined* traffic across all running instances, not per instance. A rogue supplier splitting requests across the 3 concurrently-running instances (e.g. 90 to each, 270 total in one window) is still capped at the same shared 100 — the 101st request in combined arrival order is rejected, regardless of which instance happens to receive it. An implementation that tracked this counter in-memory per instance would look correct under single-instance testing but would incorrectly allow up to `100 × instance count` requests through — this is precisely the failure mode the DB-backed design exists to prevent.

---

## Ingest endpoint: `POST /api/reservations/ingest`

**Request contract** — the incoming JSON body (`application/json`, default ASP.NET Core camelCase field naming):

| Field | JSON type | Required | Notes |
|---|---|---|---|
| `supplierId` | string | yes, non-blank after trim | Normalized per the casing rule above. |
| `reservationId` | string | yes, non-blank after trim | Normalized per the casing rule above. |
| `roomId` | string | yes, non-blank after trim | Normalized per the casing rule above. |
| `checkIn` | string | yes | ISO-8601 date-time **with an explicit UTC offset** — `Z` or `±hh:mm`, e.g. `"2026-01-10T00:00:00Z"` or the equivalent `"2026-01-10T05:30:00+05:30"`. An offset-less local timestamp is rejected (400) — there's no way to know what timezone it's relative to. |
| `checkOut` | string | yes | ISO-8601 date-time, same format/offset rules as `checkIn`. |
| `price` | number | yes | JSON number, non-negative. Parsed as `decimal`. |
| `updatedAtUtc` | string | yes | ISO-8601 date-time, same format/offset rules as `checkIn`. |

Full pipeline, in order:

1. Global error-handling middleware wraps the whole request (see Error Handling).
2. **Supplier identification (runs before throttling).** The request body must be syntactically valid JSON containing a `supplierId` field that is a non-blank string once trimmed. If the JSON cannot be parsed at all, or `supplierId` is missing, blank, or not a string, this is an immediate `400`, and — because there is no supplier to attribute the request to — **the throttle counter is not touched** (this is the one and only body-validation failure that is evaluated *before* throttling; every other validation failure below is evaluated *after*). If `supplierId` is present and non-blank, it is normalized (trim + uppercase) immediately, and used as the throttle bucket key in the next step.
3. Throttle check (see Throttle Algorithm), keyed by the normalized `supplierId`. If throttled → `429`, stop.
4. Body validation of the remaining fields, with normalization applied as each field is parsed (see Normalization):
   - `reservationId` and `roomId` present, non-blank after trim.
   - `checkIn`, `checkOut`, `updatedAtUtc` present and parseable as ISO-8601 UTC date-times.
   - `checkIn < checkOut`.
   - `price` present, parseable as a non-negative `decimal` (`price >= 0`).
   - Any failure → `400`, increment `SupplierStats.InvalidCount` for the normalized `supplierId` (already known from step 2), stop. (Does not affect `IngestedCount` or `ThrottledCount`. The request was *also* already counted toward the rolling throttle window in step 3 — that is a separate, short-lived window count, not a stats count; see Stats endpoint.)
5. Dedup/update/stale-ignore decision (see above) against the normalized `(supplierId, reservationId)`.
6. Response per outcome (table below). Stats impact per outcome is in the table's last column — see Stats Endpoint below for the full rationale, in particular why `duplicate`/`stale_ignored` count toward `ingested` despite writing nothing, and why `throttled` counts only the actual rate-limit (429) rejection.

**Response contract:**

| Outcome | HTTP status | Body | Stats impact |
|---|---|---|---|
| Created (new reservation) | 201 | `{ "status": "created", "supplierId": "...", "reservationId": "..." }` | `ingested` += 1 |
| Updated | 200 | `{ "status": "updated", "supplierId": "...", "reservationId": "..." }` | `ingested` += 1 |
| Duplicate (no-op) | 200 | `{ "status": "duplicate", "supplierId": "...", "reservationId": "..." }` | `ingested` += 1 |
| Stale (ignored) | 200 | `{ "status": "stale_ignored", "supplierId": "...", "reservationId": "..." }` | `ingested` += 1 |
| Throttled (rate limit) | 429 | ProblemDetails (see Error Handling) | `throttled` += 1 |
| Invalid input | 400 | ProblemDetails / ValidationProblemDetails (see Error Handling) | `invalid` += 1 (tracked internally only, never returned) |

---

## Stats endpoint: `GET /api/reservations/stats/{supplierId}`

1. Route validation: `supplierId` must be non-empty after trimming → `400` (ProblemDetails) if missing/blank.
2. Normalize `supplierId` the same way as on ingest (trim + uppercase invariant, see Normalization) before using it as the lookup key. This is required for the stats endpoint to agree with the ingest endpoint on supplier identity — e.g. a supplier ingested as `"acme-1"` must be found under `GET /api/reservations/stats/ACME-1` (or any other casing/whitespace variant), since ingest always stores the normalized form.
3. Look up the normalized `supplierId` in `SupplierStats`. If no row exists → `404` (ProblemDetails). A row exists as soon as anything has incremented `IngestedCount`, `ThrottledCount`, **or** `InvalidCount` for that supplier (see Data Model). Note: a supplier whose only activity so far was invalid (`400`) requests *does* have a row (because `InvalidCount` was incremented), and so returns `200` with `ingested: 0, throttled: 0` — not `404` — even though nothing was actually ingested. `404` means no request of any kind, valid or not, has ever been seen for that `supplierId`.
4. Otherwise → `200`. The `supplierId` in the response body is the **normalized** form (matching how it was normalized/stored on ingest), not necessarily byte-identical to the route parameter as typed by the caller:

```
{ "supplierId": "...", "ingested": <SupplierStats.IngestedCount>, "throttled": <SupplierStats.ThrottledCount> }
```

`ingested` = count of requests that were accepted and processed as a legitimate, well-formed supplier update — `created`, `updated`, `duplicate`, and `stale_ignored` **all** count here, regardless of whether they actually wrote anything. A duplicate resend or a stale out-of-order update is still a genuine reservation update arriving from the supplier's feed; it just didn't change stored data.

`throttled` = count of requests rejected specifically because the sliding-window rate limiter caught them (429). Nothing else counts here — this is a narrow bucket, exactly the 429 count for that supplier.

`invalid` (malformed, `400`) requests are tracked internally (`SupplierStats.InvalidCount`, see Data Model) but are **not** part of this response body — only `ingested` and `throttled` are ever returned, matching the spec's literal wording.

This lifetime `throttled` stat is a distinct concept from the rolling-window `RequestCount` used to decide whether to *reject* a request with 429 in the first place (see Throttle Algorithm) — that rolling counter counts literally every request, including invalid ones, purely to evaluate the 60-second window. `SupplierStats.ThrottledCount` is a separate, all-time tally of only the 429-rejection outcome, incremented once per rejection, never decremented or windowed.

---

## Error handling

- **Global exception-handling middleware** catches any unhandled exception anywhere in the pipeline and returns `500` with a generic ProblemDetails body (no internal exception details leaked).
- **Error shape**: ASP.NET Core's built-in `ProblemDetails` (RFC 7807) is used as the single, consistent error shape across the entire app — including framework-level failures that occur before application code runs (malformed JSON, wrong content-type, unroutable requests). Domain-specific extra fields, if any are needed, are added via the standard `extensions` mechanism rather than inventing a parallel custom shape — this avoids the app ever returning two different error shapes depending on failure type.
- **Note on achieving this in practice**: registering `AddProblemDetails()` alone (the .NET 8 default) does **not** automatically give a `ProblemDetails` body to a request that matches no route at all — by default that still falls through as an empty-bodied `404`. To make the "single consistent error shape everywhere" guarantee actually true, the coding agent must also register `app.UseStatusCodePages()` (or equivalent) in the middleware pipeline, so that empty-body 4xx/5xx responses — including unmatched routes — are also rendered as `ProblemDetails`, not just exceptions caught by `UseExceptionHandler()` and MVC/`[ApiController]` model-validation failures (which do get `ProblemDetails`/`ValidationProblemDetails` automatically). The testing agent should expect a `ProblemDetails` JSON body (with a `404` status field) for an unmatched route, not an empty response body.
- **Validation failures** (400) use `ValidationProblemDetails` with field-level error detail.
- **Throttled** (429) uses `ProblemDetails`.

---

## Testability requirements

**Test structure**: every test follows the **AAA pattern** (Arrange, Act, Assert) — set up state, perform the one HTTP call under test, then assert on the response/resulting state, with no interleaving of the three phases.

**Two different test harnesses are needed for two different concerns — don't conflate them:**

**1. Controllable time (for throttle window-boundary tests)**

The 60-second throttle window depends on server "current time" (`BucketId` and `secondsElapsedIntoCurrentBucket` are both derived from it). To make this testable without real 60+ second waits:

Note that the staleness comparison, by contrast, does **not** depend on server "current time" at all — per the Dedup/update/stale-ignore logic section, it compares the incoming request's `updatedAtUtc` against the previously-*stored* `updatedAtUtc`, both of which are values the caller supplies in request payloads. A staleness test is therefore an ordinary black-box HTTP test (no fake `TimeProvider`, no in-process host required): POST a reservation with `updatedAtUtc = T2`, then POST a conflicting update with `updatedAtUtc = T1` where `T1 < T2`, and assert `stale_ignored`. Only the throttle-window tests genuinely need the fake-clock harness described below.

- Production code obtains current time exclusively through **.NET 8's built-in `TimeProvider`** abstraction, injected via DI. Production configuration uses `TimeProvider.System`.
- For these scenarios, the testing agent runs the application in-process via **`WebApplicationFactory<TEntryPoint>`** (`TEntryPoint` being the API project's entry point, e.g. `Program`), and substitutes a fake/controllable `TimeProvider` into the DI container to fast-forward time instantly. Tests still call the real HTTP endpoints through the factory's `HttpClient` and assert only on real HTTP responses — this does not make the tests less "black-box" with respect to the dedup/throttle *algorithm*, it only gives them control over an infrastructure seam (the clock).
- This `WebApplicationFactory` harness runs a single in-process instance — it is not used for the multi-instance concurrency scenario below (fake time only exists within that one process's DI container, and `WebApplicationFactory` doesn't produce a separate OS process other instances could share a port/database with in the way section 2 needs).

**2. Multiple real instances (for concurrency tests)**

These tests are about race correctness (do simultaneous writes from separate processes collide safely), not about window timing — real wall-clock time is fine here, no fake `TimeProvider` involved. This harness does **not** use `WebApplicationFactory` — these are genuinely separate OS processes of the built application, reached over real HTTP, not an in-memory test server.

- The coding agent must make the **listening port** and the **SQLite database file path** externally configurable (standard ASP.NET Core configuration — environment variables / command-line arguments / `appsettings` — not hardcoded), so the testing agent can launch several instances of the built application as separate OS processes, each on a different port, all pointing at the same database file.
- For this exercise, the testing agent should bring up **3 concurrent instances** (no need for more) pointing at the same SQLite file, and fire concurrent/racing requests across them to validate the guarantees described in Concurrency above (no duplicate rows, no lost updates, correctly-aggregated throttle/stats counters across instances).

**General isolation note**: each test should use a distinct `supplierId` (and/or `reservationId`) where relevant, since state is persisted in SQLite and not automatically isolated between tests unless the test harness explicitly resets/recreates the database per test run.

---

## Suggested test coverage (guidance for the testing agent, not exhaustive)

- Exact resend (identical fields) → `duplicate`, no state change, `ingested` increments, `throttled`/`invalid` unaffected.
- Resend with changed business fields + newer/equal `updatedAtUtc` → `updated`, state changes, `ingested` increments, `throttled`/`invalid` unaffected.
- Resend with changed business fields + older `updatedAtUtc` → `stale_ignored`, no state change, `ingested` increments, `throttled`/`invalid` unaffected.
- New `reservationId` → `created`, `ingested` increments, `throttled`/`invalid` unaffected.
- Invalid payloads: missing field, `checkIn >= checkOut`, negative `price` → `400`, `InvalidCount` increments.
- Missing/blank `supplierId` (or unparseable JSON) → `400`, and — unlike other invalid payloads — this specific case is rejected *before* the throttle counter is touched, and never increments `InvalidCount` either (there is no supplierId to attribute either counter to).
- A supplier who has only ever sent invalid (400) requests still returns `200` (with `ingested: 0, throttled: 0`) from the stats endpoint, not `404` — because `InvalidCount` was incremented, a row already exists.
- Exercise a variety of realistic ID shapes for `supplierId`/`reservationId`/`roomId` — GUID-formatted strings, plain-integer-looking strings, hash-like strings, mixed alphanumeric — confirming dedup, throttling, and stats behave identically regardless of which convention a given supplier happens to use.
- Timestamps sent with different, valid, non-`Z` UTC offsets (e.g. `+05:30`, `-08:00`) parse correctly and produce the same stored UTC instant as an equivalent `Z` timestamp; an offset-less local timestamp is rejected as `400`.
- Same supplier ingested under different casing/whitespace (e.g. `"acme-1"`, `"ACME-1"`, `" Acme-1 "`) is treated as one identity: reservations collide/dedupe across them, and `GET /api/reservations/stats/{supplierId}` returns the same counters regardless of which casing variant is queried.
- Throttle: exactly 100 requests succeed within a window; the 101st is `429`; after the window rolls past (via fake time advance), requests succeed again.
- Only rate-limit-rejected (429) requests count toward `ThrottledCount`; `duplicate` and `stale_ignored` outcomes count toward `IngestedCount` instead; invalid (400) requests count toward `InvalidCount` only.
- The `GET /api/reservations/stats/{supplierId}` response body contains only `supplierId`, `ingested`, and `throttled` — never a count of invalid/400 requests, even though they're tracked internally.
- A mixed sequence of created/updated/duplicate/stale/throttled/invalid requests for one supplier produces `ingested`/`throttled` totals that exactly match the per-outcome breakdown in the Ingest Endpoint response contract table.
- Concurrent requests for the same `(supplierId, reservationId)`, fired across 3 concurrently-running instances sharing one SQLite file, do not produce a duplicate row or a lost update — the final stored state should always reflect whichever request carried the newest `updatedAtUtc`.
- Throttle counts and stats counts remain correct (no lost increments) when concurrent requests for the same supplier are spread across the 3 instances.
- A rogue supplier splits requests across all 3 concurrently-running instances (e.g. 90 to each within one 60s window, 270 total) — the shared 100-request limit is still enforced on the combined total, not per instance; the 101st request in combined arrival order gets `429` regardless of which instance receives it.
- Unknown `supplierId` on stats → `404` (ProblemDetails).
- Empty/blank `supplierId` route parameter on stats → `400`.
- Unhandled-error path → `500` with ProblemDetails (if a way to trigger this deterministically is needed, coordinate with the coding agent on a safe way to do so, e.g. a scenario that's naturally exercised rather than a test-only backdoor).

---

## Explicitly out of scope

Auth, observability/metrics, genuine multi-*machine* deployment (as opposed to multiple local processes sharing one SQLite file), message queues/topics, cross-reservationId reconciliation across a supplier's retries, production-grade hardening in general. These should be listed in the README as "what I'd do differently with more time" where relevant, not built.
