using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kagura.Core;

namespace Kagura.Cli.Updates;

/// <summary>
/// Asynchronous, fail-silent check against api.nuget.org for newer Kagura.Cli versions.
/// Cached for 24h in ~/.kagura/update-check.json (or ~/.devflow/ if that's all that exists).
/// Skipped when CI=true, when --no-update-check is passed, or KAGURA_NO_UPDATE_CHECK=1.
/// </summary>
internal static class UpdateChecker
{
    private const string IndexUrl = "https://api.nuget.org/v3-flatcontainer/kagura.cli/index.json";
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);

    public static bool IsSuppressed(bool noUpdateCheckFlag)
    {
        if (noUpdateCheckFlag) return true;
        if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase)) return true;
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("KAGURA_NO_UPDATE_CHECK"))) return true;
        return false;
    }

    public static async Task PrintBannerIfNewerAsync()
    {
        try
        {
            var current = GetCurrentVersion();
            if (current is null) return;

            var latest = await ResolveLatestStableAsync();
            if (latest is null) return;

            if (IsStrictlyGreater(latest, current))
            {
                Console.WriteLine($"Update available: {latest} (you have {current}). Run: dotnet tool update -g Kagura.Cli");
            }
        }
        catch
        {
            // Fully silent on any failure — version check must never crash startup.
        }
    }

    private static async Task<string?> ResolveLatestStableAsync()
    {
        var cachePath = GetCachePath();
        var cached = ReadCache(cachePath);
        if (cached is not null && DateTime.UtcNow - cached.CheckedAtUtc < CacheTtl)
        {
            return cached.LatestVersion;
        }

        var fresh = await FetchLatestAsync();
        if (fresh is not null)
        {
            WriteCache(cachePath, new CacheEntry(fresh, DateTime.UtcNow));
        }
        return fresh;
    }

    private static async Task<string?> FetchLatestAsync()
    {
        using var http = new HttpClient { Timeout = HttpTimeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Kagura.Cli/update-check");
        var doc = await http.GetFromJsonAsync<FlatContainerIndex>(IndexUrl);
        if (doc?.Versions is null) return null;
        // Filter to stable (no '-') versions and pick the lexicographically largest sane one.
        string? best = null;
        foreach (var v in doc.Versions)
        {
            if (string.IsNullOrWhiteSpace(v) || v.Contains('-')) continue;
            if (best is null || IsStrictlyGreater(v, best)) best = v;
        }
        return best;
    }

    private static string? GetCurrentVersion()
    {
        var raw = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(raw)) return null;
        var plus = raw.IndexOf('+');
        var v = plus >= 0 ? raw[..plus] : raw;
        // Drop prerelease tag for "is there a newer stable" comparisons — alpha builds always
        // think they're old, which is the correct hint to upgrade.
        var dash = v.IndexOf('-');
        return dash >= 0 ? v[..dash] : v;
    }

    private static bool IsStrictlyGreater(string a, string b)
    {
        if (Version.TryParse(NormalizeForSystemVersion(a), out var va) &&
            Version.TryParse(NormalizeForSystemVersion(b), out var vb))
        {
            return va > vb;
        }
        return string.CompareOrdinal(a, b) > 0;
    }

    private static string NormalizeForSystemVersion(string s)
    {
        // System.Version requires 2-4 components. "1.2" → "1.2.0", "1.2.3.4" stays.
        var parts = s.Split('.');
        if (parts.Length == 1) return $"{parts[0]}.0.0";
        if (parts.Length == 2) return $"{parts[0]}.{parts[1]}.0";
        if (parts.Length >= 4) return string.Join('.', parts.Take(4));
        return s;
    }

    private static string GetCachePath()
    {
        var dir = Directory.Exists(KaguraPaths.LegacyRoot) && !Directory.Exists(KaguraPaths.Root)
            ? KaguraPaths.LegacyRoot
            : KaguraPaths.Root;
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "update-check.json");
    }

    private static CacheEntry? ReadCache(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<CacheEntry>(File.ReadAllText(path));
        }
        catch
        {
            return null;
        }
    }

    private static void WriteCache(string path, CacheEntry entry)
    {
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(entry));
        }
        catch
        {
            // best effort
        }
    }

    private sealed record CacheEntry(
        [property: JsonPropertyName("latestVersion")] string LatestVersion,
        [property: JsonPropertyName("checkedAtUtc")] DateTime CheckedAtUtc);

    private sealed record FlatContainerIndex(
        [property: JsonPropertyName("versions")] List<string>? Versions);
}
