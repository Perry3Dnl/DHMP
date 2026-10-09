namespace DHMP.Protocol;

public enum DhmpProcessingMode
{
    /// <summary>Publish all complete received messages in arrival order through Ring-3 then FIFO; no network delivery guarantee.</summary>
    Sequential = 0,

    /// <summary>Publish the last complete record of a packet, in arrival order. Wire freshness ordering is not implemented.</summary>
    Latest = 1,

    /// <summary>
    /// Experimental copy-avoidance mode. Plaintext fixed-slot transports receive
    /// directly into the Sequential FIFO and bypass Ring-3. FIFO order is
    /// preserved, but when the FIFO is full the receive loop stops posting a
    /// socket receive earlier, so kernel/network loss under overload is easier
    /// to trigger. No ACK/retransmission or delivery guarantee is added.
    /// </summary>
    UnsafeSequential = 2
}
