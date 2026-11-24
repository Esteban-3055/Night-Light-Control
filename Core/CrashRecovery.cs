using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using NightLightControl.Native;

namespace NightLightControl.Core;

public sealed record RecoveryState(bool Active, int ParentId, long ParentStartTicks);

public static class CrashRecovery
{
    public static string Folder => Path.Combine(Settings.Folder, "recovery");
    public static void WriteState(string path, RecoveryState state)
    {
        string temporary = path + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(state)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static void WaitForParent(int pid, long startTicks)
    {
        if (pid <= 0 || pid == Environment.ProcessId) throw new ArgumentOutOfRangeException(nameof(pid));
        try
        {
            using var parent = Process.GetProcessById(pid);
            if (parent.StartTime.ToUniversalTime().Ticks == startTicks) parent.WaitForExit();
        }
        catch (ArgumentException) { } // Parent already exited; do not wait on a reused PID.
        catch (InvalidOperationException) { } // Exit between lookup and StartTime/WaitForExit.
    }
    public static bool RecoverIfArmed(string path, int pid, long ticks, Action restore)
    {
        var state = JsonSerializer.Deserialize<RecoveryState>(File.ReadAllText(path));
        if (state == null || state.ParentId != pid || state.ParentStartTicks != ticks) throw new InvalidDataException("Estado de recuperación no válido.");
        if (state.Active) restore();
        File.Delete(path); return state.Active;
    }
    public static bool RestoreRequestedColor(IDisplayEngine engine, Func<WindowsState> readNative)
    {
        if (readNative().Preview) return false;
        engine.Refresh(); var state = readNative();
        if (state.Preview) return false;
        engine.Apply(state.Kelvin); return true;
    }
    public static int Run(string[] args)
    {
        string? ready = null;
        try
        {
            if (args.Length != 4 || !int.TryParse(args[1], out int pid) || !long.TryParse(args[2], out long ticks)) throw new InvalidDataException("Parámetros de recuperación no válidos.");
            string path = Path.GetFullPath(args[3]);
            if (!path.StartsWith(Path.GetFullPath(Folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out _)) throw new InvalidDataException("Ruta de recuperación no válida.");
            ready = path + ".ready"; File.WriteAllText(ready, "ready");
            WaitForParent(pid, ticks);
            RecoverIfArmed(path, pid, ticks, RestoreWithRetries); return 0;
        }
        catch (Exception ex) { Controller.Log("Recuperación tras cierre: " + ex); return 1; }
        finally { try { if (ready != null && File.Exists(ready)) File.Delete(ready); } catch (IOException ex) { Controller.Log("Limpieza de recuperación: " + ex.Message); } }
    }
    private static void RestoreWithRetries()
    {
        Exception? last = null;
        for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    using var engine = new DisplayEngine();
                    bool restored = RestoreRequestedColor(engine, WindowsSettings.Read);
                    Controller.Log(restored ? "Se restauró el color de Windows tras un cierre inesperado." : "Recuperación omitida: Windows está mostrando su vista previa.");
                    return;
                }
                catch (Exception ex) { last = ex; if (attempt < 2) Thread.Sleep(1000); }
            }
        throw last!;
    }
}

public sealed class CrashGuard : IDisposable
{
    private Process? helper;
    private string? statePath;
    private RecoveryState? lastState;
    public void SetActive(bool active)
    {
        if (lastState?.Active == active && (!active || helper is { HasExited: false })) return;
        if (helper == null && !active) return;
        if (helper == null || helper.HasExited)
        {
            string executable = Environment.ProcessPath ?? throw new InvalidOperationException("No se pudo preparar la recuperación.");
            string cache = Path.Combine(CrashRecovery.Folder, Updates.CurrentVersionText); Directory.CreateDirectory(cache);
            string worker = Path.Combine(cache, "RecoveryHelper.exe");
            if (!File.Exists(worker)) File.Copy(executable, worker, false);
            using var current = Process.GetCurrentProcess();
            lastState = new(active, current.Id, current.StartTime.ToUniversalTime().Ticks);
            statePath = Path.Combine(CrashRecovery.Folder, Guid.NewGuid().ToString("N") + ".json");
            CrashRecovery.WriteState(statePath, lastState);
            var start = new ProcessStartInfo(worker) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = cache };
            foreach (string argument in new[] { "--recover-after-exit", lastState.ParentId.ToString(), lastState.ParentStartTicks.ToString(), statePath }) start.ArgumentList.Add(argument);
            try
            {
                helper?.Dispose(); helper = Process.Start(start) ?? throw new InvalidOperationException("No se pudo iniciar la protección de recuperación.");
                var deadline = Stopwatch.StartNew();
                while (!File.Exists(statePath + ".ready"))
                {
                    if (helper.HasExited || deadline.ElapsedMilliseconds > 5000) throw new InvalidOperationException("No se pudo iniciar la recuperación de color. Usa la versión portable publicada.");
                    Thread.Sleep(20);
                }
            }
            catch
            {
                CrashRecovery.WriteState(statePath, lastState with { Active = false });
                if (helper != null) { if (!helper.HasExited) helper.Kill(); helper.Dispose(); }
                helper = null; lastState = null; throw;
            }
        }
        else
        {
            var next = lastState! with { Active = active }; CrashRecovery.WriteState(statePath!, next); lastState = next;
        }
    }
    // A normal controller restoration disarms the state. If restoration failed,
    // leave it armed so that the independent process can retry after we exit.
    public void Dispose() => helper?.Dispose();
}
