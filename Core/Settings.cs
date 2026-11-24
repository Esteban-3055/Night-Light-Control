using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace NightLightControl.Core;

public sealed class CurvePoint
{
    public int Minute { get; set; }
    public double Strength { get; set; }
    public CurvePoint() { }
    public CurvePoint(int minute, double strength) { Minute = minute; Strength = strength; }
    public CurvePoint Copy() => new(Minute, Strength);
}
public sealed class Profile
{
    public string Name { get; set; } = "Mi curva";
    public List<CurvePoint> Points { get; set; } = Defaults();
    public static List<CurvePoint> Defaults() => new() { new(0, 65), new(360, 65), new(480, 0), new(1020, 0), new(1140, 30), new(1320, 65) };
}
public sealed class Settings
{
    public int Schema { get; set; } = 1;
    public bool Enabled { get; set; }
    public bool Automatic { get; set; } = true;
    public double ManualStrength { get; set; } = 40;
    public int TransitionSeconds { get; set; } = 10;
    public int ActiveProfile { get; set; }
    public List<Profile> Profiles { get; set; } = new() { new() };
    public List<string> ExcludedApps { get; set; } = new();
    public bool PauseFullscreen { get; set; }
    public bool CheckUpdatesAtStartup { get; set; } = true;
    public bool RecoverAfterCrash { get; set; } = true;
    private bool recoveryNeedsBackup;
    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NightLightControl");
    public static string FilePath => Path.Combine(Folder, "settings.json");
    [JsonIgnore] public Profile Current => Profiles[ActiveProfile];

    public void Validate()
    {
        if (Schema != 1 || Profiles is null || Profiles.Count is < 1 or > 50 || ActiveProfile < 0 || ActiveProfile >= Profiles.Count || !double.IsFinite(ManualStrength) || ManualStrength is < 0 or > 100 || TransitionSeconds is < 1 or > 120 || ExcludedApps is null) throw new InvalidDataException("La configuración no es válida.");
        foreach (var profile in Profiles)
        {
            if (profile == null || string.IsNullOrWhiteSpace(profile.Name) || profile.Name.Length > 80) throw new InvalidDataException("Nombre de perfil no válido.");
            Curve.Validate(profile.Points);
        }
        if (ExcludedApps.Any(s => string.IsNullOrWhiteSpace(s) || s.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)) throw new InvalidDataException("Indica los nombres de aplicaciones separados por comas.");
    }
    public static Settings Load(out string? warning)
        => Load(FilePath, out warning);
    public static Settings Load(string path, out string? warning)
    {
        warning = null;
        if (!File.Exists(path)) return new();
        try
        {
            var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path)) ?? throw new InvalidDataException("Configuración vacía.");
            settings.Validate(); return settings;
        }
        catch (Exception ex)
        {
            var fallback = new Settings { recoveryNeedsBackup = true };
            try { string backup = BackupInvalid(path); fallback.recoveryNeedsBackup = false; warning = "Se conservó una copia de la configuración inválida: " + backup + "\n" + ex.Message; }
            catch (Exception backupError) { warning = "No se pudo leer ni respaldar la configuración. Se conservará el archivo original hasta poder respaldarlo.\n" + backupError.Message; }
            return fallback;
        }
    }
    private static string BackupInvalid(string path)
    {
        string backup = path + ".invalid-" + Guid.NewGuid().ToString("N"); File.Copy(path, backup, false); return backup;
    }
    public void Save() => Save(FilePath);
    public void Save(string path)
    {
        Validate(); path = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (recoveryNeedsBackup && File.Exists(path)) { BackupInvalid(path); recoveryNeedsBackup = false; }
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static bool StartWithWindows
    {
        get { using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"); return key?.GetValue("NightLightControl") is string; }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (value) key.SetValue("NightLightControl", "\"" + Environment.ProcessPath + "\" --tray"); else key.DeleteValue("NightLightControl", false);
        }
    }
}
public static class Curve
{
    public static void Validate(List<CurvePoint>? points)
    {
        if (points is null || points.Count is < 2 or > 96 || points.Any(p => p == null || p.Minute is < 0 or >= 1440 || !double.IsFinite(p.Strength) || p.Strength is < 0 or > 100) || points.Select(p => p.Minute).Distinct().Count() != points.Count) throw new InvalidDataException("Usa de 2 a 96 puntos, con horas distintas e intensidades entre 0 y 100 %.");
    }
    public static double Evaluate(List<CurvePoint> points, double minute)
    {
        Validate(points);
        if (!double.IsFinite(minute)) throw new ArgumentOutOfRangeException(nameof(minute));
        minute = (minute % 1440 + 1440) % 1440;
        var sorted = points.OrderBy(p => p.Minute).ToArray();
        int next = Array.FindIndex(sorted, p => p.Minute > minute);
        if (next < 0) next = 0;
        var before = sorted[(next + sorted.Length - 1) % sorted.Length]; var after = sorted[next];
        double elapsed = (minute - before.Minute + 1440) % 1440;
        double duration = (after.Minute - before.Minute + 1440) % 1440;
        return before.Strength + (after.Strength - before.Strength) * elapsed / duration;
    }
    public static int Kelvin(double strength) => (int)Math.Round(6500 - 53 * Math.Clamp(strength, 0, 100));
    public static string Time(int minute) => $"{minute / 60:00}:{minute % 60:00}";
    public static int ParseTime(string value) => TimeSpan.TryParseExact(value, @"hh\:mm", CultureInfo.InvariantCulture, out var time) && time.TotalMinutes < 1440 ? (int)time.TotalMinutes : throw new FormatException("Usa una hora entre 00:00 y 23:59.");
}
