namespace WinUse.Core.Java;

/// <summary>Where the bridge conversation with a Java window's JVM stopped (<see cref="JabWindowDiagnosis.Stage"/>).</summary>
public static class JabStage
{
    /// <summary>The client itself is off (<c>WINUSE_JAB=off</c>) or its window could not be created.</summary>
    public const string ClientUnavailable = "client_unavailable";
    /// <summary>The JVM never sent <c>AccessBridge-FromJava-Hello</c> to the client window.</summary>
    public const string NoHello = "no_hello";
    /// <summary>The JVM said hello but the memory-mapped-file handshake failed.</summary>
    public const string HandshakeFailed = "handshake_failed";
    /// <summary>The JVM is connected but did not process a request within the timeout.</summary>
    public const string Timeout = "timeout";
    /// <summary>The JVM is connected but a request could not be delivered to its bridge window.</summary>
    public const string SendFailed = "send_failed";
    /// <summary>The JVM processed a request but wrote no answer (wire format mismatch).</summary>
    public const string NoAnswer = "no_answer";
    /// <summary>The JVM answers but says the window is not one of its Java windows.</summary>
    public const string NotOwned = "not_owned";
}

/// <summary>One connected JVM as the diagnosis lists it (no window titles, no element data).</summary>
public sealed record JabJvmSummary(int Pid, string WireFormat, bool WireFormatAssumed, bool? Is32Bit, string? LastFailure);

/// <summary>
/// Why a Java window cannot be read through the bridge, with the facts the caller needs to decide what to do: the JVM's
/// pid and loaded bridge DLL, the stage the connection reached, the request timeout, and the JVMs that are connected.
/// Contains no window titles or element data.
/// </summary>
public sealed record JabWindowDiagnosis(
    string Stage,
    string Reason,
    int JvmPid,
    nint ClientWindow,
    string? BridgeModule,
    string? WireFormat,
    bool? Is32Bit,
    bool JvmConnected,
    int TimeoutMs,
    IReadOnlyList<JabJvmSummary> ConnectedJvms,
    /// <summary>
    /// When an action sent to this JVM never answered (a Swing action that opened a modal dialog returns only when the
    /// dialog closes), the time it was sent: the bridge of this JVM stays blocked until that dialog is gone.
    /// </summary>
    DateTimeOffset? BlockedByActionAt = null);
