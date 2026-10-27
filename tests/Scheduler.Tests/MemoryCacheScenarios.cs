using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Scheduler.Infrastructure;
using Xunit;

namespace Scheduler.Tests;

public sealed class MemoryCacheScenarios
{
    [Fact]
    public async Task Given_simultaneous_cold_requests_When_reference_data_is_loaded_Then_only_one_source_load_runs()
    {
        // Given twenty concurrent requests and a source load held until all callers have started.
        using var cache = Create(new TestClock());
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loads = 0;
        async Task<Value?> Load() { Interlocked.Increment(ref loads); await release.Task; return new(1); }
        var requests = Enumerable.Range(0, 20).Select(_ => cache.Get(ReferenceCacheRegion.Catalog, "catalog", Load, default)).ToArray();

        // When the source becomes available.
        release.SetResult();
        var values = await Task.WhenAll(requests);

        // Then callers share one completed load, rather than stampeding the source.
        loads.Should().Be(1);
        values.Should().OnlyContain(value => value != null && value.Revision == 1);
    }

    [Theory]
    [InlineData(ReferenceCacheRegion.Catalog, 300)]
    [InlineData(ReferenceCacheRegion.QuotePolicy, 60)]
    public async Task Given_a_warm_entry_When_its_absolute_lifetime_passes_Then_the_next_request_reloads(ReferenceCacheRegion region, int seconds)
    {
        // Given a successfully cached source value.
        var clock = new TestClock();
        using var cache = Create(clock);
        var loads = 0;
        Task<Value?> Load() => Task.FromResult<Value?>(new(++loads));
        await cache.Get(region, "key", Load, default);
        clock.Set(clock.GetUtcNow().AddSeconds(seconds - 1));
        (await cache.Get(region, "key", Load, default))!.Revision.Should().Be(1);

        // When the fixed lifetime is reached, despite a recent cache hit.
        clock.Set(clock.GetUtcNow().AddSeconds(1));
        var refreshed = await cache.Get(region, "key", Load, default);

        // Then reads cannot extend stale configuration indefinitely.
        refreshed!.Revision.Should().Be(2);
        loads.Should().Be(2);
    }

    [Fact]
    public async Task Given_a_source_load_failed_When_the_next_request_arrives_Then_it_can_recover()
    {
        // Given the first source request fails.
        using var cache = Create(new TestClock());
        Func<Task> failed = async () => await cache.Get<Value>(ReferenceCacheRegion.Catalog, "key",
            () => throw new InvalidOperationException("Source temporarily unavailable"), default);
        await failed.Should().ThrowAsync<InvalidOperationException>();

        // When a later load succeeds.
        var result = await cache.Get<Value>(ReferenceCacheRegion.Catalog, "key", () => Task.FromResult<Value?>(new(2)), default);

        // Then no failed task/value was retained in the cache.
        result!.Revision.Should().Be(2);
    }

    [Fact]
    public async Task Given_invalidation_during_a_source_load_When_the_old_load_finishes_Then_new_requests_keep_the_new_generation()
    {
        // Given an old source load is still running when configuration is invalidated.
        using var cache = Create(new TestClock());
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<Value?> OldLoad() { await release.Task; return new(1); }
        var old = cache.Get(ReferenceCacheRegion.QuotePolicy, "key", OldLoad, default);
        cache.InvalidateAll();

        // When the old load completes and a request loads the new configuration.
        release.SetResult();
        await old;
        await cache.Get<Value>(ReferenceCacheRegion.QuotePolicy, "key", () => Task.FromResult<Value?>(new(2)), default);
        var current = await cache.Get<Value>(ReferenceCacheRegion.QuotePolicy, "key", () => throw new InvalidOperationException("Unexpected source load"), default);

        // Then the old completion cannot repopulate the current generation with stale terms.
        current!.Revision.Should().Be(2);
    }

    [Fact]
    public async Task Given_caching_is_disabled_When_configuration_changes_Then_each_request_reads_the_source()
    {
        // Given a deployment explicitly bypasses caching.
        using var cache = Create(new TestClock(), enabled: false);
        var version = 1;
        Task<Value?> Load() => Task.FromResult<Value?>(new(version));
        await cache.Get(ReferenceCacheRegion.Catalog, "key", Load, default);

        // When the source changes between requests.
        version = 2;
        var current = await cache.Get(ReferenceCacheRegion.Catalog, "key", Load, default);

        // Then a cached value cannot override the bypass setting.
        current!.Revision.Should().Be(2);
    }

    private static ReferenceDataCache Create(TestClock clock, bool enabled = true) => new(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Caching:Enabled"] = enabled.ToString() }).Build(), clock);
    private sealed record Value(int Revision);
}
