using System.CommandLine;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Kagura.Api;
using Kagura.Cli.Commands;
using Kagura.Cli.Updates;
using Microsoft.Extensions.FileProviders;

var root = new RootCommand("Kagura — local dev-flow assistant. Run `kagura run` to start the server.");

var portOption = new Option<int>(
    aliases: new[] { "--port", "-p" },
    getDefaultValue: () => KaguraApiHost.DefaultPort,
    description: "TCP port to listen on (default: 5253).");

var verboseOption = new Option<bool>(
    aliases: new[] { "--verbose", "-v" },
    description: "Show full ASP.NET host logs (otherwise warnings and errors only).");

var noUpdateCheckOption = new Option<bool>(
    "--no-update-check",
    description: "Skip the NuGet update check on startup.");

var runCommand = new Command("run", "Start the Kagura server (Kestrel on :5253).");
runCommand.AddOption(portOption);
runCommand.AddOption(verboseOption);
runCommand.AddOption(noUpdateCheckOption);
runCommand.SetHandler(async (int port, bool verbose, bool noUpdateCheck) =>
{
    if (!TryReservePort(port))
    {
        Console.Error.WriteLine($"port {port} in use — pass --port <n> to override");
        Environment.Exit(1);
    }

    var spa = TryCreateEmbeddedSpaProvider();
    var quiet = !ShouldBeVerbose(verbose);
    var hostTask = KaguraApiHost.RunAsync(args, spa, port, quiet);

    if (!UpdateChecker.IsSuppressed(noUpdateCheck))
    {
        // Fire-and-forget. Delay just enough for Kestrel's "running at" line to print first,
        // so the upgrade banner appears in the right order. Network failure is silent.
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            await UpdateChecker.PrintBannerIfNewerAsync();
        });
    }

    await hostTask;
}, portOption, verboseOption, noUpdateCheckOption);
root.AddCommand(runCommand);
root.AddCommand(OpenCommand.Build());

var versionCommand = new Command("version", "Print the installed Kagura version.");
versionCommand.SetHandler(() =>
{
    Console.WriteLine(GetInformationalVersion());
});
root.AddCommand(versionCommand);

root.AddCommand(DoctorCommand.Build());

return await root.InvokeAsync(args);

static bool ShouldBeVerbose(bool flag)
{
    if (flag) return true;
    var env = Environment.GetEnvironmentVariable("KAGURA_LOG_LEVEL");
    if (string.IsNullOrEmpty(env)) return false;
    // Anything debug/trace/information turns the floodgates on.
    return env.Equals("Debug", StringComparison.OrdinalIgnoreCase)
        || env.Equals("Trace", StringComparison.OrdinalIgnoreCase)
        || env.Equals("Information", StringComparison.OrdinalIgnoreCase);
}

static string GetInformationalVersion()
{
    var attr = Assembly.GetExecutingAssembly()
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>();

    var raw = attr?.InformationalVersion ?? "0.0.0";

    // MinVer's AssemblyInformationalVersion can include a `+<sha>` build-metadata suffix —
    // strip it so `kagura version` prints a clean semver.
    var plus = raw.IndexOf('+');
    return plus >= 0 ? raw[..plus] : raw;
}

static IFileProvider? TryCreateEmbeddedSpaProvider()
{
    var assembly = Assembly.GetExecutingAssembly();
    var hasEmbeddedSpa = assembly
        .GetManifestResourceNames()
        .Any(n => n.EndsWith(".index.html", StringComparison.OrdinalIgnoreCase));

    if (!hasEmbeddedSpa)
    {
        return null;
    }

    return new ManifestEmbeddedFileProvider(assembly, "wwwroot");
}

// Best-effort probe: open a listener on the requested port, then immediately close it.
// If another process holds the port, the bind throws SocketException(AddressAlreadyInUse)
// and we surface the friendly message before Kestrel ever logs its own stack trace.
// There is a tiny TOCTOU window between probe-close and Kestrel-open; if a race loses,
// Kestrel will throw — but in practice the message is much friendlier than the bare trace.
static bool TryReservePort(int port)
{
    try
    {
        using var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        listener.Stop();
        return true;
    }
    catch (SocketException s) when (s.SocketErrorCode == SocketError.AddressAlreadyInUse)
    {
        return false;
    }
}
