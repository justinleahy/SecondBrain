using System.Diagnostics;
using SecondBrain.Core.Retrieval;
using Xunit;
using Xunit.Abstractions;

namespace SecondBrain.Core.Tests.Retrieval;

public sealed class VectorScaleQualification(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Qualification")]
    public void MillionChunkDenseSlotsRemainBoundedUnderReplacement()
    {
        const int count = 1_000_000, dimensions = 1024, batch = 10_000;
        var vector = new float[dimensions]; vector[0] = 1;
        var map = new VectorSlotMap(dimensions);
        var timer = Stopwatch.StartNew();
        var inputs = Enumerable.Range(0, count).ToDictionary(i => $"chunk-{long.MaxValue - i}", _ => vector, StringComparer.Ordinal);
        using (var initial = map.Prepare(inputs, [])) initial.Commit();
        inputs.Clear();
        output.WriteLine($"Initial load: {timer.Elapsed.TotalSeconds:F2}s; {map.Statistics.VectorBytes} vector bytes.");
        timer.Restart();
        for (var round = 0; round < 10; round++)
        {
            var replacements = Enumerable.Range(0, batch).ToDictionary(i => $"chunk-{long.MaxValue - (round * batch + i)}", _ => vector, StringComparer.Ordinal);
            using (var old = map.AcquireSnapshot())
            {
                using (var update = map.Prepare(replacements, [])) update.Commit();
                Assert.Equal(batch, map.Statistics.RetainedSlots);
                Assert.Equal(count, old.Count);
            }
            Assert.Equal(0, map.Statistics.RetainedSlots);
            Assert.Equal(count, map.Statistics.LiveSlots);
            Assert.InRange(map.Statistics.AllocatedSlots, count, count + 2 * 65_536);
        }
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        output.WriteLine($"100k replacements: {timer.Elapsed.TotalSeconds:F2}s; vector bytes={map.Statistics.VectorBytes}; managed bytes={GC.GetTotalMemory(false)}; working set={Process.GetCurrentProcess().WorkingSet64}.");
        using var snapshot = map.AcquireSnapshot();
        Assert.True(snapshot.TryGetVector($"chunk-{long.MaxValue}", out var retained));
        Assert.Equal(1f, retained.Span[0]);
    }
}
