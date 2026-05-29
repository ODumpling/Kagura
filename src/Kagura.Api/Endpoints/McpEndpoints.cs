using System.Text.Json;
using Kagura.Core.Agents;
using Kagura.Core.Agents.Mcp;

namespace Kagura.Api.Endpoints;

/// <summary>
/// Per CONTEXT.md → "MCP transport" / ADR 0001: an in-process MCP server hosted with the API at
/// <c>/mcp/{runId}</c>. Identity is structural — Claude is launched with <c>--mcp-config</c>
/// pointing at the Agent's own URL, so the server cannot confuse one Agent's submission for
/// another's. A submission to a stale <c>runId</c> returns a JSON-RPC error.
///
/// This is a hand-rolled JSON-RPC 2.0 endpoint implementing exactly the two MCP methods the
/// Claude CLI needs to call our submission tools: <c>tools/list</c> and <c>tools/call</c>.
/// The surface is deliberately small (Lean MCP — only submission tools, no read context, no
/// git/gh wrapping), so a stub here is preferred over taking on the NuGet
/// <c>ModelContextProtocol</c> SDK as a dependency.
/// </summary>
public static class McpEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static IEndpointRouteBuilder MapMcpEndpoints(this IEndpointRouteBuilder app)
    {
        // POST /mcp/{runId} — JSON-RPC 2.0 over HTTP. One URL per Agent.
        // Additionally accept GET / OPTIONS for clients that probe the URL before posting.
        app.MapGet("/mcp/{runId:guid}", (Guid runId, IAgentSubmissionCoordinator submissions) =>
            submissions.IsActive(runId)
                ? Results.Ok(new { app = "Kagura", mcp = "ready", runId })
                : Results.NotFound(new { error = $"No active MCP submission registered for run {runId}." }));

        app.MapPost("/mcp/{runId:guid}", async (
            Guid runId,
            HttpRequest req,
            IAgentSubmissionCoordinator submissions,
            ILoggerFactory loggers,
            CancellationToken ct) =>
        {
            var log = loggers.CreateLogger("Kagura.Api.Mcp");

            JsonElement message;
            try
            {
                using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: ct);
                message = doc.RootElement.Clone();
            }
            catch (JsonException ex)
            {
                return ParseError(id: null, ex.Message);
            }

            if (!message.TryGetProperty("jsonrpc", out var v) || v.GetString() != "2.0")
                return InvalidRequest(IdOf(message), "Only JSON-RPC 2.0 is supported.");

            var method = message.TryGetProperty("method", out var m) ? m.GetString() : null;
            var id = IdOf(message);

            return method switch
            {
                "initialize" => Ok(id, new
                {
                    protocolVersion = "2024-11-05",
                    capabilities = new { tools = new { } },
                    serverInfo = new { name = "kagura", version = "0.1.0" },
                }),
                "tools/list" => Ok(id, new
                {
                    tools = new object[]
                    {
                        new
                        {
                            name = Role.Triage.McpSubmitToolName(),
                            description = "Submit the proposed triage tasks for this work item. Calling this tool ends the Triage agent.",
                            inputSchema = TriageInputSchema,
                        },
                        new
                        {
                            name = Role.Grill.McpSubmitToolName(),
                            description = "Submit the refined work-item body produced by the grilling conversation. Calling this tool ends the Grill agent.",
                            inputSchema = GrillInputSchema,
                        },
                        new
                        {
                            name = Role.AutoReview.McpSubmitToolName(),
                            description = "Submit the AutoReview verdict for this task's diff. Set `autoMerge` true if the diff is safe to merge automatically; false if it needs human review. Calling this tool ends the AutoReview agent.",
                            inputSchema = ReviewInputSchema,
                        },
                        new
                        {
                            name = Role.MergeResolver.McpSubmitToolName(),
                            description = "Submit the outcome of an attempted merge-conflict resolution. Set `resolved` true only after `git add`/`git commit` finalized the merge; otherwise set it false and leave the worktree mid-merge. Calling this tool ends the MergeResolver agent.",
                            inputSchema = MergeResolutionInputSchema,
                        },
                    },
                }),
                "tools/call" => await CallToolAsync(id, message, runId, submissions, log),
                "notifications/initialized" or "notifications/cancelled" =>
                    // Notifications have no `id` — no response expected.
                    Results.NoContent(),
                _ => MethodNotFound(id, method),
            };
        });

        return app;
    }

    // JSON Schema for kagura.submit_triage's input. Mirrors TriageSubmission.
    private static readonly object TriageInputSchema = new
    {
        type = "object",
        properties = new
        {
            tasks = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    properties = new
                    {
                        title = new { type = "string" },
                        description = new { type = "string" },
                        order = new { type = "integer" },
                    },
                    required = new[] { "title", "description", "order" },
                    additionalProperties = false,
                },
            },
        },
        required = new[] { "tasks" },
        additionalProperties = false,
    };

    // JSON Schema for kagura.submit_grill's input. Mirrors GrillSubmission — a single
    // markdown `body` string that replaces the WorkItem.Body field.
    private static readonly object GrillInputSchema = new
    {
        type = "object",
        properties = new
        {
            body = new { type = "string" },
        },
        required = new[] { "body" },
        additionalProperties = false,
    };

    // JSON Schema for kagura.submit_review's input. Mirrors ReviewSubmission — the same
    // shape as the existing ReviewVerdict so the typed-result contract seen by Ralph Loop
    // is unchanged across the legacy → PTY migration.
    private static readonly object ReviewInputSchema = new
    {
        type = "object",
        properties = new
        {
            autoMerge = new
            {
                type = "boolean",
                description = "True if the diff is safe to merge automatically; false if it needs a human reviewer to look at it before merging.",
            },
            reasoning = new
            {
                type = "string",
                description = "Short (1-3 sentences) explanation of the decision.",
            },
        },
        required = new[] { "autoMerge", "reasoning" },
        additionalProperties = false,
    };

    // JSON Schema for kagura.submit_merge_resolution's input. Mirrors MergeResolutionSubmission.
    private static readonly object MergeResolutionInputSchema = new
    {
        type = "object",
        properties = new
        {
            resolved = new
            {
                type = "boolean",
                description = "True if the conflicts were resolved AND the merge was finalized via git commit; false if the agent abandoned the merge and left the worktree mid-conflict for human attention.",
            },
            notes = new
            {
                type = "string",
                description = "Short (1-3 sentences) explanation of what was done, or why the agent gave up.",
            },
        },
        required = new[] { "resolved", "notes" },
        additionalProperties = false,
    };

    private static async Task<IResult> CallToolAsync(
        JsonElement? id, JsonElement message, Guid runId,
        IAgentSubmissionCoordinator submissions, ILogger log)
    {
        if (!message.TryGetProperty("params", out var paramsEl) || paramsEl.ValueKind != JsonValueKind.Object)
            return InvalidParams(id, "Missing or non-object 'params'.");

        var name = paramsEl.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
        if (string.IsNullOrEmpty(name)) return InvalidParams(id, "Missing tool name.");

        if (name != Role.Triage.McpSubmitToolName() &&
            name != Role.Grill.McpSubmitToolName() &&
            name != Role.AutoReview.McpSubmitToolName() &&
            name != Role.MergeResolver.McpSubmitToolName())
            return ToolError(id, $"Unknown tool '{name}'.");

        if (!paramsEl.TryGetProperty("arguments", out var args) || args.ValueKind != JsonValueKind.Object)
            return InvalidParams(id, "Missing 'arguments' object on tools/call.");

        if (!submissions.IsActive(runId))
        {
            log.LogWarning("MCP submission for stale runId {RunId} rejected", runId);
            return ToolError(id, $"No active Kagura agent run registered for {runId}; submission stale or unauthorised.");
        }

        if (!submissions.TrySubmit(runId, args))
        {
            // Race: the registration cleared between IsActive and TrySubmit.
            return ToolError(id, $"Submission for run {runId} was no longer pending.");
        }

        log.LogInformation("MCP run {RunId} submitted via tool {Tool}", runId, name);

        return Ok(id, new
        {
            // MCP tool results carry a content array; for submission tools a single text ack is fine.
            content = new[]
            {
                new { type = "text", text = "Submission accepted; the agent may now exit." },
            },
            isError = false,
        });
    }

    private static JsonElement? IdOf(JsonElement message) =>
        message.TryGetProperty("id", out var idEl) ? idEl : null;

    private static IResult Ok(JsonElement? id, object result) =>
        Results.Json(new
        {
            jsonrpc = "2.0",
            id = JsonRpcId(id),
            result,
        }, JsonOpts);

    private static IResult ParseError(JsonElement? id, string msg) => RpcError(id, -32700, "Parse error", msg);
    private static IResult InvalidRequest(JsonElement? id, string msg) => RpcError(id, -32600, "Invalid Request", msg);
    private static IResult MethodNotFound(JsonElement? id, string? method) => RpcError(id, -32601, "Method not found", method ?? "");
    private static IResult InvalidParams(JsonElement? id, string msg) => RpcError(id, -32602, "Invalid params", msg);
    private static IResult ToolError(JsonElement? id, string msg) => RpcError(id, -32000, "Tool error", msg);

    private static IResult RpcError(JsonElement? id, int code, string message, string data) =>
        Results.Json(new
        {
            jsonrpc = "2.0",
            id = JsonRpcId(id),
            error = new { code, message, data },
        }, JsonOpts);

    /// <summary>Pass through the request id without forcing a type — strings, numbers, and null are all valid per JSON-RPC.</summary>
    private static object? JsonRpcId(JsonElement? id)
    {
        if (id is null || id.Value.ValueKind == JsonValueKind.Null || id.Value.ValueKind == JsonValueKind.Undefined)
            return null;
        return id.Value.ValueKind switch
        {
            JsonValueKind.String => id.Value.GetString(),
            JsonValueKind.Number => id.Value.TryGetInt64(out var i) ? (object)i : id.Value.GetDouble(),
            _ => id.Value.GetRawText(),
        };
    }
}
