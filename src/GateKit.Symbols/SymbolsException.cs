namespace GateKit.Symbols;

/// <summary>An expected failure of a symbol or pseudo-code command (missing database, nothing
/// found where something must be): the command prints the message and exits with 1.</summary>
public sealed class SymbolsException(string message) : Exception(message);
