namespace GateKit.Stand;

/// <summary>A failure the stand explains in one message (printed as "ERROR: ..." by the CLI).</summary>
public sealed class StandException(string message, Exception? inner = null) : Exception(message, inner);
