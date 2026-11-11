using System.Diagnostics;
using Coworker.Services.Updates;
using Xunit;

namespace Coworker.UpdateTests;

/// <summary>The updater against a local mock appcast (loopback HttpListener): feed parsing and filtering,
/// signature enforcement, download, and launching the installer.</summary>
public sealed class UpdateEngineTests : IDisposable
{
    private readonly MockFeedServer _server = new();
    private readonly TempDir _temp = new();
    private readonly KeyPair _keys = new();
    private readonly List<UpdateEngine> _engines = new();

    private UpdateEngine NewEngine(string currentBuild = "1", Func<Task>? quit = null, Func<bool>? canQuit = null,
        string? waitForProcess = null, string? publicKey = null, System.Runtime.InteropServices.Architecture? architecture = null)
    {
        var engine = new UpdateEngine(new UpdateEngineOptions
        {
            FeedUrl = _server.FeedUrl,
            PublicKey = publicKey ?? _keys.PublicKey,
            CurrentBuild = currentBuild,
            StateDirectory = _temp.File("state"),
            FeedTimeout = TimeSpan.FromSeconds(5),
            QuitApplication = quit,
            CanQuitNow = canQuit,
            ProcessIdToWaitFor = waitForProcess,
            Architecture = architecture,
        });
        _engines.Add(engine);
        return engine;
    }

    /// Writes a payload, serves it under /downloads and returns a signed feed item for it.
    private FeedItem ServeInstaller(string shortVersion, string build, byte[]? payload = null, bool sign = true,
        string? signatureOverride = null, bool critical = false, bool tag = false, string? os = "windows-x64")
    {
        payload ??= new byte[300_000];
        if (payload.Length > 0 && payload.All(b => b == 0)) Random.Shared.NextBytes(payload);
        var local = _temp.File($"payload-{build}.bin");
        File.WriteAllBytes(local, payload);
        var url = $"{_server.BaseUrl}/downloads/Coworker-{shortVersion}-win-x64.exe";
        _server.Serve($"/downloads/Coworker-{shortVersion}-win-x64.exe", payload);
        var signature = signatureOverride ?? (sign ? _keys.Sign(local) : null);
        return new FeedItem(shortVersion, build, url, signature, payload.Length, os, critical, tag);
    }

    private void ServeFeed(params FeedItem[] items) => _server.Serve("/appcast-windows.xml", Feed.Build(items));

    [Fact]
    public async Task Up_to_date_when_the_feed_has_nothing_newer()
    {
        ServeFeed(ServeInstaller("1.0.0", "1"));
        var result = await NewEngine(currentBuild: "1").CheckAsync(ignoreSkippedVersions: false);
        Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
        Assert.Null(result.Latest);
    }

    [Fact]
    public async Task Finds_the_newest_installable_update_with_its_notes()
    {
        ServeFeed(ServeInstaller("1.1.0", "6"), ServeInstaller("1.2.0", "7"), ServeInstaller("1.0.0", "1"));
        var result = await NewEngine(currentBuild: "1").CheckAsync(false);

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.Equal("1.2.0", result.Latest!.ShortVersion);
        Assert.Equal("7", result.Latest.Build);
        Assert.Contains("Fixed things", result.Latest.ReleaseNotesHtml);
        Assert.Equal(300_000, result.Latest.Length);
        Assert.EndsWith("Coworker-1.2.0-win-x64.exe", result.Latest.DownloadUrl);
        Assert.Equal(new[] { "1.2.0", "1.1.0" }, result.Updates.Select(u => u.ShortVersion));   // newest first, older builds dropped
        Assert.False(result.AnyCritical);
    }

