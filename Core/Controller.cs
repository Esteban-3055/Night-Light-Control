using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using NightLightControl.Native;

namespace NightLightControl.Core;

public sealed class Controller : IDisposable
{
    private readonly IDisplayEngine engine;
    private readonly Func<WindowsState> readNative;
    private readonly Func<double> secondsNow;
    private readonly Func<DateTimeOffset> localNow;
    private readonly Func<bool> detectFlux;
    private readonly Func<string?> foregroundBlock;
    public Action<bool>? OwnershipChanged { get; set; }
    public Action<string> ErrorLog { get; set; } = Log;
    private double lastSeconds, lastApplySeconds = -100, lastConflictCheck = -100, lastErrorSeconds = -100;
    private double displayChangeUntil = -100, nextDisplayRetry, displayRetryStarted = -1, lastDisplayLog = -100;
    private bool fluxRunning, needsRefresh = true, ownsColor;
    private int lastKelvin = -1;
    private double? appliedStrength;
    public Settings Settings { get; }
    public DateTimeOffset? PauseUntil { get; private set; }
    public string Status { get; private set; } = "Inactivo";
    public string Displays { get; private set; } = "Comprobando pantallas…";
    public string Backend { get; private set; } = "Windows nativo";
    public double TargetStrength { get; private set; }
    public double CurrentStrength => appliedStrength ?? 0;
    public bool IsApplying => ownsColor && LastError == null;
    public bool IsFadingOut { get; private set; }
    public bool IsTransitioning => ownsColor && appliedStrength.HasValue && Math.Abs(TargetStrength - appliedStrength.Value) > 0.001;
    public bool IsRefreshingDisplays => needsRefresh;
    public bool Available { get; private set; }
    public bool SessionLocked { get; set; }
    public bool Suspended { get; set; }
    public string? LastError { get; private set; }

    public Controller(Settings settings) : this(settings, new DisplayEngine(), WindowsSettings.Read, Stopwatch.StartNew()) { }
    private Controller(Settings settings, IDisplayEngine engine, Func<WindowsState> readNative, Stopwatch clock)
        : this(settings, engine, readNative, () => clock.Elapsed.TotalSeconds, () => DateTimeOffset.Now, IsFluxRunning) { }

