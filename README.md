# ReservationService

Supplier feed throttle & deduplication service built with ASP.NET Core (.NET 10), Dapper, and SQLite.

---

## 1. Assumptions

- **Identity & Dedup**: A reservation is uniquely identified by the composite key `(supplierId, reservationId)`. Deduplication compares business fields (`roomId`, `checkIn`, `checkOut`, `price`). Updates with an older `updatedAtUtc` than stored are ignored as stale.
- **Stats Semantics**: `Ingested` counts all validly processed feed updates (created, updated, duplicate, stale-ignored). `Throttled` counts 429 rate-limit rejections. An unknown supplier returns `404 Not Found`.
- **Price Precision**: Prices are rounded to 2 decimal places to match the spec example(ignoring real world currency variations).
- **Multi-Instance Concurrency**: Designed for multiple local API processes sharing a single SQLite database in WAL mode. Rate limiting is DB-backed so traffic split across instances consumes a shared 60s quota.

---

## 2. Key Decisions & AI Collaboration

- **Message Queues vs. SQLite**: I initially considered per-supplier message queues. Claude pointed out that SQLite serializes all writes globally, so a queue adds indirection without removing the database bottleneck. I dropped the queue and accepted SQLite write-serialization for this scale.
- **Sliding-Window Counter vs. Log**: Claude proposed an exact request log (storing 1 row per request). I pushed back and implemented a sliding-window counter approximation to keep storage bounded per supplier.
- **Stats 404 vs. 200**: Claude initially suggested returning `200 OK` with zero stats for unknown suppliers. I overrode this in favor of `404 Not Found` to adhere to standard REST semantics.
- **Storage Format**: Claude suggested `DateTime.Ticks` (lossless .NET precision). I opted for Unix epoch milliseconds for standard SQL compatibility and tool readability.

---

## 3. What I'd Do Differently With More Time

- **Production Database for True Concurrency**: Swap SQLite for a database engine supporting row-level locking (e.g. PostgreSQL or SQL Server) to eliminate SQLite's global single writer lock and enable genuine multi-node horizontal scaling.
- **Configurable Rate Limits**: Move hardcoded constants (100 req / 60s) into configuration and environment variables
- **Reduce files bloat**: Consolidate single line DTO records into their respective feature files to reduce file sprawl.
- **Observability**: Add OpenTelemetry metrics and tracing to monitor throttle rejection rates and per-supplier traffic patterns in production (scoped out for this exercise).