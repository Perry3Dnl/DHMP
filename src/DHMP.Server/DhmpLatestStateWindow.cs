namespace DHMP.Server;

/// <summary>
/// Fixed three-record receive-side state window for Latest-mode smoothing.
/// Slots are permanent; publishing is bounded and allocation-free after construction.
/// </summary>
public sealed class DhmpLatestStateWindow
{
    public const int Capacity = 3;
    private readonly object _gate = new();
    private readonly int _recordSize;
    private readonly byte[] _slots;
    private int _nextWrite;
    private int _count;
    private long _recordsObserved;
    private long _recordsOverwritten;

    public DhmpLatestStateWindow(int recordSize)
    {
        if (recordSize <= 0 || recordSize > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(recordSize));
        _recordSize = recordSize;
        _slots = GC.AllocateUninitializedArray<byte>(checked(recordSize * Capacity));
    }

    public int RecordSize => _recordSize;
    public int Count { get { lock (_gate) return _count; } }
    public long RecordsObserved => Interlocked.Read(ref _recordsObserved);
    public long RecordsOverwritten => Interlocked.Read(ref _recordsOverwritten);

    public void PublishPacket(ReadOnlySpan<byte> packet)
    {
        if (packet.IsEmpty || packet.Length % _recordSize != 0)
            throw new ArgumentException("Latest state window requires only complete records.", nameof(packet));

        int records = packet.Length / _recordSize;
        int keep = Math.Min(records, Capacity);
        int first = records - keep;

        lock (_gate)
        {
            if (records > keep)
                Interlocked.Add(ref _recordsOverwritten, records - keep);

            for (int index = first; index < records; index++)
            {
                packet.Slice(index * _recordSize, _recordSize)
                    .CopyTo(_slots.AsSpan(_nextWrite * _recordSize, _recordSize));

                _nextWrite = (_nextWrite + 1) % Capacity;
                if (_count < Capacity) _count++;
                else Interlocked.Increment(ref _recordsOverwritten);
            }

            Interlocked.Add(ref _recordsObserved, records);
        }
    }

    public int CopyNewestTo(Span<byte> destination)
    {
        lock (_gate)
        {
            int required = _count * _recordSize;
            if (destination.Length < required)
                throw new ArgumentException($"Destination must fit {required} bytes for the current Latest state window.", nameof(destination));

            int oldest = (_nextWrite - _count + Capacity) % Capacity;
            for (int index = 0; index < _count; index++)
            {
                int slot = (oldest + index) % Capacity;
                _slots.AsSpan(slot * _recordSize, _recordSize)
                    .CopyTo(destination.Slice(index * _recordSize, _recordSize));
            }
            return _count;
        }
    }
}
