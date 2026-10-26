using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace SpeedLimit;

/// <summary>Neueres Release auf GitHub: Version, Release-Notizen, Installer-Download und (falls GitHub sie liefert) SHA-256.</summary>
public sealed record UpdateInfo(Version Version, string Notes, Uri Installer, string? Sha256);

/// <summary>Sucht neue Versionen über die GitHub-Releases und installiert sie mit dem Inno-Setup-Installer.</summary>
public static class Updater
{
    public const string Repo = "Xieonie/FlowCap";
    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("FlowCap"); // GitHub-API verlangt einen User-Agent
        return http;
    }

    public static Version Current => Normalize(Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0));

    static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    /// <summary>null = kein neueres Release (oder Repo privat / noch keine Releases).</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        using var response = await Http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest", ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null; // private Repos antworten ohne Anmeldung mit 404
        response.EnsureSuccessStatusCode();
        return Parse(await response.Content.ReadAsStringAsync(ct), Current);
    }

    public static UpdateInfo? Parse(string json, Version current)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean()) return null;
        if (root.TryGetProperty("prerelease", out var pre) && pre.GetBoolean()) return null;
        if (!Version.TryParse(root.GetProperty("tag_name").GetString()?.TrimStart('v', 'V'), out var version)) return null;
        version = Normalize(version);
        if (version <= Normalize(current)) return null;

        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            if (!name.StartsWith("FlowCap-Setup", StringComparison.OrdinalIgnoreCase) || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
            if (!Uri.TryCreate(asset.GetProperty("browser_download_url").GetString(), UriKind.Absolute, out var url) || !IsTrusted(url)) continue;
            var digest = asset.TryGetProperty("digest", out var d) ? d.GetString() : null;
            var sha = digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..] : null;
            var notes = root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
            return new UpdateInfo(version, notes, url, sha);
        }
        return null;
    }

    /// <summary>Nur Downloads aus den Releases genau dieses Repos per HTTPS.</summary>
    public static bool IsTrusted(Uri url) =>
        url.Scheme == Uri.UriSchemeHttps && url.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
        && url.AbsolutePath.StartsWith($"/{Repo}/releases/download/", StringComparison.OrdinalIgnoreCase);

    /// <summary>Installer in einen eigenen Temp-Ordner laden; Fortschritt 0..1. Prüft die SHA-256, wenn GitHub eine liefert.</summary>
    public static async Task<string> DownloadAsync(UpdateInfo update, IProgress<double> progress, CancellationToken ct = default)
    {
        var dir = Path.Combine(Path.GetTempPath(), "FlowCap-Update");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"FlowCap-Setup-{update.Version}.exe");

        using (var response = await Http.GetAsync(update.Installer, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? 0;
            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var file = File.Create(path);
            var buf = new byte[81920];
            long done = 0;
            int n;
            while ((n = await source.ReadAsync(buf, ct)) > 0)
            {
                await file.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                if (total > 0) progress.Report((double)done / total);
            }
        }

        if (update.Sha256 is { } expected)
        {
            await using var file = File.OpenRead(path);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(file, ct));
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(path);
                throw new IOException("Der heruntergeladene Installer ist beschädigt (Prüfsumme stimmt nicht).");
            }
        }
        return path;
    }

    /// <summary>Installer ohne Rückfragen starten; er beendet FlowCap und startet es danach neu (/RESTARTAPP, siehe FlowCap.iss).</summary>
    public static void RunInstaller(string path) =>
        Process.Start(new ProcessStartInfo(path, "/SILENT /SUPPRESSMSGBOXES /NORESTART /RESTARTAPP=1") { UseShellExecute = true });
}
