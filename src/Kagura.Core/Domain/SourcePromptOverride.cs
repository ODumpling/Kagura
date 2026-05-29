using Kagura.Core.Agents;

namespace Kagura.Core.Domain;

/// <summary>
/// Per-Source per-Role prompt override (ADR 0002). Each row sets the prompt text a Source
/// uses for a given <see cref="Role"/>; absence of a row means "use the current built-in
/// default" — defaults are NOT eagerly persisted, they are resolved lazily at Agent spawn
/// time so new built-in defaults flow through to every uncustomised Source automatically.
/// </summary>
public class SourcePromptOverride
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SourceId { get; set; }
    public Source? Source { get; set; }

    /// <summary>The role this override applies to. (SourceId, Role) is unique.</summary>
    public Role Role { get; set; }

    /// <summary>The custom prompt text. Always non-null when the row exists — the row's
    /// existence is the "is customised" signal, not nullability of this column.</summary>
    public string PromptText { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
