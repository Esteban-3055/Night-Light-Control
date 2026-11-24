using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;

namespace NightLightControl.Core;

public static class Maintenance
{
    public static string ExitEventName => @"Local\NightLightControl-Exit-" + Environment.UserName;
    public static void RequestExit() { if (EventWaitHandle.TryOpenExisting(ExitEventName, out var signal)) using (signal) signal.Set(); }
    public static int Uninstall(string[] args)
    {
        try
        {
            if (!Installation.IsInstalled) throw new InvalidOperationException("Esta copia no corresponde a una instalación registrada.");
            if (!args.Contains("--silent") && MessageBox.Show("¿Desinstalar Night Light Control? Se conservarán tus perfiles.", "Night Light Control", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return 0;
            RequestExit();
            string folder = Path.Combine(Settings.Folder, "maintenance", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
            string helper = Path.Combine(folder, "UninstallHelper.exe"); File.Copy(Environment.ProcessPath!, helper);
            var start = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = folder };
            foreach (string value in new[] { "--uninstall-worker", Installation.RegisteredDirectory!, Environment.ProcessId.ToString() }) start.ArgumentList.Add(value);
            Process.Start(start)?.Dispose(); return 0;
        }
        catch (Exception ex) { Controller.Log("Desinstalación: " + ex); if (!args.Contains("--silent")) MessageBox.Show(ex.Message, "Night Light Control"); return 1; }
    }
    public static int UninstallWorker(string[] args)
    {
        try
        {
            if (args.Length != 3 || !int.TryParse(args[2], out int pid) || pid == Environment.ProcessId) return 1;
            try { using var parent = Process.GetProcessById(pid); if (!parent.WaitForExit(30000)) return 2; } catch (ArgumentException) { }
            var deadline = Stopwatch.StartNew();
            while (true)
            {
                var processes = Process.GetProcessesByName("NightLightControl"); bool running = processes.Length != 0; foreach (var process in processes) process.Dispose();
                if (!running) break; if (deadline.ElapsedMilliseconds > 30000) throw new IOException("El programa sigue abierto. Cierra con Salir y restaurar."); Thread.Sleep(100);
            }
            Installation.Uninstall(args[1]); return 0;
        }
        catch (Exception ex) { Controller.Log("Desinstalación: " + ex); return 1; }
    }
}
