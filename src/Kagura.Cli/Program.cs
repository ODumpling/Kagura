using System.CommandLine;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Kagura.Api;
using Microsoft.Extensions.FileProviders;

var root = new RootCommand("Kagura — local dev-flow assistant. Run `kagura run` to start the server.");

var portOption = new Option<int>(
    aliases: new[] { "--port", "-p" },
    getDefaultValue: () => KaguraApiHost.DefaultPort,
    description: "TCP port to listen on (default: 5253).");

var runCommand = new Command("run", "Start the Kagura server (Kestrel on :5253).");
runCommand.AddOption(portOption);
runCommand.SetHandler(async (int port) =>
{
    if (!TryReservePort(port))
    {
        Console.Error.WriteLine($"port {port} in use — pass --port <n> to override");
        Environment.Exit(1);
    }

    var spa = TryCreateEmbeddedSpaProvider();
    await KaguraApiHost.RunAsync(args, spa, port);
}, portOption);
root.AddCommand(runCommand);

return await root.InvokeAsync(args);

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
