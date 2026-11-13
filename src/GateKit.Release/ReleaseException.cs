namespace GateKit.Release;

/// <summary>An expected failure of an export or an install: printed as a message, not a stack.</summary>
public sealed class ReleaseException(string message) : Exception(message);
