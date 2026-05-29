using Kagura.Core.Domain;

namespace Kagura.Core.Agents;

/// <summary>
/// Resolves the prompt template a Source should use for a given <see cref="Role"/> at Agent
/// spawn time (ADR 0002). If the Source has a <see cref="SourcePromptOverride"/> row for
/// that Role its <c>PromptText</c> wins; otherwise the current built-in default from
/// <see cref="RolePromptDefaults"/> is returned. Defaults are never written into the override
/// table — improving a built-in default flows through to every uncustomised Source.
///
/// The resolver is intentionally a pure function over an already-loaded
/// <see cref="Source.PromptOverrides"/> collection. Callers must include the navigation
/// (<c>db.Sources.Include(s =&gt; s.PromptOverrides)</c>) before invoking this; we do not
/// open a DB scope here so the resolver can be unit-tested without EF.
/// </summary>
public interface IPromptResolver
{
    string Resolve(Source source, Role role);
}

public sealed class PromptResolver : IPromptResolver
{
    public string Resolve(Source source, Role role)
    {
        var ov = source.PromptOverrides.FirstOrDefault(o => o.Role == role);
        return ov is null ? RolePromptDefaults.For(role) : ov.PromptText;
    }
}
