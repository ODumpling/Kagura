using System.CommandLine;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using Kagura.Core;
using Kagura.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Kagura.Cli.Commands;

internal static class DoctorCommand
{
    public static Command Build()
    {
        var cmd = new Command("doctor", "Diagnose whether the current machine is set up to run Kagura.");
        cmd.SetHandler(async () => Environment.ExitCode = await RunAsync());
        return cmd;
    }

    public static async Task<int> RunAsync()
    {
        var report = new Report();
        await CheckClaudeAsync(report);
        CheckGit(report);
        CheckGh(report);
        CheckStateDirectoryWritable(report);
        CheckPortFree(report);
        await CheckDatabaseAsync(report);
        return report.AllOk ? 0 : 1;
    }

    private static async Task CheckClaudeAsync(Report report)
    {
        if (!OnPath("claude"))
        {
            report.Fail("claude", "`claude` CLI not on PATH — install from https://claude.com/code");
            return;
        }

        var ver = await RunProcessAsync("claude", new[] { "--version" }, TimeSpan.FromSeconds(5));
        if (ver.ExitCode != 0)
        {
            report.Fail("claude", "`claude --version` failed — install or upgrade the claude CLI");
            return;
        }

        // Real authenticated probe — accept some token cost here, per the spec ("This is the one
        // place `doctor` is allowed to spend a token.").
        var probe = await RunProcessAsync(
            "claude",
            new[] { "-p", "hi", "--max-turns", "1" },
            TimeSpan.FromSeconds(30));

        if (probe.ExitCode != 0)
        {
            var hint = string.IsNullOrWhiteSpace(probe.Stderr)
                ? "claude probe failed — run `claude login` and retry"
                : $"claude probe failed: {Truncate(probe.Stderr, 120)}";
            report.Fail("claude", hint);
            return;
        }

        report.Ok("claude", "on PATH and authenticated");
    }

    private static void CheckGit(Report report)
    {
        if (!OnPath("git"))
        {
            report.Fail("git", "`git` not on PATH — install git");
            return;
        }

        var email = RunProcessAsync("git", new[] { "config", "--global", "user.email" }, TimeSpan.FromSeconds(5))
            .GetAwaiter().GetResult();

        if (email.ExitCode != 0 || string.IsNullOrWhiteSpace(email.Stdout))
        {
            report.Fail("git", "git is installed but `user.email` is not set — `git config --global user.email \"you@example.com\"`");
            return;
        }

        report.Ok("git", $"on PATH (user.email={email.Stdout.Trim()})");
    }

    private static void CheckGh(Report report)
    {
        if (OnPath("gh"))
        {
            report.Ok("gh", "on PATH");
        }
        else
        {
            // gh is optional — report OK with an "(disabled)" note rather than failing.
            report.Ok("gh", "not installed (optional — PR features disabled)");
        }
    }

    private static void CheckStateDirectoryWritable(Report report)
    {
        // Prefer ~/.kagura/; fall back to ~/.devflow/ on boxes whose server hasn't yet
        // run the one-shot migration so `doctor` stays useful pre-first-run.
        var dir = Directory.Exists(KaguraPaths.LegacyRoot) && !Directory.Exists(KaguraPaths.Root)
            ? KaguraPaths.LegacyRoot
            : KaguraPaths.Root;
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, $".doctor-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            report.Ok("state-dir", $"{dir} writable");
        }
        catch (Exception ex)
        {
            report.Fail("state-dir", $"{dir} not writable: {ex.Message}");
        }
    }

    private static void CheckPortFree(Report report)
    {
        const int port = 5253;
        try
        {
            var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
            if (listeners.Any(l => l.Port == port))
            {
                report.Fail("port", $"localhost:{port} is in use — stop the process or pass `--port <n>` to `kagura run`");
                return;
            }
            report.Ok("port", $"localhost:{port} free");
        }
        catch (Exception ex)
        {
            report.Fail("port", $"could not probe localhost:{port}: {ex.Message}");
        }
    }

    private static async Task CheckDatabaseAsync(Report report)
    {
        string? dbPath = File.Exists(KaguraPaths.DbPath) ? KaguraPaths.DbPath
                       : File.Exists(KaguraPaths.LegacyDbPath) ? KaguraPaths.LegacyDbPath
                       : null;
        if (dbPath is null)
        {
            report.Fail("database", $"no kagura.db at {KaguraPaths.DbPath} (or legacy {KaguraPaths.LegacyDbPath}); run `kagura run` once to create it");
            return;
        }

        try
        {
            var options = new DbContextOptionsBuilder<KaguraDbContext>()
                .UseSqlite($"Data Source={dbPath}")
                .Options;
            // `doctor` doesn't read protected columns — ephemeral keys are enough to satisfy DI.
            await using var db = new KaguraDbContext(options, new EphemeralDataProtectionProvider());
            var pending = (await db.Database.GetPendingMigrationsAsync()).ToList();
            if (pending.Count > 0)
            {
                report.Fail("database", $"{pending.Count} pending migration(s) at {dbPath} — run `kagura run` to apply");
                return;
            }
            report.Ok("database", $"{dbPath} up to date");
        }
        catch (Exception ex)
        {
            report.Fail("database", $"could not inspect {dbPath}: {ex.Message}");
        }
    }

    private static bool OnPath(string command)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var separator = OperatingSystem.IsWindows() ? ';' : ':';
        var exe = OperatingSystem.IsWindows() ? command + ".exe" : command;
        foreach (var dir in path.Split(separator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (File.Exists(Path.Combine(dir, exe)))
            {
                return true;
            }
            if (!OperatingSystem.IsWindows() && File.Exists(Path.Combine(dir, command)))
            {
                return true;
            }
        }
        return false;
    }

    private record ProcessResult(int ExitCode, string Stdout, string Stderr);

    private static async Task<ProcessResult> RunProcessAsync(string file, IEnumerable<string> args, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        try
        {
            using var proc = Process.Start(psi);
            if (proc is null) return new ProcessResult(-1, string.Empty, "could not start process");

            using var cts = new CancellationTokenSource(timeout);
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            try
            {
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return new ProcessResult(-1, await stdoutTask, $"timed out after {timeout.TotalSeconds:F0}s");
            }
            return new ProcessResult(proc.ExitCode, await stdoutTask, await stderrTask);
        }
        catch (Exception ex)
        {
            return new ProcessResult(-1, string.Empty, ex.Message);
        }
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s.Trim() : s[..max].Trim() + "...";

    private sealed class Report
    {
        public bool AllOk { get; private set; } = true;

        public void Ok(string name, string detail)
        {
            Console.Out.WriteLine($"OK   {name}: {detail}");
        }

        public void Fail(string name, string detail)
        {
            AllOk = false;
            Console.Error.WriteLine($"FAIL {name}: {detail}");
        }
    }
}
