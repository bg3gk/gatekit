namespace GateKit.Copies;

/// <summary>A failure `gatekit` explains in one message (printed as "ERROR: ..." by the CLI).</summary>
public class CopyException(string message, Exception? inner = null) : Exception(message, inner);

