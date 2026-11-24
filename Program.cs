using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Windows;
using NightLightControl.Core;
using NightLightControl.Native;
using NightLightControl.UI;

namespace NightLightControl;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--shutdown")) { Maintenance.RequestExit(); return 0; }
        if (args.Contains("--uninstall-worker")) return Maintenance.UninstallWorker(args);
        if (args.Contains("--uninstall")) return Maintenance.Uninstall(args);
        if (args.Contains("--recover-after-exit")) return CrashRecovery.Run(args);
        if (args.Contains("--apply-update") || args.Contains("--apply-installer")) return Updates.ApplyUpdate(args);
        if (args.Contains("--enable-startup")) Settings.StartWithWindows = true;
        if (args.Contains("--check-updates"))
        {
            int outputIndex = Array.IndexOf(args, "--out");
            string outputPath = outputIndex >= 0 && outputIndex + 1 < args.Length ? Path.GetFullPath(args[outputIndex + 1]) : Path.Combine(Settings.Folder, "update-check.json");
            try { var release = Updates.CheckAsync().GetAwaiter().GetResult(); Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!); File.WriteAllText(outputPath, JsonSerializer.Serialize(new { Current = Updates.CurrentVersionText, Release = release?.Tag, UpdateAvailable = release != null && release.Version > Updates.CurrentVersion }, new JsonSerializerOptions { WriteIndented = true })); return 0; }
            catch (Exception ex) { Controller.Log("Buscar actualizaciones: " + ex); return 1; }
        }
        if (args.Contains("--diagnose") || args.Contains("--verify-native")) return Diagnostic(args);
        using var mutex = new Mutex(true, @"Local\NightLightControl-" + Environment.UserName, out bool created);
        using var activate = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\NightLightControl-Show-" + Environment.UserName);
        if (!created) { activate.Set(); return 0; }
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += (_, e) => { Controller.Log(e.Exception.ToString()); if (app.MainWindow is MainWindow window) window.StopAfterUnexpectedError(); MessageBox.Show(e.Exception.Message, "Night Light Control", MessageBoxButton.OK, MessageBoxImage.Error); e.Handled = true; };
        try
        {
            var settings = Settings.Load(out string? warning);
            var window = new MainWindow(settings, activate);
            app.MainWindow = window;
            if (!args.Contains("--tray")) window.Show();
            if (warning != null) MessageBox.Show(warning, "Night Light Control");
            app.Run(); return 0;
        }
        catch (Exception ex) { Controller.Log(ex.ToString()); MessageBox.Show(ex.Message, "Night Light Control"); return 1; }
    }
    private static int Diagnostic(string[] args)
    {
        int index = Array.IndexOf(args, "--out");
        string path = index >= 0 && index + 1 < args.Length ? Path.GetFullPath(args[index + 1]) : Path.Combine(Settings.Folder, "native-check.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        object report; int code = 0;
        try
        {
            byte[] beforeSettings = WindowsSettings.ReadRecord(true), beforeState = WindowsSettings.ReadRecord(false);
            var native = WindowsSettings.Read();
            using var engine = new DisplayEngine(); engine.Refresh();
            bool verify = args.Contains("--verify-native"); bool restored = false;
            if (verify)
            {
                if (System.Diagnostics.Process.GetProcessesByName("flux").Length != 0) throw new InvalidOperationException("Cierra las otras aplicaciones de ajuste de color antes de la prueba.");
                try { engine.Apply(Math.Max(1200, native.Kelvin - 265)); Thread.Sleep(1500); }
                finally { engine.Apply(WindowsSettings.Read().Kelvin); restored = true; }
            }
            report = new { Success = true, OS = Environment.OSVersion.VersionString, engine.Description, engine.Backend, NativeState = native, ApplyTest = verify, Restored = restored,
                SettingsUnchanged = beforeSettings.SequenceEqual(WindowsSettings.ReadRecord(true)), StateUnchanged = beforeState.SequenceEqual(WindowsSettings.ReadRecord(false)),
                SettingsSha256 = Convert.ToHexString(SHA256.HashData(beforeSettings)), StateSha256 = Convert.ToHexString(SHA256.HashData(beforeState)), Timestamp = DateTimeOffset.Now };
        }
        catch (Exception ex) { report = new { Success = false, Error = ex.ToString(), Timestamp = DateTimeOffset.Now }; code = 1; }
        File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true })); return code;
    }
}
