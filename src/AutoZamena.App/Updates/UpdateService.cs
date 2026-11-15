using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AutoZamena.App.Updates;

/// <summary>Checks GitHub's latest stable release and stages a verified update without touching user settings.</summary>
internal sealed class UpdateService : IDisposable
{
    private const string Repository = "https://github.com/ToshakGulina/windows-autozamena";
    private const string LatestApi = "https://api.github.com/repos/ToshakGulina/windows-autozamena/releases/latest";
    private const string InstallerName = "AutoZamena-Setup-win-x64.exe";
    private const string PortableName = "AutoZamena-Standalone-win-x64.exe";
    private const long MaxDownloadBytes = 150L * 1024 * 1024;
    private readonly HttpClient _http;

    internal static readonly Version CurrentVersion = typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 0);
    internal static readonly bool IsPortable = File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.flag"));

    public UpdateService()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("AutoZamena-UpdateChecker/1.0");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    internal static bool TryParseNewVersion(string? tag, Version current, out Version version)
    {
        version = new Version(0, 0);
        if (tag is null || tag.Length < 2 || tag[0] != 'v' ||
            !Version.TryParse(tag[1..], out Version? parsed) || parsed is null ||
            parsed.Major < 1 || parsed.Build < 0 || parsed.Revision >= 0)
            return false;
        version = parsed;
        return version > current;
    }

    public async Task<UpdateInfo?> CheckAsync(CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _http.GetAsync(LatestApi, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using JsonDocument document = await JsonDocument.ParseAsync(stream,
            new JsonDocumentOptions { MaxDepth = 16 }, cancellationToken).ConfigureAwait(false);
        JsonElement release = document.RootElement;
        if (!release.TryGetProperty("tag_name", out JsonElement tagElement) ||
            !TryParseNewVersion(tagElement.GetString(), CurrentVersion, out Version version))
            return null;
        if (release.TryGetProperty("draft", out JsonElement draft) && draft.GetBoolean() ||
            release.TryGetProperty("prerelease", out JsonElement prerelease) && prerelease.GetBoolean())
            return null;

        string tag = "v" + version;
        string filename = IsPortable ? PortableName : InstallerName;
        JsonElement assets = release.GetProperty("assets");
        bool foundBinary = false, foundSums = false;
        foreach (JsonElement asset in assets.EnumerateArray())
        {
            string? name = asset.GetProperty("name").GetString();
            if (name == filename) foundBinary = true;
            if (name == "SHA256SUMS.txt") foundSums = true;
        }
        if (!foundBinary || !foundSums) return null;
        // Construct URLs ourselves rather than executing a URL supplied by the API response.
        return new UpdateInfo(version, tag, filename,
            $"{Repository}/releases/download/{tag}/{filename}",
            $"{Repository}/releases/download/{tag}/SHA256SUMS.txt");
    }

    internal static string? FindExpectedHash(string manifest, string fileName)
    {
        foreach (string line in manifest.Split('\n'))
        {
            string trimmed = line.TrimEnd('\r');
            if (trimmed.Length == 64 + 2 + fileName.Length &&
                trimmed[64] == ' ' && trimmed[65] == ' ' &&
                trimmed.EndsWith(fileName, StringComparison.Ordinal) &&
                IsHex(trimmed.AsSpan(0, 64)))
                return trimmed[..64].ToLowerInvariant();
        }
        return null;
    }

    private static bool IsHex(ReadOnlySpan<char> text)
    {
        foreach (char c in text)
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                return false;
        return true;
    }

    /// <summary>Caller owns returned directory and must delete it on failure or after installation.</summary>
    public async Task<(string Directory, string File)> DownloadAsync(UpdateInfo info, CancellationToken cancellationToken)
    {
        using HttpResponseMessage sumsResponse = await _http.GetAsync(info.ChecksumsUrl, cancellationToken).ConfigureAwait(false);
        sumsResponse.EnsureSuccessStatusCode();
        if (sumsResponse.Content.Headers.ContentLength is > 16384)
            throw new InvalidDataException("Слишком большой файл контрольных сумм.");
        string manifest = await sumsResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (manifest.Length > 16384) throw new InvalidDataException("Слишком большой файл контрольных сумм.");
        string expected = FindExpectedHash(manifest, info.FileName)
            ?? throw new InvalidDataException("В релизе нет контрольной суммы для файла обновления.");

        string directory = Path.Combine(Path.GetTempPath(), "AutoZamena-Update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, info.FileName);
        try
        {
            using HttpResponseMessage response = await _http.GetAsync(info.DownloadUrl,
                HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > MaxDownloadBytes)
                throw new InvalidDataException("Размер обновления превышает допустимый предел.");
            await using (Stream destination = File.Create(file))
            await using (Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            {
                byte[] buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > MaxDownloadBytes) throw new InvalidDataException("Размер обновления превышает допустимый предел.");
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
            }
            await using FileStream verified = File.OpenRead(file);
            byte[] hash = await SHA256.HashDataAsync(verified, cancellationToken).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(expected)))
                throw new InvalidDataException("Контрольная сумма обновления не совпадает. Установка отменена.");
            return (directory, file);
        }
        catch
        {
            Directory.Delete(directory, recursive: true);
            throw;
        }
    }

    public static void Launch(string stagedFile, string directory)
    {
        if (!IsPortable)
        {
            // Inno Setup handles stopping the running app and preserving its config in %APPDATA%.
            Process.Start(new ProcessStartInfo(stagedFile) { UseShellExecute = true });
            return;
        }

        // The portable executable cannot replace itself until its process has exited. A detached
        // helper copies the verified binary into the existing directory and restarts from there.
        string target = Environment.ProcessPath ?? throw new InvalidOperationException("Не найден путь к приложению.");
        string script = Path.Combine(directory, "apply-update.ps1");
        File.WriteAllText(script, """
            param([int]$OldPid, [string]$Source, [string]$Target)
            try { Wait-Process -Id $OldPid -Timeout 120 -ErrorAction SilentlyContinue } catch {}
            for ($i = 0; $i -lt 60; $i++) {
                try {
                    Copy-Item -LiteralPath $Source -Destination $Target -Force -ErrorAction Stop
                    Start-Process -FilePath $Target -ArgumentList '--minimized'
                    break
                } catch { Start-Sleep -Seconds 2 }
            }
            Remove-Item -LiteralPath $Source -Force -ErrorAction SilentlyContinue
            """);
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-ExecutionPolicy");
        start.ArgumentList.Add("Bypass");
        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(script);
        start.ArgumentList.Add("-OldPid");
        start.ArgumentList.Add(Environment.ProcessId.ToString());
        start.ArgumentList.Add("-Source");
        start.ArgumentList.Add(stagedFile);
        start.ArgumentList.Add("-Target");
        start.ArgumentList.Add(target);
        Process.Start(start)?.Dispose();
    }

    public void Dispose() => _http.Dispose();
}

internal sealed record UpdateInfo(Version Version, string Tag, string FileName, string DownloadUrl, string ChecksumsUrl);
