# Coworker for Windows: end-to-end test

The real, published `Coworker.exe` dictating into Notepad, the way a user does it. GitHub runs it on every push
that changes the Windows app (`.github/workflows/coworker-windows-e2e.yml`).

| Step | How |
|---|---|
| Microphone | VB-CABLE, a virtual sound card: the test plays a voice into "CABLE Input", and the app records it from "CABLE Output", the machine's only microphone |
| Voice | Recorded clips in `clips/` (made with macOS `say`, 16 kHz mono) |
| Hotkey | Ctrl+Shift held with `SendInput`, the same input path as a keyboard. The app normally ignores injected keys; with `COWORKER_E2E=1` it accepts keys the test tags with a marker (`Helpers/EndToEndTestMode.cs`), and nothing else |
| Server | `FakeServer.cs` answers like api.humanlike.co: `/coworker/user/me`, `/coworker/models`, `/coworker/words`, telemetry, the live dictation socket and the HTTP upload fallback. It keeps the audio of every take |
| Sign-in | `settings.json` written the way a signed-in install has it, in a folder of its own (`COWORKER_DATA_DIR`) |
| Paste target | Notepad, typed into with real key presses and read back through UI Automation |
| Checks | The text in Notepad, and that the server got a recording as long as the clip and not silent |
| Evidence | `screen.mp4` (ffmpeg), a screenshot per case, each recording as WAV, the app's `debug.log`, the server log, `results.json` |

Cases: an empty field, the middle of a sentence ("Meet me at the " + "Office at noon." gives "Meet me at the
office at noon."), and after a sentence end ("Done." + "Next step." gives "Done. Next step.").

Run it on a Windows PC (needs VB-CABLE, and ffmpeg for the video):

```
dotnet publish ..\..\Coworker.csproj -c Release -p:Platform=x64 -p:PublishDir=%TEMP%\coworker-app\
dotnet run -c Release -- --app %TEMP%\coworker-app\Coworker.exe --out e2e-output --models ..\..\..\..\Humanlike-node\plugins\coworker\models.baseline.json
```

It takes over the keyboard and Notepad for about a minute.
