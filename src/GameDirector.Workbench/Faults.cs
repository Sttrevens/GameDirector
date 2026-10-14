using System.Text.Json;
using GameDirector.Client;
using GameDirector.Core.Dsl;

namespace GameDirector.Workbench;

// Sites that know the cause state whether the SAME request can succeed later.
// Unknown exceptions are server faults, never inferred to be caller mistakes.
public abstract class ServiceFault(string message, Exception? inner = null) : Exception(message, inner)
{
    public abstract string Code { get; }
    public virtual string MessageCode => Code;
    public abstract int Status { get; }
    public virtual bool Retryable => false;
}
public sealed class UnauthorizedFault() : ServiceFault("Open the Workbench from Unity or run gd workbench --open. Local API clients must read the instance token from .service.json; pair GAMEDIRECTOR_WORKBENCH with GAMEDIRECTOR_WORKBENCH_TOKEN for an explicit endpoint.")
{
    public override string Code => "unauthorized";
    public override int Status => 401;
}
public sealed class EngineUnavailableFault(string message, Exception? inner = null) : ServiceFault(message, inner)
{
    public override string Code => "engine_unavailable";
    public override int Status => 503;
    public override bool Retryable => true;
}
public sealed class QueueFullFault(string message) : ServiceFault(message)
{
    public override string Code => "queue_full";
    public override int Status => 429;
    public override bool Retryable => true;
}
public sealed class ProviderNotConfiguredFault(string message) : ServiceFault(message)
{
    public override string Code => "provider_not_configured";
    public override int Status => 412;
}
public sealed class InvalidInputFault(string message, Exception? inner = null, string? messageCode = null) : ServiceFault(message, inner)
{
    public override string Code => "invalid_input";
    public override string MessageCode => messageCode ?? Code;
    public override int Status => 422;
}
public sealed class NotFoundFault(string message, Exception? inner = null) : ServiceFault(message, inner)
{
    public override string Code => "not_found";
    public override int Status => 404;
}
public sealed class StoreIntegrityFault(string message, Exception? inner = null) : ServiceFault(message, inner)
{
    public override string Code => "store_integrity";
    public override int Status => 500;
}
public sealed class ProviderFailureFault(string message, Exception? inner = null) : ServiceFault(message, inner)
{
    public override string Code => "provider_failure";
    public override int Status => 500;
}
public sealed class RequestConflictFault(string message) : ServiceFault(message)
{
    public override string Code => "request_conflict";
    public override int Status => 409;
}
public sealed class StateConflictFault(string message, bool retryable, Exception? inner = null) : ServiceFault(message, inner)
{
    public override string Code => "state_conflict";
    public override int Status => 409;
    public override bool Retryable { get; } = retryable;
}
// A failed media process/verification can be bytes, tooling or infrastructure.
// Without a reliable decoder verdict we must not blame the upload. No automatic
// replay: fix/inspect the toolchain first. Explicit lower-level diagnoses keep
// their messages; this boundary also diagnoses unexpected media-tool exceptions.
public sealed class MediaFailureFault(string message, Exception inner) : ServiceFault(message, inner)
{
    public override string Code => "media_failure";
    public override int Status => 500;
}

// An answering bridge's server/protocol failure is not replayable input or a
// reachability diagnosis. Preserve the answer for repair, never retry it blindly.
public sealed class EngineRejectedFault(string message, Exception inner) : ServiceFault(message, inner)
{
    public override string Code => "engine_rejected";
    public override int Status => 502;
}

public static class Faults
{
    public static ServiceFault Bridge(BridgeException ex, string context = "")
    {
        var message = context + ex.Message;
        if (ex.Kind != BridgeException.FailureKind.Rejected) return new EngineUnavailableFault(message, ex);
        if(ex.StatusCode == System.Net.HttpStatusCode.NotFound)return new NotFoundFault(message,ex);
        if(ex.StatusCode == System.Net.HttpStatusCode.InternalServerError || ex.Code == BridgeErrors.Internal.Code)return new EngineRejectedFault(message,ex);
        if(ex.Code == BridgeErrors.InvalidRequest.Code)return new InvalidInputFault(message,ex);
        if(ex.Code == BridgeErrors.StateConflict.Code)return new StateConflictFault(message,false,ex);
        return new EngineRejectedFault(message,ex);
    }
    public sealed record Mapped(int Status, string Code, bool Retryable, string Message, object? Current = null, string? MessageCode = null)
    {
        public bool Unknown => Code == "internal";
    }
    public static Mapped Of(Exception ex) => ex switch
    {
        DocumentConflict e => new(409, "document_conflict", false, e.Message, e.Current),
        RevisionRequired e => new(428, "revision_required", false, e.Message),
        ServiceFault f => new(f.Status, f.Code, f.Retryable, f.Message, MessageCode: f.MessageCode),
        BridgeException e => Of(Bridge(e)),
        StoreIntegrityException e => Of(new StoreIntegrityFault(e.Message, e)),
        EngineUnavailableException e => Of(new EngineUnavailableFault(e.Message, e)),
        EngineProtocolException e => Of(new EngineRejectedFault(e.Message, e)),
        MediaFailureException e => new(500, "media_failure", false, e.Message),
        StateConflictException e => Of(new StateConflictFault(e.Message, e.Retryable)),
        KeyNotFoundException e => new(404, "not_found", false, e.Message),
        ArgumentException e => new(422, "invalid_input", false, e.Message),
        AuthoredInputException e when e.Document == AuthoredDocumentKind.Manifest => Of(new EngineRejectedFault(e.Message, e)),
        AuthoredInputException e => new(422, "invalid_input", false, e.Message),
        // Only JSON at the request boundary is caller input. Stored/provider
        // JSON and arbitrary Format/NotSupported exceptions are NOT 422s.
        BadHttpRequestException e when e.StatusCode == 400 => new(422, "invalid_input", false, "The request body is not valid for this endpoint: " + BodyError(e)),
        BadHttpRequestException e => new(e.StatusCode, "bad_request", false, "The request could not be read."),
        _ => new(500, "internal", false, "Unexpected Workbench error; the detail was logged on the server."),
    };
    // Keep error/current exactly where Unity and MCP already read them.
    public static object Body(Mapped fault) => fault.Current != null
        ? new { error = fault.Message, code = fault.Code, retryable = fault.Retryable, current = fault.Current, messageCode = fault.MessageCode ?? fault.Code }
        : new { error = fault.Message, code = fault.Code, retryable = fault.Retryable, messageCode = fault.MessageCode ?? fault.Code };
    private static string BodyError(BadHttpRequestException ex)
    {
        var message = ex.InnerException is JsonException inner ? inner.Message : ex.Message;
        var cut = message.IndexOf(" Path: ", StringComparison.Ordinal);
        return cut < 0 ? message : message[..cut];
    }
}
