using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FlaUI.Core.Capturing;
using NAudio.CoreAudioApi;

namespace Coworker.E2E;

/// <summary>
/// End-to-end dictation on the real Coworker.exe, as a user does it:
///   1. the app starts signed in (settings written the way a signed-in install has them) against a fake server;
///   2. Notepad gets some text typed into it, with real key presses;
///   3. the test holds Ctrl+Shift, plays a recorded voice into the virtual microphone, and lets go;
///   4. the app records, streams the audio to the server, gets the transcript back and pastes it into Notepad;
///   5. the test reads Notepad's text through UI Automation and checks it, and checks the audio the server got.
/// Usage: CoworkerE2E --app <Coworker.exe> [--out <folder>] [--models <models.baseline.json>]
/// </summary>
internal static class Program
{
    private sealed record Case(string Name, string Before, string Clip, string Transcript, string Expected);

    // Each transcript is what the clip says, as the server would write it. The expected text is what the
    // user must see in Notepad after the app pasted it (sentence rules: Paste\DictationTextRules.cs).
    private static readonly Case[] Cases =
    {
        new("Empty field", "", "office-at-noon.wav", "Office at noon.", "Office at noon."),
        new("Middle of a sentence", "Meet me at the ", "office-at-noon.wav", "Office at noon.", "Meet me at the office at noon."),
        new("After a sentence end", "Done.", "next-step.wav", "Next step.", "Done. Next step."),
    };

    private sealed record Result(string Name, bool Passed, string Expected, string Got, string Transport, double AudioSeconds,
        double ClipSeconds, double Loudness, string Notes);

