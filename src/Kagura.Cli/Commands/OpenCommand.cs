using System.CommandLine;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Kagura.Api;

namespace Kagura.Cli.Commands;

internal static class OpenCommand
{
    public static Command Build()
    {
        var portOption = new Option<int>(
            aliases: new[] { "--port", "-p" },
            getDefaultValue: () => KaguraApiHost.DefaultPort,
            description: "TCP port the Kagura server is (or will be) on.");

        var cmd = new Command("open", "Open the Kagura UI in the default browser, spawning the server if needed.");
        cmd.AddOption(portOption);
        cmd.SetHandler(async (int port) => Environment.ExitCode = await RunAsync(port), portOption);
        return cmd;
    }

    private static async Task<int> RunAsync(int port)
    {
        var url = $"http://localhost:{port}/";

        if (IsListening(port))
        {
            LaunchBrowser(url);
            return 0;
        }

        // Nothing listening: spawn a detached `kagura run --port <port>` and wait for readiness.
        var process = SpawnDetachedRun(port);
        if (process is null)
        {
            Console.Error.WriteLine("could not spawn `kagura run` — is the `kagura` binary on PATH?");
            return 1;
        }

        if (!await WaitForListenerAsync(port, TimeSpan.FromSeconds(10)))
        {
            Console.Error.WriteLine($"server did not become ready on :{port} within 10s");
            return 1;
        }

        LaunchBrowser(url);
        return 0;
    }

    private static bool IsListening(int port)
    {
        try
        {
            using var client = new TcpClient();
            var connect = client.BeginConnect("127.0.0.1", port, null, null);
            var ready = connect.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(250));
            if (!ready) return false;
            try { client.EndConnect(connect); } catch { return false; }
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }

    private static Process? SpawnDetachedRun(int port)
    {
        // Re-invoke the currently-running tool by its command name (`kagura`).
        // It's on PATH because the tool is installed globally.
        try
        {
            var psi = new ProcessStartInfo("kagura")
            {
                ArgumentList = { "run", "--port", port.ToString() },
                UseShellExecute = false,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                CreateNoWindow = true,
            };
            var proc = Process.Start(psi);
            return proc;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<bool> WaitForListenerAsync(int port, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (IsListening(port)) return true;
            await Task.Delay(200);
        }
        return false;
    }

    private static void LaunchBrowser(string url)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Process.Start(new ProcessStartInfo("cmd", $"/c start \"\" \"{url}\"") { CreateNoWindow = true });
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Process.Start("open", url);
            }
            else
            {
                Process.Start("xdg-open", url);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"could not launch browser: {ex.Message}");
            Console.WriteLine(url);
        }
    }
}
