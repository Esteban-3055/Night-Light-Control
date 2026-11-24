using System.Text.Json;
using NightLightControl.Core;
using NightLightControl.Native;

int checks = 0;
void Assert(bool condition, string name) { checks++; if (!condition) throw new Exception(name); }
void Near(double actual, double expected, string name) => Assert(Math.Abs(actual - expected) < 0.00001, name);
void Reject(Action action, string name) { bool rejected = false; try { action(); } catch (Exception e) when (e is InvalidDataException or FormatException or ArgumentOutOfRangeException) { rejected = true; } Assert(rejected, name); }

var curve = new List<CurvePoint> { new(360, 0), new(1080, 60) };
Near(Curve.Evaluate(curve, 360), 0, "Exact first anchor");
Near(Curve.Evaluate(curve, 1080), 60, "Exact second anchor");
Near(Curve.Evaluate(curve, 720), 30, "Linear daytime midpoint");
Near(Curve.Evaluate(curve, 0), 30, "Midnight interpolation");
Near(Curve.Evaluate(curve, 1440), 30, "24:00 wrap");
Near(Curve.Evaluate(curve, -360), 60, "Negative minute wrap");
for (int minute = 0; minute < 1440; minute++)
{
    double actual = Curve.Evaluate(curve, minute);
    Assert(actual is >= 0 and <= 60, "Interpolation bounds");
    Near(Curve.Evaluate(curve, minute + 1440), actual, "Daily periodicity");
}
var defaults = Profile.Defaults();
Near(Curve.Evaluate(defaults, 420), 32.5, "Dawn fade to zero");
Near(Curve.Evaluate(defaults, 600), 0, "Neutral daytime");
Near(Curve.Evaluate(defaults, 1380), 65, "Night plateau");
Near(Curve.Evaluate(defaults.AsEnumerable().Reverse().ToList(), 420), 32.5, "Unsorted editing input");
Assert(Curve.Kelvin(0) == 6500 && Curve.Kelvin(100) == 1200, "Native temperature endpoints");
Reject(() => Curve.Validate(new() { new(10, 20), new(10, 30) }), "Reject duplicate times");
Reject(() => Curve.Validate(new() { new(0, double.NaN), new(500, 30) }), "Reject NaN");
Reject(() => Curve.Validate(new() { new(0, 40), new(1440, 30) }), "Reject duplicate day boundary");
Reject(() => Curve.Validate(new() { new(0, 40) }), "Reject single point");
Reject(() => Curve.Evaluate(defaults, double.PositiveInfinity), "Reject infinite time");
Assert(Curve.ParseTime("23:59") == 1439 && Curve.ParseTime("00:00") == 0, "Time input endpoints");
Reject(() => Curve.ParseTime("24:00"), "Reject 24:00 point");
Reject(() => Curve.ParseTime("xx:00"), "Reject malformed time");
var config = new Settings(); config.Validate();
Assert(!config.Enabled, "First launch inactive");
config.Profiles.Add(new Profile { Name = "Prueba", Points = new() { new(60, 10), new(1000, 90) } }); config.ActiveProfile = 1;
var roundTrip = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(config))!; roundTrip.Validate();
Assert(roundTrip.Current.Name == "Prueba" && roundTrip.Current.Points[1].Strength == 90, "Profile persistence");
roundTrip.Current.Points[0].Strength = 45;
Assert(config.Current.Points[0].Strength == 10, "Persistence isolates data");
roundTrip.ActiveProfile = 99; Reject(roundTrip.Validate, "Reject unknown active profile");
config.ExcludedApps.Add("C:\\game.exe"); Reject(config.Validate, "Reject paths in process exclusions");

