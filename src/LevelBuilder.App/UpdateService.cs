using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace LevelBuilder.App;
public sealed record ReleaseInfo(Version Version, string Tag, string AssetUrl, string AssetName);
public static class UpdateService
{
    private const string Api = "https://api.github.com/repos/isaiahpettingill/LevelBuilder/releases/latest";
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromMinutes(10) };
    static UpdateService()
    {
        Client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("LevelBuilder", "0.1"));
        Client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }
    public static async Task<ReleaseInfo?> CheckAsync()
    {
        using var response = await Client.GetAsync(Api);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
        var root = json.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v'), out var version) || version <= (typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 0))) return null;
        var rid = RuntimeInformation.RuntimeIdentifier;
        var assetName = $"LevelBuilder-{rid}.zip";
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
            if (asset.GetProperty("name").GetString() == assetName)
                return new ReleaseInfo(version, tag, asset.GetProperty("browser_download_url").GetString()!, assetName);
        throw new InvalidDataException($"Release {tag} has no {assetName} asset");
    }
    public static async Task DownloadAndRestartAsync(ReleaseInfo release, IProgress<double>? progress = null)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Executable path unavailable");
        var installDirectory = Path.GetDirectoryName(executable)!;
        var stage = Path.Combine(Path.GetTempPath(), "LevelBuilder-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            var archive = Path.Combine(stage, "release.zip");
            using (var response = await Client.GetAsync(release.AssetUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                await using var source = await response.Content.ReadAsStreamAsync();
                await using var target = File.Create(archive);
                var buffer = new byte[128 * 1024]; long written = 0;
                int count;
                while ((count = await source.ReadAsync(buffer)) != 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, count));
                    written += count;
                    if (response.Content.Headers.ContentLength is long length && length > 0) progress?.Report((double)written / length);
                }
            }
            var manifest = await Client.GetStringAsync(release.AssetUrl + ".sha256");
            var expected = manifest.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (expected is null || expected.Length != 64 || !expected.All(Uri.IsHexDigit)) throw new InvalidDataException("Invalid release checksum");
            await using (var file = File.OpenRead(archive))
            {
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(file));
                if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Release checksum does not match");
            }
            var payload = Path.Combine(stage, "payload");
            ZipFile.ExtractToDirectory(archive, payload);
            if (!File.Exists(Path.Combine(payload, Path.GetFileName(executable)))) throw new InvalidDataException("Downloaded archive lacks the application executable");
            var helper = Path.Combine(stage, OperatingSystem.IsWindows() ? "apply.cmd" : "apply.sh");
            if (OperatingSystem.IsWindows())
            {
                File.WriteAllText(helper, "@echo off\r\nsetlocal\r\n:wait\r\ntasklist /fi \"PID eq %~1\" | find \"%~1\" >nul && (timeout /t 1 /nobreak >nul & goto wait)\r\nrobocopy \"%~2\" \"%~3\" /E /R:5 /W:1 >nul\r\nstart \"\" \"%~4\"\r\n");
                Process.Start(new ProcessStartInfo("cmd.exe") { UseShellExecute = false, ArgumentList = { "/c", helper, Environment.ProcessId.ToString(), payload, installDirectory, executable }, CreateNoWindow = true });
            }
            else
            {
                File.WriteAllText(helper, "#!/bin/sh\nwhile kill -0 \"$1\" 2>/dev/null; do sleep 1; done\ncp -R \"$2/.\" \"$3/\"\nchmod +x \"$4\"\n\"$4\" >/dev/null 2>&1 &\n");
                Process.Start(new ProcessStartInfo("/bin/sh") { UseShellExecute = false, ArgumentList = { helper, Environment.ProcessId.ToString(), payload, installDirectory, executable }, CreateNoWindow = true });
            }
        }
        catch { try { Directory.Delete(stage, true); } catch { } throw; }
    }
}
