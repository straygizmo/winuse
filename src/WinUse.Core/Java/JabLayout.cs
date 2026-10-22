namespace WinUse.Core.Java;

/// <summary>
/// How a JVM's Java-side bridge lays out the wire packets. The only difference is the width of <c>JOBJECT64</c>:
/// 8 bytes (<c>jlong</c>, 8-aligned) in the wide format of <c>JavaAccessBridge-32/64.dll</c> and Java 9+'s
/// <c>javaaccessbridge.dll</c>; 4 bytes (<c>jobject</c>) in the legacy format of the suffix-less <c>JavaAccessBridge.dll</c>
/// that 32-bit Java 7/8 JREs ship. A JVM silently drops packets whose size does not match its own format.
/// </summary>
public enum JabLayout { Wide, Legacy }
