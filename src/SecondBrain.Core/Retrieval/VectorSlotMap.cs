namespace SecondBrain.Core.Retrieval;

/// <summary>
/// Segmented, dense vector storage independent of database row ids. Prepared changes become
/// visible together; a reader lease keeps its generation's vectors alive until disposal.
/// The publication adapter must commit a prepared change only after its database commit and
/// synchronize acquisition of database and vector snapshots at that boundary.
/// </summary>
public sealed class VectorSlotMap
{
    private readonly object gate = new();
    private readonly int dimensions;
    private readonly int segmentCapacity;
    private readonly HashSet<Segment> segments = [];
    private Version current = new(0, new(StringComparer.Ordinal));
    private bool preparing;

    public VectorSlotMap(int dimensions, int segmentCapacity = 65_536)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dimensions);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(segmentCapacity);
        _ = checked(dimensions * segmentCapacity);
        this.dimensions = dimensions;
        this.segmentCapacity = segmentCapacity;
    }

    public VectorSlotStatistics Statistics
    {
        get
        {
            lock (gate)
            {
                var occupied = segments.Sum(static segment => segment.Used);
                var capacity = (long)segments.Count * segmentCapacity;
                return new(current.Slots.Count, occupied - current.Slots.Count,
                    capacity, checked(capacity * dimensions * sizeof(float)));
            }
        }
    }

    public Snapshot AcquireSnapshot()
    {
        lock (gate)
        {
            current.References++;
            return new(this, current);
        }
    }

    /// <summary>
    /// Stages a single publication. Inputs are copied and unit-normalized; removals precede
    /// upserts. Disposing without committing releases all staged slots. Serialize writers.
    /// </summary>
    public Mutation Prepare(IReadOnlyDictionary<string, float[]> upserts, IEnumerable<string> removals)
    {
        ArgumentNullException.ThrowIfNull(upserts);
        ArgumentNullException.ThrowIfNull(removals);
        var removed = removals.ToArray();
        foreach (var key in removed) ArgumentException.ThrowIfNullOrWhiteSpace(key);

        lock (gate)
        {
            if (preparing) throw new InvalidOperationException("A vector publication is already prepared.");
            CompactUnleasedSegments();
            var slots = new Dictionary<string, Slot>(current.Slots, StringComparer.Ordinal);
            foreach (var slot in slots.Values) slot.Segment.References[slot.Offset]++;
            try
            {
                foreach (var key in removed)
                    if (slots.Remove(key, out var old)) ReleaseSlot(old);
                foreach (var (key, values) in upserts)
                {
                    ArgumentException.ThrowIfNullOrWhiteSpace(key);
                    var magnitude = Magnitude(values);
                    // Keep the old entry until allocation succeeds so rollback owns every slot.
                    var replacement = Allocate(values, magnitude);
                    if (slots.Remove(key, out var old)) ReleaseSlot(old);
                    slots.Add(key, replacement);
                }
                var next = new Version(checked(current.Generation + 1), slots);
                preparing = true;
                return new(this, next);
            }
            catch
            {
                foreach (var slot in slots.Values) ReleaseSlot(slot);
                throw;
            }
        }
    }

    private double Magnitude(float[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Length != dimensions) throw new ArgumentException("Vector dimensions do not match the space.", nameof(values));
        double squared = 0;
        foreach (var value in values)
        {
            if (!float.IsFinite(value)) throw new ArgumentException("Vectors must contain finite values.", nameof(values));
            squared += (double)value * value;
        }
        if (squared == 0) throw new ArgumentException("A zero vector cannot be normalized.", nameof(values));
        return Math.Sqrt(squared);
    }

    private Slot Allocate(float[] values, double magnitude)
    {
        var segment = segments.FirstOrDefault(candidate => candidate.Used < segmentCapacity);
        if (segment is null)
        {
            segment = new Segment(dimensions, segmentCapacity);
            segments.Add(segment);
        }
        var offset = segment.Free.TryPop(out var freed) ? freed : segment.Next++;
        if (magnitude == 1) values.CopyTo(segment.Values, offset * dimensions);
        else for (var i = 0; i < dimensions; i++) segment.Values[offset * dimensions + i] = (float)(values[i] / magnitude);
        segment.References[offset] = 1;
        segment.Used++;
        return new(segment, offset);
    }

    private void ReleaseSlot(Slot slot)
    {
        if (--slot.Segment.References[slot.Offset] != 0) return;
        Array.Clear(slot.Segment.Values, slot.Offset * dimensions, dimensions);
        slot.Segment.Free.Push(slot.Offset);
        if (--slot.Segment.Used == 0) segments.Remove(slot.Segment);
    }

    private void ReleaseVersion(Version version)
    {
        if (--version.References != 0) return;
        foreach (var slot in version.Slots.Values) ReleaseSlot(slot);
    }

    // Run before preparing a database publication, never in Commit: compaction may allocate
    // bookkeeping arrays. With no leased generations, vectors can move into existing holes.
    private void CompactUnleasedSegments()
    {
        if (current.References != 1 || segments.Sum(static segment => segment.Used) != current.Slots.Count) return;
        // Unchanged slots may still be shared with an older generation even when no retired
        // slots exist, so inspect references as well as the occupied count.
        if (current.Slots.Values.Any(slot => slot.Segment.References[slot.Offset] != 1)) return;
        var needed = (int)(((long)current.Slots.Count + segmentCapacity - 1) / segmentCapacity);
        if (segments.Count <= needed) return;
        var retained = segments.OrderByDescending(static segment => segment.Used).Take(needed).ToHashSet();
        var moving = current.Slots.Where(pair => !retained.Contains(pair.Value.Segment)).ToArray();
        foreach (var (key, old) in moving)
        {
            var destination = retained.First(segment => segment.Used < segmentCapacity);
            var offset = destination.Free.TryPop(out var freed) ? freed : destination.Next++;
            old.Segment.Values.AsSpan(old.Offset * dimensions, dimensions)
                .CopyTo(destination.Values.AsSpan(offset * dimensions, dimensions));
            destination.References[offset] = 1;
            destination.Used++;
            current.Slots[key] = new(destination, offset);
            ReleaseSlot(old);
        }
    }

    internal sealed class Segment(int dimensions, int capacity)
    {
        internal float[] Values { get; } = new float[checked(dimensions * capacity)];
        internal int[] References { get; } = new int[capacity];
        internal Stack<int> Free { get; } = new();
        internal int Next;
        internal int Used;
    }

    internal readonly record struct Slot(Segment Segment, int Offset);

    internal sealed class Version(long generation, Dictionary<string, Slot> slots)
    {
        internal long Generation { get; } = generation;
        internal Dictionary<string, Slot> Slots { get; } = slots;
        internal int References = 1;
    }

    public sealed class Mutation : IDisposable
    {
        private readonly VectorSlotMap owner;
        private Version? version;

        internal Mutation(VectorSlotMap owner, Version version)
        {
            this.owner = owner;
            this.version = version;
        }

        public void Commit()
        {
            lock (owner.gate)
            {
                ObjectDisposedException.ThrowIf(version is null, this);
                var previous = owner.current;
                owner.current = version;
                version = null;
                owner.preparing = false;
                owner.ReleaseVersion(previous);
            }
        }

        public void Dispose()
        {
            lock (owner.gate)
            {
                if (version is null) return;
                owner.ReleaseVersion(version);
                version = null;
                owner.preparing = false;
            }
        }
    }

    /// <summary>
    /// Returned vector memory is valid only for the lifetime of this reader lease.
    /// Concurrent reads are supported; callers must finish reads before disposing the lease.
    /// </summary>
    public sealed class Snapshot : IDisposable
    {
        private readonly VectorSlotMap owner;
        private Version? version;

        internal Snapshot(VectorSlotMap owner, Version version)
        {
            this.owner = owner;
            this.version = version;
        }

        public long Generation
        {
            get { lock (owner.gate) return GetVersion().Generation; }
        }

        public int Count
        {
            get { lock (owner.gate) return GetVersion().Slots.Count; }
        }

        public IReadOnlyList<KeyValuePair<string, ReadOnlyMemory<float>>> Entries
        {
            get
            {
                lock (owner.gate)
                    return GetVersion().Slots.Select(pair =>
                        new KeyValuePair<string, ReadOnlyMemory<float>>(pair.Key, Memory(pair.Value))).ToArray();
            }
        }

        public bool TryGetVector(string key, out ReadOnlyMemory<float> vector)
        {
            // A live lease pins both the immutable slot dictionary and its vector storage.
            // Writers use another version, and compaction requires no outstanding leases.
            var leased = Volatile.Read(ref version);
            ObjectDisposedException.ThrowIf(leased is null, this);
            if (leased.Slots.TryGetValue(key, out var slot))
            {
                vector = Memory(slot);
                return true;
            }
            vector = default;
            return false;
        }

        public void Dispose()
        {
            lock (owner.gate)
            {
                if (version is null) return;
                owner.ReleaseVersion(version);
                version = null;
            }
        }

        private Version GetVersion()
        {
            ObjectDisposedException.ThrowIf(version is null, this);
            return version;
        }

        private ReadOnlyMemory<float> Memory(Slot slot) =>
            slot.Segment.Values.AsMemory(slot.Offset * owner.dimensions, owner.dimensions);
    }
}

public sealed record VectorSlotStatistics(int LiveSlots, int RetainedSlots, long AllocatedSlots, long VectorBytes);
