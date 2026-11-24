using System;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace NightLightControl.Core;

public sealed class PreparedInstallation : IDisposable
{
    public string Folder { get; }
    public string[] Files { get; }
    private const string Prefix = "NightLightControl-stage-";
    private PreparedInstallation(string folder, string[] files) { Folder = folder; Files = files; }
    public static PreparedInstallation Create(Stream stream)
    {
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, true);
        var files = archive.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToArray();
        if (files.Count(e => e.FullName == "NightLightControl.exe") != 1 || files.Any(e => !Installation.IsPackageFile(e.FullName)) || files.Sum(e => e.Length) > 500_000_000) throw new InvalidDataException("El paquete de instalación no es válido.");
        string folder = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
        var paths = files.Select(e => Installation.EntryPath(folder, e.FullName)).ToArray();
        if (paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Length) throw new InvalidDataException("El paquete contiene rutas duplicadas.");
        var prepared = new PreparedInstallation(folder, files.Select(e => e.FullName.Replace('\\', '/')).ToArray());
        Directory.CreateDirectory(folder);
        try
        {
            long total = 0; byte[] buffer = new byte[81920];
            for (int i = 0; i < files.Length; i++)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(paths[i])!);
                using var input = files[i].Open(); using var output = File.Create(paths[i]); long entryBytes = 0; int count;
                while ((count = input.Read(buffer)) > 0)
                {
                    total += count; entryBytes += count;
                    if (total > 500_000_000 || entryBytes > files[i].Length) throw new InvalidDataException("El paquete excede el tamaño permitido.");
                    output.Write(buffer, 0, count);
                }
                if (entryBytes != files[i].Length) throw new InvalidDataException("El paquete está incompleto.");
            }
            return prepared;
        }
        catch { prepared.Dispose(); throw; }
    }
    public void Dispose()
    {
        // Only this private, GUID-named temporary directory may be removed recursively.
        string full = Path.GetFullPath(Folder);
        if (!Installation.SamePath(Path.GetDirectoryName(full)!, Path.GetTempPath()) || !Path.GetFileName(full).StartsWith(Prefix, StringComparison.Ordinal) || !Guid.TryParseExact(Path.GetFileName(full)[Prefix.Length..], "N", out _)) throw new InvalidDataException("Carpeta temporal inesperada.");
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }
}
