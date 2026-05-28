using System.CommandLine;
using System.Reflection;
using Kagura.Api;
using Microsoft.Extensions.FileProviders;

var root = new RootCommand("Kagura — local dev-flow assistant. Run `kagura run` to start the server.");

var runCommand = new Command("run", "Start the Kagura server (Kestrel on :5253).");
runCommand.SetHandler(async () =>
{
    var spa = TryCreateEmbeddedSpaProvider();
    await KaguraApiHost.RunAsync(args, spa);
});
root.AddCommand(runCommand);

var versionCommand = new Command("version", "Print the installed Kagura version.");
versionCommand.SetHandler(() =>
{
    Console.WriteLine(GetInformationalVersion());
});
root.AddCommand(versionCommand);

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

static string GetInformationalVersion()
{
    var attr = Assembly.GetExecutingAssembly()
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>();

    var raw = attr?.InformationalVersion ?? "0.0.0";

    // MinVer's AssemblyInformationalVersion can include a `+<sha>` build metadata suffix —
    // strip it so `kagura version` prints a clean semver.
    var plus = raw.IndexOf('+');
    return plus >= 0 ? raw[..plus] : raw;
}
