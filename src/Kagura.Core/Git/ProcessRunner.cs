using System.Diagnostics;
using System.Text;

namespace Kagura.Core.Git;

public record ProcessResult(int ExitCode, string Stdout, string Stderr)
{
    public bool Success => ExitCode == 0;
}

public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> args,
        string? workingDirectory = null,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {fileName}");
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var stdoutTask = ReadAll(proc.StandardOutput, stdout, ct);
        var stderrTask = ReadAll(proc.StandardError, stderr, ct);

        await proc.WaitForExitAsync(ct);
        await Task.WhenAll(stdoutTask, stderrTask);

        return new ProcessResult(proc.ExitCode, stdout.ToString(), stderr.ToString());
    }

    public static async Task<ProcessResult> RunRequiredAsync(
        string fileName, IEnumerable<string> args, string? workingDirectory = null, CancellationToken ct = default)
    {
        var r = await RunAsync(fileName, args, workingDirectory, ct);
        if (!r.Success)
            throw new InvalidOperationException(
                $"Process `{fileName} {string.Join(' ', args)}` exited {r.ExitCode}.\nstdout: {r.Stdout}\nstderr: {r.Stderr}");
        return r;
    }

    private static async Task ReadAll(StreamReader reader, StringBuilder sink, CancellationToken ct)
    {
        var buf = new char[4096];
        while (!ct.IsCancellationRequested)
        {
            var n = await reader.ReadAsync(buf, ct);
            if (n == 0) break;
            sink.Append(buf, 0, n);
        }
    }
}
