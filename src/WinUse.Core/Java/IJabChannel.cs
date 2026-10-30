namespace WinUse.Core.Java;

/// <summary>How one request to a JVM ended.</summary>
public enum JabExchange
{
    /// <summary>The JVM processed the packet and set the completion flag (its answer is in the buffer).</summary>
    Answered,
    /// <summary>The JVM's bridge window did not process the message within the timeout: the Java side is hung or suspended.</summary>
    Timeout,
    /// <summary>The message could not be delivered (window gone, blocked by UIPI, ...).</summary>
    SendFailed,
    /// <summary>The JVM processed the message but never set the completion flag.</summary>
    NoAnswer,
}

/// <summary>
/// One JVM's end of the Java Access Bridge conversation. <see cref="Exchange"/> sends <c>buffer[0..size)</c> (type id and
/// struct) and writes the JVM's answer back into the same bytes; it returns <see cref="JabExchange.Answered"/> only when the
/// JVM set the completion flag. A JVM that received a packet of the wrong wire format acknowledges it without touching it,
/// so callers judge success by the result fields, not by the outcome alone.
/// </summary>
internal interface IJabChannel : IDisposable
{
    JabExchange Exchange(byte[] buffer, int size);
}
