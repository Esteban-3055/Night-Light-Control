using System.Diagnostics;
using NightLightControl.Core;
using NightLightControl.Native;

internal static class RegressionTests
{
    public static void Run(Action<bool, string> assert, Action<double, double, string> near)
    {
        double seconds = 0; var native = new WindowsState(6500, false, false); bool flux = false; string? blocked = null;
        var settings = new Settings { Enabled = false, Automatic = false, ManualStrength = 60, TransitionSeconds = 10 };
        var display = new FakeDisplay(); var date = new DateTimeOffset(2026, 11, 24, 21, 0, 0, TimeSpan.FromHours(-3));
        using (var controller = new Controller(settings, display, () => native, () => seconds, () => date.AddSeconds(seconds), () => flux, () => blocked) { ErrorLog = _ => { } })
        {
            void Tick(double delta = .25) { seconds += delta; controller.Tick(); }
            void Settle(int ticks = 100) { for (int i = 0; i < ticks; i++) Tick(); }
            controller.Tick(); assert(display.Applied.Count == 0, "Disabled startup does not change color");
            settings.Enabled = true; Tick(); near(controller.CurrentStrength, 0, "Enable starts without an initial jump");
            int first = display.Applied.Count; Settle(24); near(controller.CurrentStrength, 60, "Fade-in reaches target");
            assert(display.Applied.Skip(first).SequenceEqual(display.Applied.Skip(first).OrderByDescending(k => k)), "Fade-in changes monotonically");
            settings.Enabled = false; Tick(); near(controller.CurrentStrength, 60, "Disable begins at displayed color");
            assert(controller.IsApplying && controller.IsFadingOut && controller.IsTransitioning, "Fade-out stays scheduled when Enabled is false");
            Tick(1); near(controller.CurrentStrength, 50, "Fade-out advances at same rate as fade-in");
            settings.Enabled = true; Tick(); near(controller.CurrentStrength, 50, "Re-enable during fade-out has no jump");
            Settle(); near(controller.CurrentStrength, 60, "Interrupted fade-out returns to desired warmth");
            native = new(4380, false, false); // Windows itself requests 40%, not neutral.
            settings.Enabled = false; Tick(); Settle();
            assert(!controller.IsApplying && !controller.IsTransitioning && display.Applied.Last() == 4380, "Fade-out returns to current Windows request");
            int writes = display.Applied.Count; Tick(12); assert(display.Applied.Count == writes, "Disabled idle no longer writes color");
            native = new(6500, false, false); settings.Enabled = true; Tick(); Settle();
            native = new(4000, true, false); writes = display.Applied.Count; Tick(); controller.TryRestore();
            assert(display.Applied.Count == writes && !controller.IsApplying, "Windows preview is untouched by tick and exit restoration");
            settings.Enabled = false; Tick(); assert(display.Applied.Count == writes, "Disabling while previewing Windows does not overwrite it");
            native = new(6500, false, false); settings.Enabled = true; Tick(); Settle();
            controller.Pause(TimeSpan.FromSeconds(2)); Tick(); assert(!controller.IsApplying && display.Applied.Last() == 6500, "Pause restores Windows");
            Tick(3); assert(controller.PauseUntil == null && controller.IsApplying, "Expired pause resumes automatically");
            Settle(); controller.SessionLocked = true; Tick(); writes = display.Applied.Count; Tick();
            assert(display.Applied.Count == writes && !controller.IsApplying, "Locked session does not reapply the curve");
            controller.SessionLocked = false; controller.Refresh(); Tick(); Settle();
            settings.ManualStrength = 70; display.FailNext = true; Tick();
            assert(controller.LastError != null && !controller.IsApplying && display.Applied.Last() == 6500, "Failed native write restores Windows and reports failure");
            writes = display.Applied.Count; Tick(); assert(display.Applied.Count == writes, "Native failures use backoff");
            Tick(6); assert(controller.LastError == null && controller.Available && controller.IsApplying, "Native error recovers without restart");
            Settle(); flux = true; Tick(4); assert(!controller.IsApplying && display.Applied.Last() == 6500, "f.lux conflict releases native color");
            flux = false; Tick(4); Settle(); blocked = "En pausa: prueba.exe"; Tick();
            assert(!controller.IsApplying && controller.Status.Contains("prueba.exe"), "Foreground exclusion restores color");
            blocked = null; Tick(); Settle(); controller.Suspended = true; Tick(); writes = display.Applied.Count;
            Tick(); assert(display.Applied.Count == writes, "Suspended state stays inactive");
        }

        var now = date; bool refuseWrite = true; int saves = 0;
        var pending = new PendingSave(() => { if (refuseWrite) throw new IOException("Injected save failure"); saves++; }, () => now);
        pending.MarkChanged(); assert(!pending.IsDue, "Save debounce waits for editing to finish");
        now = now.AddSeconds(1); assert(pending.IsDue, "Changed curve becomes due for automatic save");
        assert(!pending.TrySave() && pending.IsDirty && pending.LastError != null, "Save failure retains pending edits");
        assert(!pending.IsDue, "Save failure does not repeatedly show errors every frame");
        refuseWrite = false; assert(pending.TrySave() && !pending.IsDirty && saves == 1, "Explicit save retries successfully");

        string folder = Path.Combine(Path.GetTempPath(), "NightLightControl-regression-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        try
        {
            string path = Path.Combine(folder, "settings.json"); var config = new Settings(); config.Current.Points[0].Strength = 65.25; config.Save(path);
            var loaded = Settings.Load(path, out string? warning); near(loaded.Current.Points[0].Strength, 65.25, "Saving preserves precise curve intensity");
            assert(warning == null && loaded.RecoverAfterCrash, "Existing settings default to crash protection");
            File.WriteAllText(path, "invalid original configuration");
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                loaded = Settings.Load(path, out warning); bool failed = false;
                try { loaded.Save(path); } catch (IOException) { failed = true; }
                assert(warning != null && failed, "Locked invalid settings neither crash startup nor overwrite the original");
            }
            loaded.Save(path);
            assert(Directory.EnumerateFiles(folder, "*.invalid-*").Any(f => File.ReadAllText(f) == "invalid original configuration"), "Recovery saves original bytes before replacing corrupt settings");
            assert(Settings.Load(path, out warning).Profiles.Count == 1 && warning == null, "Recovery can save after the lock is released");

            var recoveryDisplay = new FakeDisplay();
            assert(CrashRecovery.RestoreRequestedColor(recoveryDisplay, () => new(4100, false, false)) && recoveryDisplay.Applied.Last() == 4100, "Crash recovery follows Windows' latest requested temperature");
            int writes = recoveryDisplay.Applied.Count;
            assert(!CrashRecovery.RestoreRequestedColor(recoveryDisplay, () => new(3900, true, false)) && recoveryDisplay.Applied.Count == writes, "Crash recovery respects Windows preview");
            int reads = 0;
            assert(!CrashRecovery.RestoreRequestedColor(recoveryDisplay, () => new(3900, ++reads == 2, false)), "Recovery rechecks preview after opening displays");
            ProcessRecoveryCase(folder, true, assert); ProcessRecoveryCase(folder, false, assert);
            RealWorkerDisarmedCase(assert);
        }
        finally { foreach (string file in Directory.EnumerateFiles(folder)) File.Delete(file); Directory.Delete(folder); }
    }
    private static void RealWorkerDisarmedCase(Action<bool, string> assert)
    {
        // Execute the production recovery entry point, disarmed: it must exit
        // without loading the native display engine or touching screen colors.
        string repository = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        string configuration = Directory.GetParent(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory))!.Name;
        string app = Path.Combine(repository, "bin", configuration, "net10.0-windows", "NightLightControl.exe");
        string test = Path.Combine(AppContext.BaseDirectory, "NightLightControl.Tests.exe");
        Directory.CreateDirectory(CrashRecovery.Folder);
        string state = Path.Combine(CrashRecovery.Folder, Guid.NewGuid().ToString("N") + ".json");
        var start = new ProcessStartInfo(test) { UseShellExecute = false, CreateNoWindow = true }; start.ArgumentList.Add("--wait-test-parent");
        using var parent = Process.Start(start)!; Process? worker = null;
        try
        {
            long ticks = parent.StartTime.ToUniversalTime().Ticks; CrashRecovery.WriteState(state, new(false, parent.Id, ticks));
            var launch = new ProcessStartInfo(app) { UseShellExecute = false, CreateNoWindow = true };
            foreach (string argument in new[] { "--recover-after-exit", parent.Id.ToString(), ticks.ToString(), state }) launch.ArgumentList.Add(argument);
            worker = Process.Start(launch)!; var deadline = Stopwatch.StartNew();
            while (!File.Exists(state + ".ready") && !worker.HasExited && deadline.ElapsedMilliseconds < 10000) Thread.Sleep(20);
            assert(File.Exists(state + ".ready"), "Production helper acknowledges that it is running");
            parent.Kill(); parent.WaitForExit();
            assert(worker.WaitForExit(10000) && worker.ExitCode == 0 && !File.Exists(state), "Production helper handles disarmed exit without native display access");
        }
        finally
        {
            if (!parent.HasExited) { parent.Kill(); parent.WaitForExit(); }
            if (worker != null) { if (!worker.HasExited) { worker.Kill(); worker.WaitForExit(); } worker.Dispose(); }
            if (File.Exists(state)) File.Delete(state); if (File.Exists(state + ".ready")) File.Delete(state + ".ready");
        }
    }
    private static void ProcessRecoveryCase(string folder, bool active, Action<bool, string> assert)
    {
        string executable = Path.Combine(AppContext.BaseDirectory, "NightLightControl.Tests.exe");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true }; start.ArgumentList.Add("--wait-test-parent");
        using var parent = Process.Start(start)!; Process? worker = null;
        try
        {
            long ticks = parent.StartTime.ToUniversalTime().Ticks;
            string state = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".json"), log = state + ".result";
            CrashRecovery.WriteState(state, new(active, parent.Id, ticks));
            var child = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
            foreach (string argument in new[] { "--recovery-test-worker", state, parent.Id.ToString(), ticks.ToString(), log }) child.ArgumentList.Add(argument);
            worker = Process.Start(child)!;
            parent.Kill(); parent.WaitForExit(); // Kill only the surrogate test parent, never the real program.
            bool finished = worker.WaitForExit(15000);
            assert(finished && worker.ExitCode == 0, "Independent recovery worker handles parent exit");
            assert(File.Exists(log) == active && !File.Exists(state), active ? "Forced parent termination invokes restoration" : "Disarmed normal exit avoids redundant restoration");
        }
        finally { if (!parent.HasExited) { parent.Kill(); parent.WaitForExit(); } if (worker != null) { if (!worker.HasExited) { worker.Kill(); worker.WaitForExit(); } worker.Dispose(); } }
    }
    private sealed class FakeDisplay : IDisplayEngine
    {
        public List<int> Applied { get; } = new();
        public bool FailNext { get; set; }
        public bool HasApplied { get; private set; }
        public string Description => "Simulated display";
        public string Backend => "Simulation";
        public void Refresh() { }
        public void Apply(int kelvin) { HasApplied = true; if (FailNext) { FailNext = false; throw new IOException("Injected display failure"); } Applied.Add(kelvin); }
        public void Dispose() { }
    }
}
