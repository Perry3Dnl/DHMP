namespace DHMP.Connector;

/// <summary>
/// Per-burst pacing options. This is an application send policy and adds no DHMP wire bytes.
/// The connector-wide Pmax remains the hard upper bound.
/// </summary>
public sealed class DhmpBurstOptions
{
    /// <summary>Requested burst rate in records per second.</summary>
    public int RecordsPerSecond { get; init; } = 1000;

    internal void Validate(int maximumRecordsPerSecond)
    {
        if (RecordsPerSecond <= 0)
            throw new ArgumentOutOfRangeException(nameof(RecordsPerSecond));

        if (RecordsPerSecond > maximumRecordsPerSecond)
            throw new ArgumentOutOfRangeException(
                nameof(RecordsPerSecond),
                $"Burst rate cannot exceed the connection limit of {maximumRecordsPerSecond} records/s.");
    }
}

/// <summary>Summary of one completed fire-and-forget burst submission.</summary>
public readonly record struct DhmpBurstResult(
    int RecordsSent,
    TimeSpan Elapsed);
