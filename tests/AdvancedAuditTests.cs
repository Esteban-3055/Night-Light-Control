using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using NightLightControl.Core;
using NightLightControl.Native;

internal static class AdvancedAuditTests
{
    public static void Run(Action<bool, string> assert, Action<double, double, string> near)
    {
        var random = new Random(14273);
        for (int sample = 0; sample < 20; sample++)
        {
            var minutes = Enumerable.Range(0, 1440).OrderBy(_ => random.Next()).Take(random.Next(2, 97)).OrderBy(x => x).ToArray();
            var points = minutes.Select(m => new CurvePoint(m, random.NextDouble() * 100)).ToList();
            var reverse = points.AsEnumerable().Reverse().ToList();
            for (int i = 0; i < points.Count; i++)
            {
                var a = points[i]; var b = points[(i + 1) % points.Count];
                int length = (b.Minute - a.Minute + 1440) % 1440;
                foreach (double fraction in new[] { 0, .25, .5, .75 })
                    near(Curve.Evaluate(reverse, a.Minute + length * fraction), a.Strength + (b.Strength - a.Strength) * fraction, "Optimized curve follows every linear segment and midnight wrap");
            }
            points[0].Strength = 100; near(Curve.Evaluate(points, points[0].Minute), 100, "Point edits are immediately visible without a stale cache");
        }
        bool ready = false; double time = 0; var fake = new Display(); var settings = new Settings { Enabled = true, Automatic = false, ManualStrength = 50 };
        using (var controller = new Controller(settings, fake, () => new(6500, false, false), () => time, () => DateTimeOffset.Now, () => false, () => null) { ErrorLog = _ => { }, EnsureProtection = () => ready })
        {
            for (int i = 0; i < 10; i++) { time += .25; controller.Tick(); }
            assert(controller.IsPreparingProtection && fake.Applied.Count == 0, "Native writes wait for asynchronous protection readiness");
            settings.Enabled = false; controller.Tick(); assert(!controller.IsPreparingProtection && !controller.IsApplying, "Disable cancels pending activation");
            settings.Enabled = true; ready = true; controller.Tick(); near(controller.CurrentStrength, 0, "Prepared activation starts without a color jump");
            time += 1; controller.Tick(); near(controller.CurrentStrength, 10, "Fade starts normally once protection is ready");
            fake.Failures = 100; controller.Refresh();
            for (int i = 0; i < 15; i++) { time += 1; controller.Tick(); }
            assert(controller.RecommendedTickMilliseconds == 1000 && settings.Enabled, "Long display outage backs off UI polling and keeps user intent");
            fake.Failures = 0; time += 1; controller.Tick(); assert(!controller.IsRefreshingDisplays && controller.LastError == null, "Long display outage recovers");
            fake.ApplyFailures = 100; controller.Refresh();
            for (int i = 0; i < 15; i++) { time += 1; controller.Tick(); }
            assert(controller.RecommendedTickMilliseconds == 1000 && settings.Enabled && controller.LastError == null, "Repeated native apply failures back off even when display enumeration succeeds");
            fake.ApplyFailures = 0; time += 1; controller.Tick(); assert(!controller.IsRefreshingDisplays && controller.LastError == null, "Native apply recovers after prolonged projection failure");
            controller.Refresh(); controller.EnsureProtection = () => throw new IOException("Injected guard failure"); time += .25; controller.Tick();
            assert(controller.LastError != null && !controller.IsApplying, "Guard errors are not misreported as display reconnection");
        }
        var values = new List<double>(); double clock = 0;
        var throttled = new TransferProgress(new InlineProgress(v => values.Add(v)), () => clock);
        for (int i = 0; i < 1000; i++) { clock = i / 1000.0; throttled.Report(i, 1000); }
        throttled.Report(1000, 1000); throttled.Report(1000, 1000);
        assert(values.Count <= 7 && values.Last() == 100 && values.Count(v => v == 100) == 1, "Download progress is throttled and completes once");

        string root = Path.Combine(Path.GetTempPath(), "NightLightControl-audit-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            string installed = Path.Combine(root, "installed"), docs = Path.Combine(installed, "docs"); Directory.CreateDirectory(docs);
            File.WriteAllText(Path.Combine(installed, "NightLightControl.exe"), "program");
            File.WriteAllText(Path.Combine(docs, "interface.png"), "owned"); File.WriteAllText(Path.Combine(docs, "my-notes.md"), "personal");
            File.WriteAllText(Path.Combine(installed, "installed-files.json"), JsonSerializer.Serialize(new[] { "NightLightControl.exe", "docs/interface.png" }));
            Installation.RemoveApplicationFiles(installed);
            assert(File.Exists(Path.Combine(docs, "my-notes.md")) && !File.Exists(Path.Combine(docs, "interface.png")), "Uninstall preserves foreign documents and deletes only owned files");
            File.WriteAllText(Path.Combine(installed, "NightLightControl.exe"), "program"); File.WriteAllText(Path.Combine(installed, "installed-files.json"), "[]");
            bool rejected = false; try { Installation.RemoveApplicationFiles(installed); } catch (InvalidDataException) { rejected = true; }
            assert(rejected && File.Exists(Path.Combine(installed, "NightLightControl.exe")), "Invalid manifest aborts before deleting application files");
            string sourceExe = Path.Combine(root, "source.exe"), cachedExe = Path.Combine(root, "copy.exe");
            File.WriteAllText(sourceExe, "good"); File.WriteAllText(cachedExe, "evil");
            assert(!CrashGuard.SameExecutable(sourceExe, cachedExe), "Same-size corrupted helper is detected");
            File.Copy(sourceExe, cachedExe, true); assert(CrashGuard.SameExecutable(sourceExe, cachedExe), "Identical cached helper is reused");
            using (var zip = Package(("NightLightControl.exe", "program"), ("docs/a.md", "doc")))
            using (var prepared = PreparedInstallation.Create(zip)) assert(File.ReadAllText(Path.Combine(prepared.Folder, "NightLightControl.exe")) == "program", "Full package is staged before installation writes");
            using (var zip = Package(("NightLightControl.exe", "program"), ("docs/a.md", "one"), ("docs/A.md", "two")))
            {
                rejected = false; try { using var p = PreparedInstallation.Create(zip); } catch (InvalidDataException) { rejected = true; }
                assert(rejected, "Installer rejects duplicate Windows paths");
            }
            using (var zip = Package(("NightLightControl.exe", "program"), ("docs/conflict", "file"), ("docs/conflict/child.md", "child")))
            {
                rejected = false; try { using var p = PreparedInstallation.Create(zip); } catch (IOException) { rejected = true; }
                assert(rejected, "Conflicting package structure is rejected in private staging");
            }
            TestDownloads(root, assert).GetAwaiter().GetResult();
        }
        finally
        {
            if (!Installation.SamePath(Path.GetDirectoryName(root)!, Path.GetTempPath()) || !Path.GetFileName(root).StartsWith("NightLightControl-audit-", StringComparison.Ordinal)) throw new IOException("Unexpected test directory");
            Directory.Delete(root, true);
        }
    }
    private static async Task TestDownloads(string root, Action<bool, string> assert)
    {
        byte[] body = new byte[250000]; new Random(7).NextBytes(body);
        var release = new AppRelease(new Version(9, 0, 0), "v9.0.0", "", Updates.RepositoryUrl + "/releases/download/v9.0.0/NightLightControl.exe", null, Convert.ToHexString(SHA256.HashData(body)), body.Length);
        using var client = new HttpClient(new Handler(body)); string staging = Path.Combine(root, "downloads"); Directory.CreateDirectory(staging);
        using (var cancel = new CancellationTokenSource())
        {
            bool cancelled = false;
            try { await Updates.DownloadAsync(release, new InlineProgress(_ => cancel.Cancel()), cancel.Token, client, staging); } catch (OperationCanceledException) { cancelled = true; }
            assert(cancelled && !Directory.EnumerateFileSystemEntries(staging).Any(), "Cancelled download cleans its partial file and staging folder");
        }
        using (var cancel = new CancellationTokenSource())
        {
            bool cancelled = false;
            try { await Updates.DownloadAsync(release, new InlineProgress(value => { if (value >= 100) cancel.Cancel(); }), cancel.Token, client, staging); } catch (OperationCanceledException) { cancelled = true; }
            assert(cancelled && !Directory.EnumerateFileSystemEntries(staging).Any(), "Cancellation at transfer completion cannot hand off an installation");
        }
        bool rejected = false;
        try { await Updates.DownloadAsync(release with { Sha256 = new string('a', 64) }, null, default, client, staging); } catch (InvalidDataException) { rejected = true; }
        assert(rejected && !Directory.EnumerateFileSystemEntries(staging).Any(), "Bad download hash cleans all temporary files");
        var downloaded = await Updates.DownloadAsync(release, null, default, client, staging);
        assert(File.ReadAllBytes(downloaded.FilePath).SequenceEqual(body), "Throttled download preserves exact payload and checksum");
    }
    private static MemoryStream Package(params (string Path, string Text)[] files)
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true)) foreach (var f in files) { using var writer = new StreamWriter(zip.CreateEntry(f.Path).Open()); writer.Write(f.Text); }
        stream.Position = 0; return stream;
    }
    private sealed class InlineProgress(Action<double> report) : IProgress<double> { public void Report(double value) => report(value); }
    private sealed class Handler(byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
    }
    private sealed class Display : IDisplayEngine
    {
        public bool HasApplied { get; private set; }
        public List<int> Applied { get; } = new(); public int Failures { get; set; } public int ApplyFailures { get; set; }
        public string Description => "Simulation"; public string Backend => "Simulation";
        public void Refresh() { if (Failures-- > 0) throw new IOException("Injected display outage"); }
        public void Apply(int kelvin) { if (ApplyFailures-- > 0) throw new IOException("Injected native apply failure"); HasApplied = true; Applied.Add(kelvin); }
        public void Dispose() { }
    }
}
