# Reference and quote-policy caching

The Infrastructure layer implements cache-aside reads using a dedicated `IMemoryCache` owned by `ReferenceDataCache`. Domain/Application remain independent of cache libraries, and the HTTP contracts are unchanged. Each API process owns its own cache.

| Cached data | Identity | Default absolute lifetime |
| --- | --- | --- |
| Shared dealership/service catalogue | Fixed catalogue key | 300 seconds |
| Base price/currency/version, time bands, service baseline, category duration rules/versions, dealership time zone | Dealership + service IDs | 60 seconds |

Customer/vehicle lists remain live, caller-scoped PostgreSQL reads. Every new quote also validates current vehicle ownership and reads its current category. That category selects a duration rule from the cached policy, with the existing baseline fallback. Each quote evaluates its requested local weekday/minute against the policy's complete band list; crossing a pricing boundary does not require cache expiration.

Price, bands, and duration rules are loaded together in one SQL statement and retained as an immutable policy snapshot. A quote uses that same snapshot throughout issuance, even if the cache expires between retrieving its inputs and selecting its band. Newly constructed quote inputs are not shared mutable cache objects. Accepted quotes and appointments keep their persisted price/duration snapshots and their existing expiry/idempotency behavior.

## Expiration, invalidation, and failure behavior

A hit returns the cached value. A miss loads from PostgreSQL and stores only a successful non-null result. Absolute expiration bounds staleness even under continuous traffic. Sixteen fixed coordination gates coalesce concurrent loads of a key, with occasional serialization between different keys sharing a gate. Gate count remains bounded for unknown identifiers. Cancellation/failure releases the gate; failed loads and unknown policies are not retained.

The cache permits at most 512 entries by default. This is an entry-count budget, **not a byte limit**; policy/catalogue payload size still depends on reference-data volume. If an entry cannot be admitted, the successfully loaded source value can still serve its caller. Restarting the API clears its cache.

Policy changes made directly in PostgreSQL can take up to 60 seconds to affect newly issued quotes; catalogue changes can take up to 300 seconds. Payloads carry their policy/duration versions, but those versions are not polled on every hit. `ReferenceDataCache.InvalidateAll()` invalidates this process's entries immediately for subsequent calls. Its generation key prevents an older in-flight load from repopulating the active generation after invalidation. A future configuration writer should invoke invalidation after a successful commit. There is currently no administration/invalidation HTTP endpoint or automatic cross-process invalidation.

No cache participates in booking confirmation, resource availability/occupancy, appointment status, authorization decisions, or notification state. Those reads continue against PostgreSQL; confirmation rechecks resources inside the existing transaction and locking protocol. Readiness also queries the database. A warm cache cannot make the application function without its database, because identity-specific reads and business writes still require it. When a refresh fails, an expired cached value is not served as a fallback.

## Configuration and telemetry

`appsettings.json` supplies:

```json
"Caching": {
  "Enabled": true,
  "MaxEntries": 512,
  "ReferenceSeconds": 300,
  "PolicySeconds": 60
}
```

Set `Caching__Enabled=false` to bypass caching. Allowed ranges are 1–10,000 entries, 1–3,600 reference seconds, and 1–300 policy seconds. The cache owns its bounded `MemoryCache` instance, so framework caches are not subjected to its entry-size requirements.

`scheduler.cache.requests` counts `hit`, `miss`, and `bypass` by the bounded `cache` label (`catalog` or `quote-policy`). `scheduler.cache.loads` counts actual source loads as `success`, `not_found`, or `error`. Multiple simultaneous misses can share one source load; compare both counters when examining stampede protection. Both meters use the existing OpenTelemetry/Prometheus pipeline.

```promql
sum by (cache, outcome) (rate(scheduler_cache_requests_total[5m]))
sum by (cache, outcome) (rate(scheduler_cache_loads_total[5m]))
```

## Redis improvement plan

Redis is a documented future extension. The current implementation uses process-local memory only.

1. Introduce a shared Redis cache through an Infrastructure adapter using `IDistributedCache`, with explicit serialization, schema/version identifiers, and keys scoped to dealership/service.
2. Keep bounded TTLs. After a configuration update commits, publish durable invalidation through an outbox event. If retaining a local memory layer, invalidate that layer on all replicas as well. Account for event delay and retain TTL as a freshness bound.
3. On Redis timeout/unavailability, fall back to bounded PostgreSQL reads. Control concurrent fallback traffic to avoid overwhelming the database. Measure hit rates, source loads, latency, invalidation delay, and memory use.
4. Keep booking correctness in PostgreSQL. Redis must not become the authority for reservations or supply process-distributed booking locks.

Shared caching can reduce repeated loads and cold-cache duplication across API replicas. It adds network access and an operational dependency; improvements must be measured with the real traffic mix. It does not automatically scale the PostgreSQL writer or establish production availability. Redis has not been added to Docker Compose or provisioned.

References: [Microsoft's in-memory caching guidance](https://learn.microsoft.com/en-us/aspnet/core/performance/caching/memory?view=aspnetcore-10.0) and [distributed cache guidance](https://learn.microsoft.com/en-us/aspnet/core/performance/caching/distributed?view=aspnetcore-10.0).
