using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace NightLightControl.Core;

public static class Installation
{
    public const string RegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\NightLightControl";
    public static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Night Light Control");
    public static string? RegisteredDirectory { get { using var key = Registry.CurrentUser.OpenSubKey(RegistryKey); return key?.GetValue("InstallLocation") as string; } }
    public static bool IsInstalled => Environment.ProcessPath is string exe && RegisteredDirectory is string folder && SamePath(exe, Path.Combine(folder, "NightLightControl.exe"));
    public static string StartShortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Night Light Control.lnk");
    public static string DesktopShortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Night Light Control.lnk");
    public static bool SamePath(string a, string b) => Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar).Equals(Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    public static string EntryPath(string directory, string entry)
    {
        if (Path.IsPathRooted(entry) || entry.Replace('\\', '/').Split('/').Any(part => part is "." or ".." || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || part.EndsWith('.') || part.EndsWith(' '))) throw new InvalidDataException("Ruta de paquete no válida.");
        string root = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
        string target = Path.GetFullPath(Path.Combine(root, entry));
        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("El paquete contiene una ruta fuera de la instalación.");
        return target;
    }
    public static bool IsPackageFile(string name) => name is "NightLightControl.exe" or "LEEME.md" or "THIRD-PARTY-NOTICES.txt" || name.Replace('\\', '/').StartsWith("docs/", StringComparison.Ordinal) && !name.EndsWith("/", StringComparison.Ordinal);
    public static void Install(Stream package, string directory, bool startup, bool desktop)
    {
        directory = Path.GetFullPath(directory);
        if (SamePath(directory, Settings.Folder) || SamePath(directory, Path.GetPathRoot(directory)!)) throw new InvalidDataException("Elige una carpeta exclusiva para el programa.");
        if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any() && (RegisteredDirectory == null || !SamePath(directory, RegisteredDirectory))) throw new IOException("La carpeta elegida contiene otros archivos. Elige una carpeta vacía.");
        using var prepared = PreparedInstallation.Create(package);
        EnsureNoReparsePoints(directory, EntryPath(directory, "installed-files.json"));
        foreach (var process in System.Diagnostics.Process.GetProcessesByName("NightLightControl"))
        {
            using (process) if (process.Id != Environment.ProcessId) throw new IOException("Cierra Night Light Control con Salir y restaurar antes de instalar.");
        }
        Directory.CreateDirectory(directory);
        foreach (var file in prepared.Files)
        {
            string target = EntryPath(directory, file); EnsureNoReparsePoints(directory, target); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            string temporary = target + ".installing";
            EnsureNoReparsePoints(directory, temporary);
            try
            {
                File.Copy(EntryPath(prepared.Folder, file), temporary, true);
                if (file == "NightLightControl.exe" && File.Exists(target))
                {
                    string backup = target + ".previous"; EnsureNoReparsePoints(directory, backup);
                    File.Replace(temporary, target, backup, true);
                }
                else File.Move(temporary, target, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        string exe = Path.Combine(directory, "NightLightControl.exe");
        using (var key = Registry.CurrentUser.CreateSubKey(RegistryKey))
        {
            key.SetValue("DisplayName", "Night Light Control"); key.SetValue("DisplayVersion", Updates.CurrentVersionText);
            key.SetValue("Publisher", "Esteban-3055"); key.SetValue("InstallLocation", directory); key.SetValue("DisplayIcon", exe);
            key.SetValue("UninstallString", Quote(exe) + " --uninstall"); key.SetValue("QuietUninstallString", Quote(exe) + " --uninstall --silent");
            key.SetValue("URLInfoAbout", Updates.RepositoryUrl); key.SetValue("NoModify", 1); key.SetValue("NoRepair", 1);
            key.SetValue("EstimatedSize", (int)(prepared.Files.Sum(f => new FileInfo(EntryPath(prepared.Folder, f)).Length) / 1024));
        }
        File.WriteAllText(EntryPath(directory, "installed-files.json"), JsonSerializer.Serialize(prepared.Files));
        using (var run = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
        {
            if (startup) run.SetValue("NightLightControl", Quote(exe) + " --tray"); else run.DeleteValue("NightLightControl", false);
        }
        CreateShortcut(StartShortcut, exe); if (desktop) CreateShortcut(DesktopShortcut, exe);
    }
    public static string Quote(string path) => "\"" + path + "\"";
    public static void EnsureNoReparsePoints(string directory, string path)
    {
        string root = Path.GetFullPath(directory), current = Path.GetFullPath(path);
        if (!SamePath(root, current) && !current.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Ruta fuera de la instalación.");
        while (true)
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("La instalación contiene un enlace de archivos inesperado.");
            if (SamePath(current, root)) break; current = Path.GetDirectoryName(current)!;
        }
    }
    public static void RemoveApplicationFiles(string directory)
    {
        string manifest = EntryPath(directory, "installed-files.json");
        EnsureNoReparsePoints(directory, manifest);
        string[] files = File.Exists(manifest) ? JsonSerializer.Deserialize<string[]>(File.ReadAllText(manifest)) ?? throw new InvalidDataException("Lista de instalación inválida.") : new[] { "NightLightControl.exe", "LEEME.md", "THIRD-PARTY-NOTICES.txt", "docs/interface.png", "docs/preferences.png", "docs/profiles.png", "docs/updates.png", "docs/REVISION.md" };
        if (files.Count(f => f == "NightLightControl.exe") != 1 || files.Any(f => !IsPackageFile(f))) throw new InvalidDataException("Lista de archivos de instalación no válida.");
        var targets = files.Append("NightLightControl.exe.previous").Append("installed-files.json").Select(f => EntryPath(directory, f)).ToArray();
        foreach (var target in targets) EnsureNoReparsePoints(directory, target);
        foreach (var target in targets) if (File.Exists(target)) File.Delete(target);
        foreach (var folder in targets.Select(f => Path.GetDirectoryName(f)!).Where(f => !SamePath(f, directory)).Distinct().OrderByDescending(f => f.Length))
            if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
        if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
    }
    private static void CreateShortcut(string path, string executable)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        object shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        object? shortcut = null;
        try { dynamic automation = shell; shortcut = automation.CreateShortcut(path); dynamic link = shortcut; link.TargetPath = executable; link.WorkingDirectory = Path.GetDirectoryName(executable); link.IconLocation = executable + ",0"; link.Save(); }
        finally { if (shortcut != null) Marshal.FinalReleaseComObject(shortcut); Marshal.FinalReleaseComObject(shell); }
    }
    public static void Uninstall(string directory)
    {
        if (RegisteredDirectory == null || !SamePath(directory, RegisteredDirectory)) throw new InvalidDataException("La instalación no coincide con la registrada.");
        directory = Path.GetFullPath(directory);
        RemoveApplicationFiles(directory);
        using (var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
        {
            if (run?.GetValue("NightLightControl") is string command && command.Equals(Quote(Path.Combine(directory, "NightLightControl.exe")) + " --tray", StringComparison.OrdinalIgnoreCase)) run.DeleteValue("NightLightControl", false);
        }
        foreach (string link in new[] { StartShortcut, DesktopShortcut }) if (File.Exists(link)) File.Delete(link);
        Registry.CurrentUser.DeleteSubKeyTree(RegistryKey, false);
    }
}
