namespace WinUse.Core.Java;

/// <summary>
/// One JVM's end of the Java Access Bridge conversation. <see cref="Exchange"/> sends <c>buffer[0..size)</c> (type id and
/// struct) and writes the JVM's answer back into the same bytes; it returns false when the JVM did not answer (timeout,
/// window gone, completion flag not set). A JVM that received a packet of the wrong wire format acknowledges it without
/// touching it, so callers judge success by the result fields, not by the return value alone.
/// </summary>
internal interface IJabChannel : IDisposable
{
    bool Exchange(byte[] buffer, int size);
}
