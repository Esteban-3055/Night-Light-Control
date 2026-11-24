using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
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
        if (Path.IsPathRooted(entry) || entry.Replace('\\', '/').Split('/').Any(part => part is "." or "..")) throw new InvalidDataException("Ruta de paquete no válida.");
        string root = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
        string target = Path.GetFullPath(Path.Combine(root, entry));
        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("El paquete contiene una ruta fuera de la instalación.");
        return target;
    }
    private static bool Allowed(string name) => name is "NightLightControl.exe" or "LEEME.md" or "THIRD-PARTY-NOTICES.txt" || name.Replace('\\', '/').StartsWith("docs/", StringComparison.Ordinal) && !name.EndsWith("/", StringComparison.Ordinal);
    public static void Install(Stream package, string directory, bool startup, bool desktop)
    {
        directory = Path.GetFullPath(directory);
        if (SamePath(directory, Settings.Folder) || SamePath(directory, Path.GetPathRoot(directory)!)) throw new InvalidDataException("Elige una carpeta exclusiva para el programa.");
        if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any() && (RegisteredDirectory == null || !SamePath(directory, RegisteredDirectory))) throw new IOException("La carpeta elegida contiene otros archivos. Elige una carpeta vacía.");
        using var archive = new ZipArchive(package, ZipArchiveMode.Read, true);
        var files = archive.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToArray();
        if (files.Count(e => e.FullName == "NightLightControl.exe") != 1 || files.Any(e => !Allowed(e.FullName)) || files.Sum(e => e.Length) > 500_000_000) throw new InvalidDataException("El paquete de instalación no es válido.");
        foreach (var file in files) EntryPath(directory, file.FullName);
        foreach (var process in System.Diagnostics.Process.GetProcessesByName("NightLightControl"))
        {
            using (process) if (process.Id != Environment.ProcessId) throw new IOException("Cierra Night Light Control con Salir y restaurar antes de instalar.");
        }
        Directory.CreateDirectory(directory);
        foreach (var file in files)
        {
            string target = EntryPath(directory, file.FullName); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            string temporary = target + ".installing";
            try { file.ExtractToFile(temporary, true); File.Move(temporary, target, true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        string exe = Path.Combine(directory, "NightLightControl.exe");
        using (var key = Registry.CurrentUser.CreateSubKey(RegistryKey))
        {
            key.SetValue("DisplayName", "Night Light Control"); key.SetValue("DisplayVersion", Updates.CurrentVersionText);
            key.SetValue("Publisher", "Esteban-3055"); key.SetValue("InstallLocation", directory); key.SetValue("DisplayIcon", exe);
            key.SetValue("UninstallString", Quote(exe) + " --uninstall"); key.SetValue("QuietUninstallString", Quote(exe) + " --uninstall --silent");
            key.SetValue("URLInfoAbout", Updates.RepositoryUrl); key.SetValue("NoModify", 1); key.SetValue("NoRepair", 1);
            key.SetValue("EstimatedSize", (int)(files.Sum(e => e.Length) / 1024));
        }
        using (var run = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
        {
            if (startup) run.SetValue("NightLightControl", Quote(exe) + " --tray"); else run.DeleteValue("NightLightControl", false);
        }
        CreateShortcut(StartShortcut, exe); if (desktop) CreateShortcut(DesktopShortcut, exe);
    }
    public static string Quote(string path) => "\"" + path + "\"";
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
        // Delete only known application files. User configuration is in a different folder.
        foreach (string name in new[] { "NightLightControl.exe", "NightLightControl.exe.previous", "LEEME.md", "THIRD-PARTY-NOTICES.txt" })
        {
            string file = EntryPath(directory, name); if (File.Exists(file)) File.Delete(file);
        }
        string docs = EntryPath(directory, "docs");
        if (Directory.Exists(docs)) foreach (string file in Directory.EnumerateFiles(docs))
        {
            if (Path.GetExtension(file) is ".png" or ".md") File.Delete(file);
        }
        if (Directory.Exists(docs) && !Directory.EnumerateFileSystemEntries(docs).Any()) Directory.Delete(docs);
        if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
        using (var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
        {
            if (run?.GetValue("NightLightControl") is string command && command.Equals(Quote(Path.Combine(directory, "NightLightControl.exe")) + " --tray", StringComparison.OrdinalIgnoreCase)) run.DeleteValue("NightLightControl", false);
        }
        foreach (string link in new[] { StartShortcut, DesktopShortcut }) if (File.Exists(link)) File.Delete(link);
        Registry.CurrentUser.DeleteSubKeyTree(RegistryKey, false);
    }
}
