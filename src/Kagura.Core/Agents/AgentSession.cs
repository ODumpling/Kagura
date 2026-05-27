using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Porta.Pty;

namespace Kagura.Core.Agents;

public sealed class AgentSession : IAsyncDisposable
{
    public Guid RunId { get; }
    public Guid TaskId { get; }
    public Guid WorkItemId { get; }
    public string WorktreePath { get; }
    public string TranscriptLogPath { get; }
    public int ProcessId { get; }
    public DateTime StartedAt { get; } = DateTime.UtcNow;
    public DateTime? EndedAt { get; private set; }
    public int? ExitCode { get; private set; }
    public bool Alive => !EndedAt.HasValue;

    public event Action<byte[]>? OnData;
    public event Action<int?>? OnExit;

    private readonly IPtyConnection _pty;
    private readonly FileStream _transcript;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _readLoop;
    private readonly ILogger _log;

    public AgentSession(
        Guid runId,
        Guid taskId,
        Guid workItemId,
        string worktreePath,
        string transcriptLogPath,
        IPtyConnection pty,
        ILogger log)
    {
        RunId = runId;
        TaskId = taskId;
        WorkItemId = workItemId;
        WorktreePath = worktreePath;
        TranscriptLogPath = transcriptLogPath;
        ProcessId = pty.Pid;
        _pty = pty;
        _log = log;

        Directory.CreateDirectory(Path.GetDirectoryName(transcriptLogPath)!);
        _transcript = new FileStream(transcriptLogPath, FileMode.Create, FileAccess.Write, FileShare.Read);

        _pty.ProcessExited += (_, e) =>
        {
            ExitCode = e.ExitCode;
            EndedAt = DateTime.UtcNow;
            OnExit?.Invoke(ExitCode);
            _cts.Cancel();
        };

        _readLoop = Task.Run(ReadLoop);
    }

    private async Task ReadLoop()
    {
        var buf = new byte[4096];
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var n = await _pty.ReaderStream.ReadAsync(buf.AsMemory(), _cts.Token);
                if (n == 0) break;
                var chunk = new byte[n];
                Array.Copy(buf, chunk, n);
                await _transcript.WriteAsync(chunk.AsMemory(), _cts.Token);
                await _transcript.FlushAsync(_cts.Token);
                OnData?.Invoke(chunk);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.LogError(ex, "PTY read loop crashed for run {RunId}", RunId);
        }
        finally
        {
            if (!EndedAt.HasValue) EndedAt = DateTime.UtcNow;
        }
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        if (!Alive) return;
        await _pty.WriterStream.WriteAsync(data, ct);
        await _pty.WriterStream.FlushAsync(ct);
    }

    public void Resize(int cols, int rows)
    {
        if (Alive) _pty.Resize(cols, rows);
    }

    public byte[] ReadTranscript()
    {
        try { return File.ReadAllBytes(TranscriptLogPath); }
        catch { return Array.Empty<byte>(); }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { _pty.Dispose(); } catch { }
        try { await _readLoop; } catch { }
        await _transcript.DisposeAsync();
        _cts.Dispose();
    }
}
