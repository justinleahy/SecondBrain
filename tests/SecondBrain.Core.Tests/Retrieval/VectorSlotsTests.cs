using SecondBrain.Core.Retrieval;
using Xunit;

namespace SecondBrain.Core.Tests.Retrieval;

public sealed class VectorSlotsTests
{
    [Fact]
    public void DeleteReplaceChurnReusesSlots()
    {
        var map = new VectorSlotMap(2, 4);
        for (var i = 0; i < 1_000; i++)
        {
            using var change = map.Prepare(new Dictionary<string, float[]>
            {
                [$"row-{long.MaxValue - i}"] = [3, 4],
                ["stable"] = [1, 0],
            }, i == 0 ? [] : [$"row-{long.MaxValue - i + 1}"]);
            change.Commit();
            Assert.Equal(2, map.Statistics.LiveSlots);
            Assert.Equal(0, map.Statistics.RetainedSlots);
            Assert.InRange(map.Statistics.AllocatedSlots, 2, 8);
        }
        using (var empty = map.Prepare(new Dictionary<string, float[]>(), ["stable", $"row-{long.MaxValue - 999}"])) empty.Commit();
        Assert.Equal(new VectorSlotStatistics(0, 0, 0, 0), map.Statistics);
    }

    [Fact]
    public void ReclamationWaitsForReaders()
    {
        var map = new VectorSlotMap(2, 2);
        Publish(map, "a", [3, 4]);
        var first = map.AcquireSnapshot();
        var second = map.AcquireSnapshot();
        Publish(map, "a", [0, 2]);
        using var latest = map.AcquireSnapshot();
        Assert.True(first.TryGetVector("a", out var original));
        Assert.Equal(new float[] { .6f, .8f }, original.ToArray());
        Assert.True(latest.TryGetVector("a", out var replacement));
        Assert.Equal(new float[] { 0, 1 }, replacement.ToArray());
        Assert.Equal(1, map.Statistics.RetainedSlots);
        first.Dispose();
        Assert.Equal(1, map.Statistics.RetainedSlots);
        second.Dispose();
        Assert.Equal(0, map.Statistics.RetainedSlots);
        Assert.Equal(2, latest.Generation);
    }

    [Fact]
    public async Task LeasedVectorReadsRemainStableAcrossConcurrentPublications()
    {
        var map = new VectorSlotMap(2, 2);
        Publish(map, "a", [3, 4]);
        using var reader = map.AcquireSnapshot();
        using var start = new ManualResetEventSlim();
        var writer = Task.Run(() =>
        {
            start.Wait();
            for (var i = 0; i < 1_000; i++) Publish(map, "a", [i + 1, 1]);
        });
        var reads = Task.Run(() =>
        {
            start.Wait();
            for (var i = 0; i < 10_000; i++)
            {
                Assert.True(reader.TryGetVector("a", out var vector));
                Assert.Equal(.6f, vector.Span[0]);
                Assert.Equal(.8f, vector.Span[1]);
            }
        });
        start.Set();
        await Task.WhenAll(writer, reads);
        Assert.Equal(1, map.Statistics.RetainedSlots);
    }

    [Fact]
    public void AbortedPublicationPreservesSnapshotAndReleasesStaging()
    {
        var map = new VectorSlotMap(2, 2);
        Publish(map, "a", [1, 0]);
        using (map.Prepare(new Dictionary<string, float[]> { ["b"] = [0, 1] }, ["a"]))
        {
            using var reader = map.AcquireSnapshot();
            Assert.True(reader.TryGetVector("a", out _));
            Assert.False(reader.TryGetVector("b", out _));
        }
        using var after = map.AcquireSnapshot();
        Assert.Equal(1, after.Generation);
        Assert.True(after.TryGetVector("a", out _));
        Assert.Equal(0, map.Statistics.RetainedSlots);
    }

