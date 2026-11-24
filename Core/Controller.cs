using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using NightLightControl.Native;

namespace NightLightControl.Core;

public sealed class Controller : IDisposable
{
    private readonly DisplayEngine engine = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private double lastSeconds, lastApplySeconds = -100;
    private double lastConflictCheck = -100;
    private bool fluxRunning;
    private int lastKelvin = -1;
    private bool needsRefresh = true, wasBlocked, wasEnabled;
    private double? appliedStrength;
    public Settings Settings { get; }
    public DateTimeOffset? PauseUntil { get; private set; }
    public string Status { get; private set; } = "Inactivo";
    public string Displays { get; private set; } = "Comprobando pantallas…";
    public string Backend { get; private set; } = "Windows nativo";
    public double TargetStrength { get; private set; }
    public double CurrentStrength => appliedStrength ?? 0;
    public bool IsTransitioning => wasEnabled && appliedStrength.HasValue && Math.Abs(TargetStrength - appliedStrength.Value) > 0.02;
    public bool Available { get; private set; }
    public bool SessionLocked { get; set; }
    public bool Suspended { get; set; }
    public string? LastError { get; private set; }
    public Controller(Settings settings) { Settings = settings; }
    public void Refresh() { needsRefresh = true; lastApplySeconds = -100; }
    public void Pause(TimeSpan duration) { PauseUntil = DateTimeOffset.UtcNow.Add(duration); }
    public void Resume() { PauseUntil = null; }
    public void Tick()
    {
        double seconds = clock.Elapsed.TotalSeconds, elapsed = Math.Clamp(seconds - lastSeconds, 0, 2); lastSeconds = seconds;
        if (LastError != null && seconds - lastApplySeconds < 5) return;
        try
        {
            if (needsRefresh)
            {
                engine.Refresh(); Displays = engine.Description; Backend = engine.Backend; Available = true; needsRefresh = false;
            }
            if (!Settings.Enabled)
            {
                if (!wasBlocked && engine.HasApplied) Restore();
                wasBlocked = true; wasEnabled = false; appliedStrength = null; Status = "Control desactivado"; LastError = null;
                return;
            }
            var native = WindowsSettings.Read();
            var now = DateTime.Now;
            TargetStrength = Settings.Automatic ? Curve.Evaluate(Settings.Current.Points, now.TimeOfDay.TotalMinutes) : Settings.ManualStrength;
            if (seconds - lastConflictCheck >= 3)
            {
                var fluxProcesses = Process.GetProcessesByName("flux"); fluxRunning = fluxProcesses.Length != 0;
                foreach (var process in fluxProcesses) process.Dispose(); lastConflictCheck = seconds;
            }
            if (PauseUntil <= DateTimeOffset.UtcNow) PauseUntil = null;
            string? exclusion = ActiveExclusion();
            string? block = !Settings.Enabled ? "Control desactivado" : SessionLocked || Suspended ? "En pausa: sesión suspendida" : fluxRunning ? "En pausa: f.lux está abierto" : native.Preview ? "En pausa: ajustando Luz nocturna en Windows" : PauseUntil.HasValue ? "En pausa hasta las " + PauseUntil.Value.ToLocalTime().ToString("HH:mm") : exclusion;
            if (block != null)
            {
                // Do not overwrite a Windows slider preview. Release our runtime tint otherwise.
                if (!wasBlocked && engine.HasApplied && !native.Preview) Restore();
                wasBlocked = true; wasEnabled = false; appliedStrength = null;
                Status = block; LastError = null; return;
            }
            if (wasBlocked || !wasEnabled || appliedStrength == null) appliedStrength = Math.Clamp((6500 - native.Kelvin) / 53.0, 0, 100);
            wasBlocked = false; wasEnabled = true;
            double maxStep = elapsed * 100 / Settings.TransitionSeconds;
            appliedStrength += Math.Clamp(TargetStrength - appliedStrength.Value, -maxStep, maxStep);
            int kelvin = Curve.Kelvin(appliedStrength.Value);
            if (kelvin != lastKelvin || seconds - lastApplySeconds >= 10)
            {
                engine.Apply(kelvin); lastKelvin = kelvin; lastApplySeconds = seconds;
            }
            Status = (Settings.Automatic ? "Siguiendo tu curva" : "Intensidad manual") + (native.ScheduleEnabled ? " · horario de Windows activo" : "");
            LastError = null;
        }
        catch (Exception ex)
        {
            LastError = ex.Message; Status = "No se pudo aplicar: " + ex.Message; Available = false; needsRefresh = true; lastApplySeconds = seconds;
            Log(ex.ToString());
            try { Restore(); } catch (Exception restore) { Log("Restauración pendiente: " + restore); }
            appliedStrength = null; wasEnabled = false;
        }
    }
    private string? ActiveExclusion()
    {
        if (Settings.ExcludedApps.Count == 0 && !Settings.PauseFullscreen) return null;
        nint window = GetForegroundWindow(); if (window == 0) return null;
        GetWindowThreadProcessId(window, out uint pid);
        try
        {
            using var p = Process.GetProcessById((int)pid);
            if (Settings.ExcludedApps.Any(s => string.Equals(System.IO.Path.GetFileNameWithoutExtension(s), p.ProcessName, StringComparison.OrdinalIgnoreCase))) return "En pausa: " + p.ProcessName;
            if (Settings.PauseFullscreen && pid != Environment.ProcessId && p.ProcessName is not "explorer" and not "dwm" && GetWindowRect(window, out var rect))
            {
                var monitor = System.Windows.Forms.Screen.FromHandle(window).Bounds;
                if (rect.Left <= monitor.Left && rect.Top <= monitor.Top && rect.Right >= monitor.Right && rect.Bottom >= monitor.Bottom) return "En pausa: pantalla completa";
            }
        }
        catch (ArgumentException) { } catch (System.ComponentModel.Win32Exception) { }
        return null;
    }
    public void Restore()
    {
        if (!engine.HasApplied) return;
        engine.Apply(WindowsSettings.Read().Kelvin); lastKelvin = -1;
    }
    public bool TryRestore()
    {
        try { Restore(); return true; } catch (Exception ex) { LastError = ex.Message; Log("Restauración: " + ex); return false; }
    }
    public void Dispose() { TryRestore(); engine.Dispose(); }
    public static void Log(string value)
    {
        try { Directory.CreateDirectory(Settings.Folder); string path = Path.Combine(Settings.Folder, "diagnostic.log"); if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024) File.Move(path, path + ".old", true); File.AppendAllText(path, DateTimeOffset.Now + " " + value + Environment.NewLine); } catch { }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rect rect);
}
