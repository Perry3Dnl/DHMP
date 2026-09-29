using System.Buffers.Binary;

namespace DHMP.Server;

/// <summary>
/// Optional Latest-mode freshness filter using an application-owned 64-bit generation
/// field inside the fixed record. It adds no DHMP wire bytes.
/// </summary>
/// <remarks>
/// Single-owner only. Serial comparison uses modulo-2^64 half-range ordering so normal
/// wraparound is handled. Equal generations and values at least half the serial space
/// behind the current value are treated as stale.
/// </remarks>
public sealed class DhmpLatestGenerationFilter
{
    private readonly int _recordSize;
    private readonly int _generationOffset;
    private readonly DhmpGenerationByteOrder _byteOrder;

    private bool _hasGeneration;
    private ulong _latestGeneration;
    private long _acceptedRecords;
    private long _staleRecords;

    public DhmpLatestGenerationFilter(
        int recordSize,
        int generationOffset = 0,
        DhmpGenerationByteOrder byteOrder =
            DhmpGenerationByteOrder.BigEndian)
    {
        if (recordSize < sizeof(ulong))
            throw new ArgumentOutOfRangeException(nameof(recordSize));

        if (generationOffset < 0 ||
            generationOffset > recordSize - sizeof(ulong))
            throw new ArgumentOutOfRangeException(nameof(generationOffset));

        if (byteOrder is not DhmpGenerationByteOrder.BigEndian and
            not DhmpGenerationByteOrder.LittleEndian)
            throw new ArgumentOutOfRangeException(nameof(byteOrder));

        _recordSize = recordSize;
        _generationOffset = generationOffset;
        _byteOrder = byteOrder;
    }

    public bool HasGeneration => _hasGeneration;
    public ulong LatestGeneration => _latestGeneration;
    public long AcceptedRecords => _acceptedRecords;
    public long StaleRecords => _staleRecords;

    public bool TryPublish(
        ReadOnlySpan<byte> record,
        Action<ReadOnlySpan<byte>> publish)
    {
        ArgumentNullException.ThrowIfNull(publish);

        if (record.Length != _recordSize)
            throw new ArgumentException(
                $"Freshness filter expects exactly one {_recordSize}-byte record.",
                nameof(record));

        ulong candidate = ReadGeneration(record);

        if (_hasGeneration &&
            !IsNewer(candidate, _latestGeneration))
        {
            _staleRecords++;
            return false;
        }

        _latestGeneration = candidate;
        _hasGeneration = true;
        _acceptedRecords++;
        publish(record);
        return true;
    }

    public void Reset()
    {
        _hasGeneration = false;
        _latestGeneration = 0;
        _acceptedRecords = 0;
        _staleRecords = 0;
    }

    public static bool IsNewer(
        ulong candidate,
        ulong current)
    {
        ulong delta = unchecked(candidate - current);
        return delta != 0 &&
               delta < (1UL << 63);
    }

    private ulong ReadGeneration(ReadOnlySpan<byte> record)
    {
        ReadOnlySpan<byte> field =
            record.Slice(
                _generationOffset,
                sizeof(ulong));

        return _byteOrder == DhmpGenerationByteOrder.BigEndian
            ? BinaryPrimitives.ReadUInt64BigEndian(field)
            : BinaryPrimitives.ReadUInt64LittleEndian(field);
    }
}
