using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Cryptography;
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
    private sealed class Worker(Process process, string path, RecoveryState state)
    {
        public Process Process { get; } = process;
        public string Path { get; } = path;
        public RecoveryState State { get; set; } = state;
    }
    private Task<Worker>? preparation;
    private Worker? worker;
    public bool SetActive(bool active)
    {
        if (worker != null && worker.Process.HasExited) { worker.Process.Dispose(); worker = null; }
        if (worker == null && preparation is { IsCompleted: true })
        {
            if (!active && preparation.IsFaulted) { _ = preparation.Exception; preparation = null; return true; }
            var completed = preparation; preparation = null; worker = completed.GetAwaiter().GetResult();
        }
        if (worker != null && worker.Process.HasExited) { worker.Process.Dispose(); worker = null; }
        if (worker == null)
        {
            if (!active) return true;
            preparation ??= Task.Run(PrepareWorker); return false;
        }
        if (worker.State.Active != active) { var next = worker.State with { Active = active }; CrashRecovery.WriteState(worker.Path, next); worker.State = next; }
        return true;
    }
    private static Worker PrepareWorker()
    {
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("No se pudo preparar la recuperación.");
        string cache = System.IO.Path.Combine(CrashRecovery.Folder, Updates.CurrentVersionText); Directory.CreateDirectory(cache);
        string helperPath = System.IO.Path.Combine(cache, "RecoveryHelper.exe");
        // Copy atomically: interrupted preparation must not poison the next startup.
        if (!SameExecutable(executable, helperPath))
        {
            string temporary = helperPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.Copy(executable, temporary, false); File.Move(temporary, helperPath, true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        using var current = Process.GetCurrentProcess();
        var state = new RecoveryState(false, current.Id, current.StartTime.ToUniversalTime().Ticks);
        string path = System.IO.Path.Combine(CrashRecovery.Folder, Guid.NewGuid().ToString("N") + ".json"); CrashRecovery.WriteState(path, state);
        var start = new ProcessStartInfo(helperPath) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = cache };
        foreach (string value in new[] { "--recover-after-exit", state.ParentId.ToString(), state.ParentStartTicks.ToString(), path }) start.ArgumentList.Add(value);
        Process? process = null;
        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException("No se pudo iniciar la recuperación de color.");
            var deadline = Stopwatch.StartNew();
            while (!File.Exists(path + ".ready"))
            {
                if (process.HasExited || deadline.ElapsedMilliseconds > 10000) throw new InvalidOperationException("No se pudo preparar la protección de color. Reintenta o reinstala la aplicación.");
                Thread.Sleep(20);
            }
            return new Worker(process, path, state);
        }
        catch
        {
            if (process != null) { if (!process.HasExited) { process.Kill(); process.WaitForExit(); } process.Dispose(); }
            if (File.Exists(path)) File.Delete(path); if (File.Exists(path + ".ready")) File.Delete(path + ".ready"); throw;
        }
    }
    public static bool SameExecutable(string source, string candidate)
    {
        if (!File.Exists(candidate) || new FileInfo(source).Length != new FileInfo(candidate).Length) return false;
        using var original = File.OpenRead(source); using var copy = File.OpenRead(candidate);
        return CryptographicOperations.FixedTimeEquals(SHA256.HashData(original), SHA256.HashData(copy));
    }
    // Do not disarm when a native restoration failed: the worker must still retry on exit.
    public void Dispose()
    {
        worker?.Process.Dispose();
        if (preparation is { IsCompletedSuccessfully: true }) preparation.Result.Process.Dispose();
    }
}
