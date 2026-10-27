using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;

namespace Scheduler.Infrastructure;

public enum ReferenceCacheRegion { Catalog, QuotePolicy }

// Own a size-limited cache rather than imposing entry-size requirements on framework caches.
public sealed class ReferenceDataCache : IDisposable
{
    private readonly MemoryCache storage;
    private readonly IMemoryCache cache;
    private readonly TimeProvider clock;
    private readonly bool enabled;
    private readonly TimeSpan catalogLifetime;
    private readonly TimeSpan policyLifetime;
    private readonly SemaphoreSlim[] gates = Enumerable.Range(0, 16).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private long generation;

    public ReferenceDataCache(IConfiguration configuration, TimeProvider clock)
    {
        this.clock = clock;
        var settings = configuration.GetSection("Caching");
        enabled = settings.GetValue("Enabled", true);
        var capacity = settings.GetValue("MaxEntries", 512);
        var catalogSeconds = settings.GetValue("ReferenceSeconds", 300);
        var policySeconds = settings.GetValue("PolicySeconds", 60);
        if (capacity is < 1 or > 10000 || catalogSeconds is < 1 or > 3600 || policySeconds is < 1 or > 300)
            throw new InvalidOperationException("Caching requires MaxEntries 1–10000, ReferenceSeconds 1–3600, and PolicySeconds 1–300.");
        catalogLifetime = TimeSpan.FromSeconds(catalogSeconds);
        policyLifetime = TimeSpan.FromSeconds(policySeconds);
        storage = new MemoryCache(new MemoryCacheOptions { SizeLimit = capacity });
        cache = storage;
    }

    public async Task<T?> Get<T>(ReferenceCacheRegion region, object key, Func<Task<T?>> load, CancellationToken ct) where T : class
    {
        ct.ThrowIfCancellationRequested();
        var name = region == ReferenceCacheRegion.Catalog ? "catalog" : "quote-policy";
        if (!enabled)
        {
            InfrastructureTelemetry.CacheRequest(name, "bypass");
            return await load();
        }
        var cacheKey = new Key(region, key, Volatile.Read(ref generation));
        if (TryRead<T>(cacheKey, out var value))
        {
            InfrastructureTelemetry.CacheRequest(name, "hit");
            return value;
        }
        InfrastructureTelemetry.CacheRequest(name, "miss");
        // Fixed stripes bound coordination memory even for invalid/unrecognized identifiers.
        var gate = gates[(int)((uint)cacheKey.GetHashCode() % gates.Length)];
        await gate.WaitAsync(ct);
        try
        {
            if (TryRead<T>(cacheKey, out value)) return value;
            try
            {
                value = await load();
                InfrastructureTelemetry.CacheLoad(name, value == null ? "not_found" : "success");
                if (value != null)
                {
                    var lifetime = region == ReferenceCacheRegion.Catalog ? catalogLifetime : policyLifetime;
                    cache.Set(cacheKey, new Entry<T>(value, clock.GetUtcNow().Add(lifetime)),
                        new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = lifetime, Size = 1 });
                }
                return value;
            }
            catch
            {
                InfrastructureTelemetry.CacheLoad(name, "error");
                throw; // A failed load never poisons the next request or serves an expired value.
            }
        }
        finally { gate.Release(); }
    }

    private bool TryRead<T>(Key key, out T? value) where T : class
    {
        if (cache.TryGetValue<Entry<T>>(key, out var entry) && entry != null && clock.GetUtcNow() < entry.ExpiresAt)
        {
            value = entry.Value;
            return true;
        }
        value = null;
        return false;
    }

    // A future configuration writer calls this after commit. In-flight old loads retain their old generation.
    public void InvalidateAll()
    {
        Interlocked.Increment(ref generation);
        storage.Clear();
    }

    public void Dispose()
    {
        storage.Dispose();
        foreach (var gate in gates) gate.Dispose();
    }

    private sealed record Key(ReferenceCacheRegion Region, object Identity, long Generation);
    private sealed record Entry<T>(T Value, DateTimeOffset ExpiresAt);
}
