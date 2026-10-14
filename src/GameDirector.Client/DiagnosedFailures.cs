namespace GameDirector.Client;

// Client owns these diagnoses but not HTTP policy. Workbench maps the types;
// CLI/MCP keep reading Message. Unknown BCL exceptions are deliberately distinct.
public class StoreIntegrityException(string message, Exception? inner = null) : IOException(message, inner);
public sealed class EngineUnavailableException(string message, Exception? inner = null) : InvalidOperationException(message, inner);
public sealed class EngineProtocolException(string message, Exception? inner = null) : InvalidOperationException(message, inner);
public sealed class MediaFailureException(string message, Exception? inner = null) : InvalidOperationException(message, inner);
public sealed class StateConflictException(string message, bool retryable = false, Exception? inner = null) : InvalidOperationException(message, inner)
{
    public bool Retryable { get; } = retryable;
}