    [Theory]
    [InlineData(float.NaN, 1)]
    [InlineData(float.PositiveInfinity, 1)]
    [InlineData(0, 0)]
    public void InvalidVectorCannotChangeCommittedGeneration(float x, float y)
    {
        var map = new VectorSlotMap(2, 2);
        Publish(map, "a", [1, 0]);
        Assert.Throws<ArgumentException>(() => map.Prepare(new Dictionary<string, float[]> { ["a"] = [x, y] }, []));
        using var snapshot = map.AcquireSnapshot();
        Assert.Equal(1, snapshot.Generation);
        Assert.Equal(0, map.Statistics.RetainedSlots);
    }

    [Fact]
    public void DimensionsAndConcurrentPreparationsAreRejected()
    {
        var map = new VectorSlotMap(2, 2);
        Assert.Throws<ArgumentException>(() => map.Prepare(new Dictionary<string, float[]> { ["a"] = [1] }, []));
        using var prepared = map.Prepare(new Dictionary<string, float[]> { ["a"] = [1, 0] }, []);
        Assert.Throws<InvalidOperationException>(() => map.Prepare(new Dictionary<string, float[]>(), []));
        prepared.Commit();
        Assert.Throws<ObjectDisposedException>(prepared.Commit);
    }

    [Fact]
    public void RestartRebuildsDenseMap()
    {
        // Persisted identifiers are deliberately sparse; a new map allocates by live count.
        var persisted = new Dictionary<string, float[]>();
        for (var i = 0; i < 17; i++) persisted.Add($"space/chunk/{long.MaxValue - i * 1000}", [1, i + 1]);
        var map = new VectorSlotMap(2, 4);
        using (var rebuild = map.Prepare(persisted, [])) rebuild.Commit();
        using var snapshot = map.AcquireSnapshot();
        Assert.Equal(17, snapshot.Count);
        Assert.Equal(20, map.Statistics.AllocatedSlots);
        foreach (var pair in snapshot.Entries)
        {
            var squared = pair.Value.ToArray().Sum(value => (double)value * value);
            Assert.InRange(squared, .999999, 1.000001);
        }
    }

    [Fact]
    public void InputsAreCopiedAndExtremeFiniteValuesNormalize()
    {
        var map = new VectorSlotMap(2, 2);
        var input = new[] { float.MaxValue, float.MaxValue };
        using var prepared = map.Prepare(new Dictionary<string, float[]> { ["a"] = input }, []);
        input[0] = 0;
        prepared.Commit();
        using var snapshot = map.AcquireSnapshot();
        Assert.True(snapshot.TryGetVector("a", out var value));
        Assert.Equal(value.Span[0], value.Span[1]);
        Assert.InRange(value.Span[0], .7071067f, .7071069f);
    }

    [Fact]
    public void NextPublicationCompactsSparseSegmentsAfterReadersDrain()
    {
        var map = new VectorSlotMap(2, 4);
        var initial = Enumerable.Range(0, 40).ToDictionary(i => i.ToString(), i => new float[] { 1, i + 1 });
        using (var fill = map.Prepare(initial, [])) fill.Commit();
        var reader = map.AcquireSnapshot();
        using (var remove = map.Prepare(new Dictionary<string, float[]>(),
                   Enumerable.Range(0, 40).Where(i => i % 4 != 0).Select(i => i.ToString()))) remove.Commit();
        Assert.Equal(40, map.Statistics.AllocatedSlots);
        reader.Dispose();
        using (var compact = map.Prepare(new Dictionary<string, float[]>(), [])) compact.Commit();
        Assert.Equal(10, map.Statistics.LiveSlots);
        Assert.Equal(12, map.Statistics.AllocatedSlots);
        using var after = map.AcquireSnapshot();
        foreach (var i in Enumerable.Range(0, 40).Where(i => i % 4 == 0))
        {
            Assert.True(after.TryGetVector(i.ToString(), out var vector));
            Assert.InRange(vector.Span[1] / vector.Span[0], i + .9999f, i + 1.0001f);
        }
    }

    private static void Publish(VectorSlotMap map, string id, float[] vector)
    {
        using var mutation = map.Prepare(new Dictionary<string, float[]> { [id] = vector }, []);
        mutation.Commit();
    }
}