byte[] saved = Convert.FromHexString("434201000A0201002A06B0FB93D8062A2B0E2143420100CA140E1500CA1E0E0700CF28C435CA320E132E1100CA3C0E062E260000000000");
byte[] off = Convert.FromHexString("434201000A0201002A2A000000");
var state = WindowsSettings.Decode(saved, off);
Assert(state.Kelvin == 6500 && !state.Preview, "Omitted Bond defaults represent inactive state");
for (int n = 0; n < saved.Length; n++) { int length = n; Reject(() => WindowsSettings.Decode(saved[..length], off), "Reject truncated CloudStore " + n); }
var bad = (byte[])saved.Clone(); bad[0] = 0; Reject(() => WindowsSettings.Decode(bad, off), "Reject unknown CloudStore format");
Assert(WindowsSettings.Decode(off, off).Kelvin == 6500, "Empty defaults for settings");
string sampleHash = new('a', 64);
object Release(string tag, string assetUrl, bool draft = false, string? hash = null) => new
{
    tag_name = tag, draft, prerelease = true,
    assets = new object[] { new { name = "NightLightControl.exe", browser_download_url = assetUrl, size = 100, digest = "sha256:" + (hash ?? sampleHash) } }
};
string official = Updates.RepositoryUrl + "/releases/download/v0.9.0/NightLightControl.exe";
Assert(Updates.IsOfficialAsset(official), "Updater accepts own repository");
Assert(!Updates.IsOfficialAsset("https://github.com.evil.test/Esteban-3055/Night-Light-Control./releases/download/v1/app.exe"), "Updater rejects hostname lookalike");
Assert(!Updates.IsOfficialAsset("https://github.com/Other/Other/releases/download/v1/app.exe"), "Updater rejects another repository");
Assert(!Updates.IsOfficialAsset(official.Replace("https:", "http:")), "Updater requires HTTPS");
var release = Updates.ParseReleases(JsonSerializer.Serialize(new[] { Release("v0.8.0", official), Release("v0.9.0", official), Release("v1.0.0", official, true) }));
Assert(release?.Tag == "v0.9.0" && release.Sha256 == sampleHash, "Updater selects newest non-draft version");
Assert(Updates.ParseReleases("[]") == null, "Updater handles no release");
Assert(Updates.ParseReleases(JsonSerializer.Serialize(new[] { Release("invalid", official) })) == null, "Updater skips unknown version format");
Assert(Updates.ParseReleases(JsonSerializer.Serialize(new[] { Release("v0.9.0", "https://example.org/file.exe") })) == null, "Updater refuses external release asset");
Reject(() => Updates.NormalizeHash("bad hash"), "Updater rejects malformed hash");
var temporary = Path.Combine(Path.GetTempPath(), "NightLightControl-update-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temporary);
try
{
    string originalFile = Path.Combine(temporary, "app.exe"), candidate = Path.Combine(temporary, "download.exe");
    File.WriteAllText(originalFile, "previous version"); File.WriteAllText(candidate, "next version");
    Reject(() => Updates.InstallFiles(candidate, originalFile, sampleHash), "Updater rejects corrupt file before installation");
    Assert(File.ReadAllText(originalFile) == "previous version" && !File.Exists(originalFile + ".new") && !File.Exists(originalFile + ".previous"), "Failed verification leaves installation intact");
    string expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(candidate)));
    Updates.InstallFiles(candidate, originalFile, expected);
    Assert(File.ReadAllText(originalFile) == "next version" && File.ReadAllText(originalFile + ".previous") == "previous version", "Atomic update retains previous executable");
    Assert(!File.Exists(originalFile + ".new"), "Successful update leaves no staging file");
}
finally
{
    foreach (string file in Directory.EnumerateFiles(temporary)) File.Delete(file);
    Directory.Delete(temporary);
}
Console.WriteLine($"PASS: {checks} checks; daily curve, midnight, persistence, CloudStore and verified update installation.");

if (args.Contains("--native"))
{
    Exception? failure = null;
    var thread = new Thread(() =>
    {
        try
        {
            byte[] originalSettings = WindowsSettings.ReadRecord(true), originalState = WindowsSettings.ReadRecord(false);
            var s = new Settings { Enabled = false, Automatic = false, ManualStrength = 5, TransitionSeconds = 1 };
            using (var c = new Controller(s))
            {
                c.Tick(); Assert(c.Available && c.LastError == null && c.Status == "Control desactivado", "Native idle probe");
                s.Enabled = true;
                for (int i = 0; i < 12; i++) { Thread.Sleep(50); c.Tick(); }
                Assert(c.LastError == null && c.Status == "Intensidad manual", "Native apply controller"); Near(c.CurrentStrength, 5, "Manual settles at target");
                c.Pause(TimeSpan.FromHours(1)); c.Tick(); Assert(c.Status.StartsWith("En pausa hasta"), "Pause releases control");
                c.Resume(); c.SessionLocked = true; c.Tick(); Assert(c.Status == "En pausa: sesión suspendida", "Locked session stays paused");
                c.SessionLocked = false; c.Refresh(); c.Tick(); Assert(c.LastError == null, "Refresh after session resume");
                s.ManualStrength = 0;
                for (int i = 0; i < 12; i++) { Thread.Sleep(50); c.Tick(); }
                Near(c.CurrentStrength, 0, "Zero strength returns to neutral");
                s.Enabled = false; c.Tick(); Assert(c.Status == "Control desactivado" && c.LastError == null, "Disable restores Windows");
            }
            Assert(originalSettings.SequenceEqual(WindowsSettings.ReadRecord(true)) && originalState.SequenceEqual(WindowsSettings.ReadRecord(false)), "Native lifecycle preserves both Windows records");
            Console.WriteLine("PASS: native controller lifecycle; 5% warmth, pause, resume, session guard, neutral, disable, restoration and unchanged Windows records.");
        }
        catch (Exception ex) { failure = ex; }
    });
    thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
    if (failure != null) throw failure;
}
