namespace Kagura.Core.Domain;

public abstract record SourceConfig;

public record MarkdownConfig(string IssuesPath) : SourceConfig;

public record GitHubConfig(string Owner, string Repo, string? Token, string? Labels = null) : SourceConfig;

public record AzureDevOpsConfig(string Organization, string Project, string? Pat, string? Query = null) : SourceConfig;

public record BeadsConfig(string? Status = null) : SourceConfig;
