using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NightLightControl.Core;

public sealed record AppRelease(Version Version, string Tag, string PageUrl, string DownloadUrl, string? ChecksumUrl, string? Sha256, long Size);
public sealed record DownloadedUpdate(string FilePath, string Sha256);

public static class Updates
{
    public const string RepositoryUrl = "https://github.com/Esteban-3055/Night-Light-Control";
    private const string ApiUrl = "https://api.github.com/repos/Esteban-3055/Night-Light-Control/releases?per_page=30";
    private const long MaximumDownload = 350_000_000;
    public static Version CurrentVersion => typeof(Updates).Assembly.GetName().Version ?? new Version(0, 0, 0);
    public static string CurrentVersionText => CurrentVersion.ToString(3);
    public static string UpdateFolder => Path.Combine(Settings.Folder, "updates");
    private static readonly HttpClient Client = CreateClient();
    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(6) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("NightLightControl/" + CurrentVersionText);
        return client;
    }
    public static bool IsOfficialAsset(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) && uri.AbsolutePath.StartsWith("/Esteban-3055/Night-Light-Control/releases/download/", StringComparison.OrdinalIgnoreCase);
    public static string NormalizeHash(string value)
    {
        if (value.Length != 64 || !value.All(Uri.IsHexDigit)) throw new InvalidDataException("La suma de verificación de la actualización no es válida.");
        return value.ToLowerInvariant();
    }
    public static AppRelease? ParseReleases(string json)
    {
        using var document = JsonDocument.Parse(json); AppRelease? newest = null;
        foreach (var item in document.RootElement.EnumerateArray())
        {
            try
            {
            if (item.TryGetProperty("draft", out var draft) && draft.GetBoolean()) continue;
            string tag = item.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version)) continue;
            JsonElement? executable = null, checksum = null;
            foreach (var asset in item.GetProperty("assets").EnumerateArray())
            {
                if (asset.GetProperty("name").GetString() == "NightLightControl.exe") executable = asset;
                if (asset.GetProperty("name").GetString() == "SHA256SUMS.txt") checksum = asset;
            }
            if (executable == null) continue;
            string url = executable.Value.GetProperty("browser_download_url").GetString() ?? "";
            if (!IsOfficialAsset(url)) continue;
            string? checksumUrl = checksum?.GetProperty("browser_download_url").GetString();
            if (checksumUrl != null && !IsOfficialAsset(checksumUrl)) continue;
            string? hash = executable.Value.TryGetProperty("digest", out var digest) && digest.ValueKind == JsonValueKind.String ? digest.GetString() : null;
            hash = hash?.StartsWith("sha256:", StringComparison.Ordinal) == true ? NormalizeHash(hash[7..]) : null;
            if (hash == null && checksumUrl == null) continue;
            long size = executable.Value.GetProperty("size").GetInt64();
            if (size is <= 0 or > MaximumDownload) continue;
            string page = RepositoryUrl + "/releases/tag/" + Uri.EscapeDataString(tag);
            var release = new AppRelease(version, tag, page, url, checksumUrl, hash, size);
            if (newest == null || release.Version > newest.Version) newest = release;
            }
            catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or System.Collections.Generic.KeyNotFoundException or FormatException or OverflowException)
            {
                // A malformed/incomplete release must not hide another valid update.
            }
        }
        return newest;
    }
    public static async Task<AppRelease?> CheckAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromSeconds(25));
        using var request = new HttpRequestMessage(HttpMethod.Get, ApiUrl);
        request.Headers.Accept.ParseAdd("application/vnd.github+json"); request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var response = await Client.SendAsync(request, timeout.Token).ConfigureAwait(false); response.EnsureSuccessStatusCode();
        return ParseReleases(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
    }
    public static async Task<DownloadedUpdate> DownloadAsync(AppRelease release, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!IsOfficialAsset(release.DownloadUrl)) throw new InvalidDataException("La descarga no pertenece al repositorio del programa.");
        string? expected = release.Sha256;
        if (expected == null)
        {
            if (release.ChecksumUrl == null || !IsOfficialAsset(release.ChecksumUrl)) throw new InvalidDataException("Falta la verificación de la descarga.");
            string sums = await Client.GetStringAsync(release.ChecksumUrl, cancellationToken).ConfigureAwait(false);
            expected = sums.Split('\n').Select(line => line.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                .Where(parts => parts.Length == 2 && parts[1].TrimStart('*') == "NightLightControl.exe").Select(parts => parts[0]).SingleOrDefault();
            if (expected == null) throw new InvalidDataException("La versión publicada no incluye la verificación del ejecutable.");
        }
        expected = NormalizeHash(expected);
        string folder = Path.Combine(UpdateFolder, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "NightLightControl.exe");
        try
        {
            using var response = await Client.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false); response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaximumDownload) throw new InvalidDataException("La descarga supera el tamaño permitido.");
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                byte[] buffer = new byte[81920]; long total = 0; int count;
                while ((count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    total += count; if (total > MaximumDownload) throw new InvalidDataException("La descarga supera el tamaño permitido.");
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                    progress?.Report(Math.Clamp(100.0 * total / release.Size, 0, 100));
                }
                if (total != release.Size) throw new InvalidDataException("La descarga está incompleta.");
            }
            VerifyFile(path, expected); progress?.Report(100); return new(path, expected);
        }
        catch { if (File.Exists(path)) File.Delete(path); if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder); throw; }
    }
    public static void VerifyFile(string path, string expectedHash)
    {
        expectedHash = NormalizeHash(expectedHash);
        using var stream = File.OpenRead(path); string actual = Convert.ToHexString(SHA256.HashData(stream));
        if (!actual.Equals(expectedHash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("La descarga no superó la verificación. El programa instalado no se ha modificado.");
    }
    public static void StartInstall(DownloadedUpdate download)
    {
        string source = ValidateSource(download.FilePath); VerifyFile(source, download.Sha256);
        string target = Environment.ProcessPath ?? throw new InvalidOperationException("No se pudo localizar el programa.");
        ValidateTarget(target);
        string helper = Path.Combine(Path.GetDirectoryName(source)!, "UpdateHelper.exe"); File.Copy(target, helper, false);
        var start = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(helper)! };
        foreach (string value in new[] { "--apply-update", source, target, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), download.Sha256 }) start.ArgumentList.Add(value);
        using var helperProcess = Process.Start(start) ?? throw new InvalidOperationException("No se pudo iniciar la instalación.");
    }
    private static string ValidateSource(string source)
    {
        string full = Path.GetFullPath(source), root = Path.GetFullPath(UpdateFolder) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(full) != "NightLightControl.exe") throw new InvalidDataException("Ruta de actualización no válida.");
        return full;
    }
    private static void ValidateTarget(string target)
    {
        if (Path.GetFileName(target) != "NightLightControl.exe" || !File.Exists(target) || Path.GetFullPath(target).StartsWith(Path.GetFullPath(UpdateFolder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Utiliza la versión portable instalada para actualizar el programa.");
    }
    public static void InstallFiles(string source, string target, string expectedHash)
    {
        // Verify before creating any installation file, and retain the previous executable.
        VerifyFile(source, expectedHash);
        string stage = target + ".new", backup = target + ".previous";
        File.Copy(source, stage, true);
        try { File.Replace(stage, target, backup, true); }
        catch { if (File.Exists(stage)) File.Delete(stage); throw; }
    }
    public static int ApplyUpdate(string[] args)
    {
        try
        {
            if (args.Length != 5 || args[0] != "--apply-update" || !int.TryParse(args[3], out int pid) || pid <= 0 || pid == Environment.ProcessId) throw new InvalidDataException("Parámetros de actualización no válidos.");
            string source = ValidateSource(args[1]), target = Path.GetFullPath(args[2]); ValidateTarget(target); VerifyFile(source, args[4]);
            try { using var parent = Process.GetProcessById(pid); if (!parent.WaitForExit(30000)) throw new TimeoutException("El programa no terminó a tiempo para actualizarse."); }
            catch (ArgumentException) { }
            InstallFiles(source, target, args[4]);
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(target)! })?.Dispose();
            return 0;
        }
        catch (Exception ex) { Controller.Log("Actualización: " + ex); return 1; }
    }
}