    // Test the actual state machine with a fake display and clock, without accessing Windows.
    public Controller(Settings settings, IDisplayEngine engine, Func<WindowsState> readNative,
        Func<double> secondsNow, Func<DateTimeOffset> localNow, Func<bool> detectFlux, Func<string?>? foregroundBlock = null)
    {
        Settings = settings; this.engine = engine; this.readNative = readNative;
        this.secondsNow = secondsNow; this.localNow = localNow; this.detectFlux = detectFlux;
        this.foregroundBlock = foregroundBlock ?? ActiveExclusion;
    }
    public void Refresh() { needsRefresh = true; lastApplySeconds = -100; nextDisplayRetry = 0; displayRetryStarted = secondsNow(); displayChangeUntil = secondsNow() + 5; LastError = null; }
    public void Pause(TimeSpan duration) { PauseUntil = localNow().Add(duration); }
    public void Resume() { PauseUntil = null; }
    public void Tick()
    {
        double seconds = secondsNow(), elapsed = Math.Clamp(seconds - lastSeconds, 0, 2); lastSeconds = seconds;
        if (LastError != null && seconds - lastErrorSeconds < 5) return;
        try
        {
            if (SessionLocked || Suspended) { Restore(); Status = "En pausa: sesión suspendida"; LastError = null; return; }
            if (needsRefresh)
            {
                if (seconds < nextDisplayRetry) return;
                try { engine.Refresh(); }
                catch (Exception ex) { RetryDisplayChange(ex, seconds); return; }
                Displays = engine.Description; Backend = engine.Backend; Available = true; needsRefresh = false; displayRetryStarted = -1;
            }
            if (!Settings.Enabled && !ownsColor) { Status = "Control desactivado"; LastError = null; return; }
            var native = readNative(); var now = localNow();
            if (seconds - lastConflictCheck >= 3) { fluxRunning = detectFlux(); lastConflictCheck = seconds; }
            if (PauseUntil <= now) PauseUntil = null;
            // Never overwrite Windows' own strength-slider preview, even during disable or exit.
            if (native.Preview) { ReleaseOwnership(); Status = "En pausa: ajustando Luz nocturna en Windows"; LastError = null; return; }
            string? block = fluxRunning ? "En pausa: f.lux está abierto" : PauseUntil.HasValue ? "En pausa hasta las " + PauseUntil.Value.ToLocalTime().ToString("HH:mm") : foregroundBlock();
            if (block != null) { Restore(); Status = block; LastError = null; return; }

            bool wasFadingOut = IsFadingOut;
            IsFadingOut = !Settings.Enabled;
            TargetStrength = IsFadingOut ? Strength(native.Kelvin) : Settings.Automatic ? Curve.Evaluate(Settings.Current.Points, now.TimeOfDay.TotalMinutes) : Settings.ManualStrength;
            bool starting = !ownsColor;
            if (starting) appliedStrength = Strength(native.Kelvin);
            // State changes start from the displayed color, including re-enable during fade-out.
            if (starting || wasFadingOut != IsFadingOut) elapsed = 0;
            double step = elapsed * 100 / Settings.TransitionSeconds;
            double current = appliedStrength!.Value;
            current += Math.Clamp(TargetStrength - current, -step, step); appliedStrength = current;
            int kelvin = Curve.Kelvin(current);
            OwnershipChanged?.Invoke(true);
            ownsColor = true; // Restore even when a native call succeeds on only some displays.
            if (kelvin != lastKelvin || seconds - lastApplySeconds >= 10)
            {
                engine.Apply(kelvin); lastKelvin = kelvin; lastApplySeconds = seconds;
            }
            if (IsFadingOut && kelvin == native.Kelvin) { Restore(); Status = "Control desactivado"; }
            else Status = IsFadingOut ? "Desactivando suavemente…" : (Settings.Automatic ? "Siguiendo tu curva" : "Intensidad manual") + (native.ScheduleEnabled ? " · horario de Windows activo" : "");
            Available = true; LastError = null;
        }
        catch (Exception ex)
        {
            if (seconds <= displayChangeUntil) { RetryDisplayChange(ex, seconds); return; }
            ErrorLog(ex.ToString()); Available = false; needsRefresh = true; lastErrorSeconds = seconds;
            try { Restore(); } catch (Exception restore) { ErrorLog("Restauración pendiente: " + restore); }
            LastError = ex.Message; Status = "No se pudo aplicar: " + ex.Message;
        }
    }
    private void RetryDisplayChange(Exception error, double seconds)
    {
        // Display topology may disappear briefly while Windows switches outputs.
        // Preserve ownership and current warmth; do not restore neutral or turn off settings.
        if (displayRetryStarted < 0) displayRetryStarted = seconds;
        needsRefresh = true; Available = false; LastError = null; lastApplySeconds = -100;
        nextDisplayRetry = seconds + (seconds - displayRetryStarted < 10 ? .25 : 1);
        Status = "Reconectando pantallas… se conserva el control";
        if (seconds - lastDisplayLog >= 5) { ErrorLog("Cambio de pantalla: " + error.Message); lastDisplayLog = seconds; }
    }
    private static double Strength(int kelvin) => Math.Clamp((6500 - kelvin) / 53.0, 0, 100);
    private void ReleaseOwnership() { OwnershipChanged?.Invoke(false); ownsColor = false; appliedStrength = null; IsFadingOut = false; lastKelvin = -1; }
    private static bool IsFluxRunning()
    {
        var processes = Process.GetProcessesByName("flux");
        try { return processes.Length != 0; } finally { foreach (var p in processes) p.Dispose(); }
    }
    private string? ActiveExclusion()
    {
        if (Settings.ExcludedApps.Count == 0 && !Settings.PauseFullscreen) return null;
        nint window = GetForegroundWindow(); if (window == 0) return null;
        GetWindowThreadProcessId(window, out uint pid);
        try
        {
            using var p = Process.GetProcessById((int)pid);
            if (Settings.ExcludedApps.Any(s => string.Equals(Path.GetFileNameWithoutExtension(s), p.ProcessName, StringComparison.OrdinalIgnoreCase))) return "En pausa: " + p.ProcessName;
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
        if (!ownsColor) return;
        if (engine.HasApplied) { var native = readNative(); if (!native.Preview) engine.Apply(native.Kelvin); }
        ReleaseOwnership();
    }
    public bool TryRestore()
    {
        try { Restore(); return true; } catch (Exception ex) { LastError = ex.Message; ErrorLog("Restauración: " + ex); return false; }
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
