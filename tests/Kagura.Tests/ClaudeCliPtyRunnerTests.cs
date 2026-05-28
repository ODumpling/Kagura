using Kagura.Core.ClaudeCli;

namespace Kagura.Tests;

public class ClaudeCliPtyRunnerTests
{
    [Fact]
    public void ExtractResultEnvelope_finds_result_line_among_stream()
    {
        var streamJson =
            "{\"type\":\"system\",\"subtype\":\"init\",\"cwd\":\"/foo\"}\n" +
            "{\"type\":\"assistant\",\"message\":{\"content\":\"working\"}}\n" +
            "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":\"[]\"}\n";

        var envelope = ClaudeCliPtyRunner.ExtractResultEnvelope(streamJson);

        Assert.Contains("\"type\":\"result\"", envelope);
        Assert.Contains("\"is_error\":false", envelope);
        Assert.Contains("\"result\":\"[]\"", envelope);
    }

    [Fact]
    public void ExtractResultEnvelope_handles_crlf_line_endings_from_pty()
    {
        var streamJson =
            "{\"type\":\"system\"}\r\n" +
            "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":\"ok\"}\r\n";

        var envelope = ClaudeCliPtyRunner.ExtractResultEnvelope(streamJson);

        Assert.Contains("\"type\":\"result\"", envelope);
        Assert.Contains("\"result\":\"ok\"", envelope);
    }

    [Fact]
    public void ExtractResultEnvelope_strips_ansi_escape_sequences()
    {
        var streamJson =
            "\x1B[2J\x1B[H{\"type\":\"system\"}\r\n" +
            "\x1B[31m{\"type\":\"result\",\"is_error\":false,\"result\":\"ok\"}\x1B[0m\r\n";

        var envelope = ClaudeCliPtyRunner.ExtractResultEnvelope(streamJson);

        Assert.Contains("\"type\":\"result\"", envelope);
        Assert.False(envelope.Contains('\x1B'), "envelope must not contain ESC chars");
    }

    [Fact]
    public void ExtractResultEnvelope_picks_last_result_line_when_multiple()
    {
        var streamJson =
            "{\"type\":\"result\",\"is_error\":false,\"result\":\"first\"}\n" +
            "{\"type\":\"result\",\"is_error\":false,\"result\":\"second\"}\n";

        var envelope = ClaudeCliPtyRunner.ExtractResultEnvelope(streamJson);

        Assert.Contains("\"result\":\"second\"", envelope);
    }

    [Fact]
    public void ExtractResultEnvelope_throws_when_no_result_line()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ClaudeCliPtyRunner.ExtractResultEnvelope("{\"type\":\"system\"}\n"));
    }
}
