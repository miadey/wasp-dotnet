using System;
using System.Collections.Generic;
using System.Text;

namespace Wasp.AspNetCore.Blazor.Wasp;

/// <summary>
/// A render batch — the HTML fragment and event-handler map produced
/// by rendering a component for a given path/state. Returned from
/// <c>GET /_wasp/render</c> (as JSON) and inline from
/// <c>POST /_wasp/event</c>.
/// </summary>
public sealed class WaspRenderBatch
{
    /// <summary>Stable id for this batch — sha256(canister state + route).</summary>
    public string BatchId { get; init; } = "";

    /// <summary>The HTML fragment to splice into the anchor element.</summary>
    public string Html { get; init; } = "";

    /// <summary>CSS selector for the DOM element whose innerHTML to replace.</summary>
    public string Anchor { get; init; } = "#wasp-root";
}

/// <summary>
/// Implementations produce a <see cref="WaspRenderBatch"/> for a given
/// route. Implementations MUST be:
///   - Deterministic: same input → same output (so the boundary's
///     v2-cert path can return cached responses safely).
///   - Idempotent w.r.t. canister state: invoking the renderer in a
///     query call must not mutate any persistent state. State reads
///     are fine; state writes belong to event handlers.
///   - Side-effect-free besides reads from DI singletons / stable memory.
///
/// v1 (gh #118): renderer is a plain delegate the developer supplies.
/// v2: framework will provide a stateless Blazor renderer subclass that
/// runs stock Razor components, embeds <c>data-wasp-evt-click</c>
/// attributes for <c>@onclick</c>, etc.
/// </summary>
public interface IWaspRenderer
{
    WaspRenderBatch Render(WaspRenderRequest req);
    /// <summary>
    /// Dispatch an event by handler id and return the post-event render
    /// batch. Mutating state is fine and expected here — this method
    /// only runs on the update-call path.
    /// </summary>
    WaspRenderBatch DispatchEvent(WaspEventRequest req);
}

public sealed class WaspRenderRequest
{
    public string Path { get; init; } = "/";
    public string? QueryString { get; init; }
    public string? LastBatchId { get; init; }
}

public sealed class WaspEventRequest
{
    public string Path { get; init; } = "/";
    public string HandlerId { get; init; } = "";
    public string EventName { get; init; } = "click";
    public string? LastBatchId { get; init; }
}