    public static async Task<int> Main(string[] args)
    {
        var app = Arg(args, "--app") ?? throw new ArgumentException("--app <path to Coworker.exe> is required");
        var outDir = Path.GetFullPath(Arg(args, "--out") ?? "e2e-output");
        var modelsPath = Arg(args, "--models");
        Directory.CreateDirectory(outDir);
        var clips = Path.Combine(AppContext.BaseDirectory, "clips");

        Console.WriteLine($"Coworker end-to-end test\n  app: {app}\n  output: {outDir}");
        DescribeAudio();

        var models = modelsPath != null && File.Exists(modelsPath) ? File.ReadAllText(modelsPath) : "{\"transcription\":[]}";
        using var server = new FakeServer(18080, models);
        server.Start();
        Console.WriteLine($"Fake server on {server.BaseUrl}");

        var dataDir = Path.Combine(outDir, "appdata");
        WriteSignedInSettings(dataDir);

        using var recorder = ScreenRecorder.Start(Path.Combine(outDir, "screen.mp4"));
        var results = new List<Result>();
        Process? coworker = null;
        var exitCode = 1;
        try
        {
            coworker = LaunchCoworker(app, server.BaseUrl, dataDir);
            var ready = await Task.WhenAny(server.Connected, Task.Delay(TimeSpan.FromSeconds(60)));
            if (ready != server.Connected)
                throw new InvalidOperationException("Coworker did not connect to the server within 60 s (not signed in, or it did not start)");
            Console.WriteLine("Coworker is running, signed in and connected");
            await Task.Delay(TimeSpan.FromSeconds(3));   // let the dictionary and model list load

            using var notepad = NotepadTarget.Launch();
            foreach (var testCase in Cases)
            {
                try
                {
                    results.Add(RunCase(testCase, notepad, server, clips, outDir));
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"   FAIL {testCase.Name}: {ex.Message}");
                    Screenshot(Path.Combine(outDir, "failure.png"));
                    results.Add(new Result(testCase.Name, false, testCase.Expected, ex.Message, "", 0, 0, 0, "the case stopped with an error"));
                }
            }
            exitCode = results.All(r => r.Passed) ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAILED: {ex.Message}");
            Screenshot(Path.Combine(outDir, "failure.png"));
            results.Add(new Result("Setup", false, "", ex.Message, "", 0, 0, 0, ""));
        }
        finally
        {
            if (coworker is { HasExited: false })
            {
                try { coworker.Kill(entireProcessTree: true); } catch (Exception) { }
            }
            CopyIfExists(Path.Combine(dataDir, "debug.log"), Path.Combine(outDir, "coworker-debug.log"));
            File.WriteAllLines(Path.Combine(outDir, "server.log"), server.Log);
            File.WriteAllText(Path.Combine(outDir, "results.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
            WriteSummary(results);
        }
        return exitCode;
    }

    private static Result RunCase(Case testCase, NotepadTarget notepad, FakeServer server, string clips, string outDir)
    {
        Console.WriteLine($"\n== {testCase.Name}: \"{testCase.Before}\" + voice \"{testCase.Clip}\"");
        var slug = string.Concat(testCase.Name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-'));
        server.NextTranscript = testCase.Transcript;
        notepad.Reset(testCase.Before);
        var takesBefore = server.Takes.Count;
        Thread.Sleep(400);

        TimeSpan clipLength;
        Keyboard.HoldDictationShortcut();
        try
        {
            Thread.Sleep(600);                                // the app starts recording once the hold is seen
            clipLength = Speaker.Play(Path.Combine(clips, testCase.Clip));
            Thread.Sleep(700);                                // the last word reaches the app before the release
        }
        finally
        {
            Keyboard.ReleaseDictationShortcut();              // never leave Ctrl+Shift held down
        }

        var got = notepad.WaitForChange(testCase.Before, TimeSpan.FromSeconds(30));
        Screenshot(Path.Combine(outDir, $"{slug}.png"));

        var take = server.Takes.Skip(takesBefore).LastOrDefault();
        var notes = new List<string>();
        double seconds = 0, loudness = 0;
        if (take == null)
        {
            notes.Add("the app sent no recording");
        }
        else
        {
            var audio = take.Audio.ToArray();
            if (take.Transport == "websocket")
            {
                seconds = audio.Length / 32_000.0;   // 16 kHz, 16-bit mono
                loudness = Speaker.Rms(audio);
                Speaker.SavePcm16k(audio, Path.Combine(outDir, $"{slug}-recorded.wav"));
                if (seconds < clipLength.TotalSeconds * 0.8) notes.Add($"recording too short ({seconds:0.00} s for a {clipLength.TotalSeconds:0.00} s clip)");
                if (loudness < 0.005) notes.Add($"recording is silent (loudness {loudness:0.0000})");
            }
            else
            {
                File.WriteAllBytes(Path.Combine(outDir, $"{slug}-upload.bin"), audio);
                notes.Add($"sent over the HTTP fallback ({audio.Length} bytes), not the live socket");
            }
            if (take.Start is { } start)
            {
                if (start["text_rules"]?.GetValue<bool>() != true) notes.Add("start message without text_rules");
                if (string.IsNullOrWhiteSpace(start["active_app"]?.GetValue<string>())) notes.Add("start message without active_app");
                if (start.ContainsKey("prompt")) notes.Add("start message still sends a prompt");
                // The focused field (role, label, multiline; never its text) goes with the start, or with the stop
                // when UI Automation answered late (Transcription/TakeField.cs).
                if ((start["field"] ?? take.Stop?["field"]) is not JsonObject field || field["role"] == null)
                    notes.Add("no field (role, label) in the start or stop message");
            }
        }

        // The text in Notepad and a real, long enough, non-silent recording decide the result; notes about the
        // start message are reported but don't fail the case.
        var passed = got == testCase.Expected && take != null
            && !notes.Any(n => n.StartsWith("recording", StringComparison.Ordinal) || n.StartsWith("the app sent", StringComparison.Ordinal));
        if (got != testCase.Expected) notes.Insert(0, "pasted text differs");
        var result = new Result(testCase.Name, passed, testCase.Expected, got, take?.Transport ?? "none", Math.Round(seconds, 2),
            Math.Round(clipLength.TotalSeconds, 2), Math.Round(loudness, 4), string.Join("; ", notes));
        Console.WriteLine($"   expected: \"{testCase.Expected}\"\n   got:      \"{got}\"\n   {(passed ? "PASS" : "FAIL")} {result.Notes}");
        return result;
    }

    private static Process LaunchCoworker(string app, string baseUrl, string dataDir)
    {
        var start = new ProcessStartInfo(app, "--background")
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(app)!,
        };
        start.Environment["COWORKER_API_BASE"] = baseUrl;
        start.Environment["COWORKER_WEB_BASE"] = baseUrl;
        start.Environment["COWORKER_DATA_DIR"] = dataDir;
        start.Environment["COWORKER_E2E"] = "1";
        var process = Process.Start(start) ?? throw new InvalidOperationException("Coworker.exe did not start");
        Console.WriteLine($"Started Coworker (pid {process.Id})");
        return process;
    }

    /// The settings a signed-in install has. The app moves the token out of settings.json into the
    /// credential store on first read (SettingsStore.TryRead), as it does for an install from before that store.
    private static void WriteSignedInSettings(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        var settings = new JsonObject
        {
            ["accessToken"] = FakeJwt(),
            ["userEmail"] = FakeServer.Email,
            ["connectedUserId"] = FakeServer.UserId,
            ["isConnected"] = true,
            ["deviceId"] = FakeServer.DeviceId,
            ["hasCompletedOnboarding"] = true,
            ["dictationShortcut"] = "Ctrl+Shift",
            ["spaceAfterSentenceEnd"] = true,
        };
        File.WriteAllText(Path.Combine(dataDir, "settings.json"), settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(dataDir, "debug.flag"), "end-to-end test");   // turns on debug.log
    }

    private static string FakeJwt()
    {
        static string B64(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var exp = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds();
        return $"{B64("{\"alg\":\"none\",\"typ\":\"JWT\"}")}.{B64($"{{\"sub\":\"{FakeServer.UserId}\",\"email\":\"{FakeServer.Email}\",\"device_id\":\"{FakeServer.DeviceId}\",\"exp\":{exp}}}")}.e2e";
    }

    private static void DescribeAudio()
    {
        Console.WriteLine($"  playback devices: {string.Join(" | ", Speaker.PlaybackDevices())}");
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var mics = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).Select(d => d.FriendlyName).ToList();
            var current = enumerator.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Console)
                ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console).FriendlyName
                : "none";
            Console.WriteLine($"  microphones: {string.Join(" | ", mics)} (default: {current})");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  microphones: could not list ({ex.Message})");
        }
    }

    private static void Screenshot(string path)
    {
        try { Capture.Screen().ToFile(path); }
        catch (Exception ex) { Console.WriteLine($"  screenshot failed: {ex.Message}"); }
    }

    private static void CopyIfExists(string from, string to)
    {
        if (File.Exists(from)) File.Copy(from, to, overwrite: true);
    }

    private static void WriteSummary(IReadOnlyList<Result> results)
    {
        var md = new StringBuilder("### Coworker for Windows, end to end\n\n| Case | Result | Expected in Notepad | Got | Recording | Notes |\n|---|---|---|---|---|---|\n");
        foreach (var r in results)
        {
            var recording = r.Transport == "websocket" ? $"{r.AudioSeconds} s of {r.ClipSeconds} s, loudness {r.Loudness}" : r.Transport;
            md.Append($"| {r.Name} | {(r.Passed ? "✅" : "❌")} | `{r.Expected}` | `{r.Got}` | {recording} | {r.Notes} |\n");
        }
        Console.WriteLine("\n" + md);
        var summary = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
        if (!string.IsNullOrEmpty(summary)) File.AppendAllText(summary, md.ToString());
    }

    private static string? Arg(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