    [Fact]
    public async Task Compares_build_numbers_numerically()
    {
        ServeFeed(ServeInstaller("1.0.10", "10"));
        var result = await NewEngine(currentBuild: "9").CheckAsync(false);
        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);   // "10" > "9", not string order
    }

    [Fact]
    public async Task Ignores_items_for_other_operating_systems()
    {
        ServeFeed(ServeInstaller("9.9", "99", os: "macos"), ServeInstaller("1.2.0", "7"));
        var result = await NewEngine().CheckAsync(false);
        Assert.Equal("1.2.0", result.Latest!.ShortVersion);
    }

    [Fact]
    public async Task Offers_only_builds_made_for_this_cpu()
    {
        ServeFeed(
            ServeInstaller("1.3.0", "8", os: "windows-arm64"),
            ServeInstaller("1.2.0", "7", os: "windows-x64"),
            ServeInstaller("1.1.0", "6", os: "windows"));   // names no CPU: for everyone

        var x64 = await NewEngine(architecture: System.Runtime.InteropServices.Architecture.X64).CheckAsync(false);
        Assert.Equal(new[] { "1.2.0", "1.1.0" }, x64.Updates.Select(u => u.ShortVersion));

        var arm64 = await NewEngine(architecture: System.Runtime.InteropServices.Architecture.Arm64).CheckAsync(false);
        Assert.Equal(new[] { "1.3.0", "1.1.0" }, arm64.Updates.Select(u => u.ShortVersion));
    }

    [Fact]
    public async Task Drops_items_that_carry_no_signature()
    {
        ServeFeed(ServeInstaller("2.0.0", "8", sign: false), ServeInstaller("1.2.0", "7"));
        var result = await NewEngine().CheckAsync(false);
        Assert.Equal("1.2.0", result.Latest!.ShortVersion);

        ServeFeed(ServeInstaller("2.0.0", "8", sign: false));
        Assert.Equal(UpdateCheckStatus.UpToDate, (await NewEngine().CheckAsync(false)).Status);
    }

    [Fact]
    public async Task Detects_critical_updates_by_element_and_by_tag()
    {
        ServeFeed(ServeInstaller("1.2.0", "7", critical: true));
        Assert.True((await NewEngine().CheckAsync(false)).Latest!.IsCritical);

        ServeFeed(ServeInstaller("1.2.0", "7", tag: true));
        var tagged = await NewEngine().CheckAsync(false);
        Assert.True(tagged.Latest!.IsCritical);
        Assert.True(tagged.AnyCritical);
    }

    [Fact]
    public async Task A_critical_release_the_user_already_has_does_not_force_an_update()
    {
        // build 3 was critical, the user runs build 5, build 6 is an ordinary update
        ServeFeed(ServeInstaller("1.3.0", "6"), ServeInstaller("1.1.0", "3", critical: true));
        var result = await NewEngine(currentBuild: "5").CheckAsync(false);
        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.False(result.AnyCritical);
    }

    [Fact]
    public async Task A_critical_release_that_was_skipped_over_still_counts()
    {
        // the user is on build 1 and would jump over critical build 3 to build 6
        ServeFeed(ServeInstaller("1.3.0", "6"), ServeInstaller("1.1.0", "3", critical: true));
        Assert.True((await NewEngine(currentBuild: "1").CheckAsync(false)).AnyCritical);
    }

    [Fact]
    public async Task Skip_this_version_silences_background_checks_but_not_manual_ones()
    {
        ServeFeed(ServeInstaller("1.2.0", "7"));
        var engine = NewEngine();
        var first = await engine.CheckAsync(false);
        engine.SkipVersion(first.Latest!);

        var background = await engine.CheckAsync(ignoreSkippedVersions: false);
        Assert.Equal(UpdateCheckStatus.Skipped, background.Status);
        Assert.Equal("1.2.0", background.Latest!.ShortVersion);

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, (await engine.CheckAsync(ignoreSkippedVersions: true)).Status);

        // and a newer release is offered again
        ServeFeed(ServeInstaller("1.3.0", "8"), ServeInstaller("1.2.0", "7"));
        Assert.Equal(UpdateCheckStatus.UpdateAvailable, (await engine.CheckAsync(false)).Status);
    }

    [Fact]
    public async Task Reports_failure_for_a_missing_or_broken_feed()
    {
        var missing = await NewEngine().CheckAsync(false);            // nothing served: 404
        Assert.Equal(UpdateCheckStatus.Failed, missing.Status);

        _server.Serve("/appcast-windows.xml", "this is <not xml");
        Assert.Equal(UpdateCheckStatus.Failed, (await NewEngine().CheckAsync(false)).Status);

        _server.Serve("/appcast-windows.xml", "server error", status: 500);
        Assert.Equal(UpdateCheckStatus.Failed, (await NewEngine().CheckAsync(false)).Status);
    }

    [Fact]
    public async Task The_feed_shipped_in_the_website_repo_reads_as_up_to_date_while_it_has_no_releases()
    {
        // Humanlike-next/public/coworker/appcast-windows.xml, exactly as it is checked in.
        string? feedFile = null;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null && feedFile == null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "Humanlike-next", "public", "coworker", "appcast-windows.xml");
            if (File.Exists(candidate)) feedFile = candidate;
        }
        if (feedFile == null) return;   // the website repo is not next to this checkout

        var text = File.ReadAllText(feedFile);
        _server.Serve("/appcast-windows.xml", text);
        var result = await NewEngine().CheckAsync(false);
        if (text.Contains("<item>"))
            Assert.NotEqual(UpdateCheckStatus.Failed, result.Status);   // releases have been published since
        else
            Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
    }

    [Fact]
    public async Task Persists_the_time_of_the_last_successful_check()
    {
        ServeFeed(ServeInstaller("1.0.0", "1"));
        var engine = NewEngine();
        Assert.Null(engine.LastCheckTime);
        await engine.CheckAsync(false);
        Assert.NotNull(engine.LastCheckTime);
        Assert.True((DateTimeOffset.UtcNow - engine.LastCheckTime!.Value).TotalMinutes < 1);
    }

    [Fact]
    public async Task Downloads_and_verifies_the_installer()
    {
        var payload = new byte[2_500_000];
        Random.Shared.NextBytes(payload);
        ServeFeed(ServeInstaller("1.2.0", "7", payload));
        var engine = NewEngine();
        var update = (await engine.CheckAsync(false)).Latest!;

        var reports = new List<DownloadProgress>();
        var path = await engine.DownloadAsync(update, new Progress<DownloadProgress>(reports.Add));

        Assert.Equal(payload, await File.ReadAllBytesAsync(path));
        Assert.Equal("Coworker-1.2.0-7.exe", Path.GetFileName(path));
        Assert.StartsWith(engine.DownloadDirectory, path);
        await Task.Delay(100);   // Progress<T> posts to the thread pool
        Assert.NotEmpty(reports);
    }

    [Fact]
    public async Task Rejects_an_installer_whose_signature_does_not_match()
    {
        // signed with a different key than the one the app trusts
        var attacker = new KeyPair();
        var payload = new byte[400_000];
        Random.Shared.NextBytes(payload);
        var local = _temp.File("evil.bin");
        File.WriteAllBytes(local, payload);
        _server.Serve("/downloads/Coworker-1.2.0-win-x64.exe", payload);
        ServeFeed(new FeedItem("1.2.0", "7", $"{_server.BaseUrl}/downloads/Coworker-1.2.0-win-x64.exe", attacker.Sign(local), payload.Length));

        var engine = NewEngine();
        var update = (await engine.CheckAsync(false)).Latest!;
        await Assert.ThrowsAnyAsync<Exception>(() => engine.DownloadAsync(update));
        Assert.Empty(Directory.GetFiles(engine.DownloadDirectory));   // nothing suspicious left on disk
    }

    [Fact]
    public async Task Rejects_a_download_that_was_tampered_with_after_signing()
    {
        var payload = new byte[400_000];
        Random.Shared.NextBytes(payload);
        var item = ServeInstaller("1.2.0", "7", payload);
        var tampered = (byte[])payload.Clone();
        tampered[1234] ^= 0xFF;
        _server.Serve("/downloads/Coworker-1.2.0-win-x64.exe", tampered);
        ServeFeed(item);

        var engine = NewEngine();
        var update = (await engine.CheckAsync(false)).Latest!;
        await Assert.ThrowsAnyAsync<Exception>(() => engine.DownloadAsync(update));
        Assert.Empty(Directory.GetFiles(engine.DownloadDirectory));
    }

    [Fact]
    public async Task Rejects_everything_when_the_app_holds_a_different_public_key()
    {
        ServeFeed(ServeInstaller("1.2.0", "7"));
        var engine = NewEngine(publicKey: new KeyPair().PublicKey);
        var update = (await engine.CheckAsync(false)).Latest!;
        await Assert.ThrowsAnyAsync<Exception>(() => engine.DownloadAsync(update));
    }

    [Fact]
    public async Task Reuses_an_installer_that_is_already_downloaded_and_valid()
    {
        ServeFeed(ServeInstaller("1.2.0", "7"));
        var engine = NewEngine();
        var update = (await engine.CheckAsync(false)).Latest!;
        var first = await engine.DownloadAsync(update);
        var hitsAfterFirst = _server.Hits("/downloads/Coworker-1.2.0-win-x64.exe");

        var second = await engine.DownloadAsync(update);
        Assert.Equal(first, second);
        Assert.Equal(hitsAfterFirst, _server.Hits("/downloads/Coworker-1.2.0-win-x64.exe"));
    }

    [Fact]
    public async Task Can_cancel_a_download()
    {
        var payload = new byte[1_000_000];
        Random.Shared.NextBytes(payload);
        ServeFeed(ServeInstaller("1.2.0", "7", payload));
        var engine = NewEngine();
        var update = (await engine.CheckAsync(false)).Latest!;

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.DownloadAsync(update, null, cts.Token));
    }

    [Fact]
    public async Task Cleans_up_old_downloads_but_keeps_the_requested_one()
    {
        ServeFeed(ServeInstaller("1.2.0", "7"));
        var engine = NewEngine();
        var path = await engine.DownloadAsync((await engine.CheckAsync(false)).Latest!);
        var stale = Path.Combine(engine.DownloadDirectory, "Coworker-0.9.0-1.exe");
        await File.WriteAllTextAsync(stale, "old");

        engine.CleanUpDownloads(keepPath: path);
        Assert.True(File.Exists(path));
        Assert.False(File.Exists(stale));
        engine.CleanUpDownloads();
        Assert.False(File.Exists(path));
    }

    // ---- installer launch (a fake installer stands in for the Inno Setup exe) ----

    /// Serves the fake installer as the update payload; returns its feed item.
    private FeedItem ServeFakeInstaller()
    {
        var fakeDir = Path.Combine(AppContext.BaseDirectory, "fake-installer");
        var exe = Path.Combine(fakeDir, "FakeInstaller.exe");
        Assert.True(File.Exists(exe), $"FakeInstaller.exe missing from {fakeDir}; build the test project, not just the sources");
        var bytes = File.ReadAllBytes(exe);
        _server.Serve("/downloads/Coworker-1.2.0-win-x64.exe", bytes);
        return new FeedItem("1.2.0", "7", $"{_server.BaseUrl}/downloads/Coworker-1.2.0-win-x64.exe", _keys.Sign(exe), bytes.Length);
    }

    /// The apphost needs its .dll and runtimeconfig next to it; copy them beside the downloaded file.
    private static void PlaceFakeInstallerSupportFiles(string downloadedExe)
    {
        var fakeDir = Path.Combine(AppContext.BaseDirectory, "fake-installer");
        var target = Path.GetDirectoryName(downloadedExe)!;
        File.Copy(Path.Combine(fakeDir, "FakeInstaller.dll"), Path.Combine(target, "FakeInstaller.dll"), overwrite: true);
        File.Copy(Path.Combine(fakeDir, "FakeInstaller.runtimeconfig.json"), Path.Combine(target, "FakeInstaller.runtimeconfig.json"), overwrite: true);
    }

    private static async Task<string?> WaitForFile(string path, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < timeout)
        {
            if (File.Exists(path))
            {
                await Task.Delay(200);
                try { return await File.ReadAllTextAsync(path); } catch (IOException) { }
            }
            await Task.Delay(100);
        }
        return null;
    }

    [Fact]
    public async Task Launches_the_installer_silently_after_the_app_has_exited()
    {
        ServeFeed(ServeFakeInstaller());
        var marker = _temp.File("installer-ran.txt");
        Environment.SetEnvironmentVariable("FAKE_INSTALLER_MARKER", marker);
        try
        {
            // The launcher script waits for this process (a stand-in for the app) to end first.
            using var app = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 4 127.0.0.1 > nul") { CreateNoWindow = true, UseShellExecute = false })!;
            var quitRequested = new TaskCompletionSource();
            var engine = NewEngine(quit: () => { quitRequested.TrySetResult(); return Task.CompletedTask; },
                waitForProcess: app.Id.ToString());

            var update = (await engine.CheckAsync(false)).Latest!;
            var path = await engine.DownloadAsync(update);
            PlaceFakeInstallerSupportFiles(path);

            var launched = Stopwatch.StartNew();
            var outcome = await engine.InstallAsync(update, path);
            Assert.Equal(InstallOutcome.Started, outcome);
            await quitRequested.Task.WaitAsync(TimeSpan.FromSeconds(10));   // the app was asked to quit

            var recorded = await WaitForFile(marker, TimeSpan.FromSeconds(30));
            Assert.NotNull(recorded);
            var arguments = recorded!.Split('\n')[0];
            Assert.Contains("/VERYSILENT", arguments);
            Assert.Contains("/SUPPRESSMSGBOXES", arguments);
            Assert.Contains("/NORESTART", arguments);
            Assert.Contains("/CLOSEAPPLICATIONS", arguments);
            Assert.True(app.HasExited, "the installer started before the app exited");
            Assert.True(launched.Elapsed > TimeSpan.FromSeconds(1.5), $"installer ran after only {launched.Elapsed}; it must wait for the app");
        }
        finally
        {
            Environment.SetEnvironmentVariable("FAKE_INSTALLER_MARKER", null);
        }
    }

    [Fact]
    public async Task Defers_the_install_while_the_app_is_busy()
    {
        ServeFeed(ServeFakeInstaller());
        var marker = _temp.File("installer-ran.txt");
        Environment.SetEnvironmentVariable("FAKE_INSTALLER_MARKER", marker);
        try
        {
            var quit = false;
            var engine = NewEngine(quit: () => { quit = true; return Task.CompletedTask; }, canQuit: () => false);
            var update = (await engine.CheckAsync(false)).Latest!;
            var path = await engine.DownloadAsync(update);
            PlaceFakeInstallerSupportFiles(path);

            Assert.Equal(InstallOutcome.Deferred, await engine.InstallAsync(update, path));
            await Task.Delay(1500);
            Assert.False(quit);
            Assert.False(File.Exists(marker));
        }
        finally
        {
            Environment.SetEnvironmentVariable("FAKE_INSTALLER_MARKER", null);
        }
    }

    [Fact]
    public async Task Refuses_to_run_an_installer_that_changed_on_disk()
    {
        ServeFeed(ServeFakeInstaller());
        var marker = _temp.File("installer-ran.txt");
        Environment.SetEnvironmentVariable("FAKE_INSTALLER_MARKER", marker);
        try
        {
            var quit = false;
            var engine = NewEngine(quit: () => { quit = true; return Task.CompletedTask; });
            var update = (await engine.CheckAsync(false)).Latest!;
            var path = await engine.DownloadAsync(update);
            PlaceFakeInstallerSupportFiles(path);
            await using (var stream = new FileStream(path, FileMode.Append)) stream.WriteByte(0x42);   // tamper

            Assert.Equal(InstallOutcome.InvalidSignature, await engine.InstallAsync(update, path));
            await Task.Delay(1500);
            Assert.False(quit);
            Assert.False(File.Exists(marker));
        }
        finally
        {
            Environment.SetEnvironmentVariable("FAKE_INSTALLER_MARKER", null);
        }
    }

    public void Dispose()
    {
        foreach (var engine in _engines) engine.Dispose();
        _server.Dispose();
        _temp.Dispose();
    }
}
