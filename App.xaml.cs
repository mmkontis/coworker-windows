using Microsoft.UI.Xaml;
using Microsoft.Win32;
using Coworker.Dictation;
using Coworker.Helpers;
using Coworker.Services;

namespace Coworker;

public partial class App : Application
{
    // A private COWORKER_DATA_DIR (dev/test) gets its own mutex and pipe so it can run beside the installed app.
    private static readonly string InstanceSuffix = Environment.GetEnvironmentVariable("COWORKER_DATA_DIR") is { Length: > 0 } dir
        ? "." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(dir.ToLowerInvariant())))[..12]
        : "";
    private static readonly string PipeName = "CoworkerWindowsDeepLink" + InstanceSuffix;
    private const string ActivateMessage = "activate";
    private const string RestartedArgument = "--restarted";

    public static Window? MainWindow { get; private set; }

    /// True once the app is really exiting (tray Quit, Settings Quit): closing the main window then
    /// no longer just minimizes it.
    public static bool IsQuitting { get; private set; }

    /// Windows is logging off or shutting down; the main window must not veto its own close.
    public static bool IsSessionEnding { get; set; }

    private static System.Threading.Mutex? _mutex;

    public App()
    {
        InitializeComponent();
        // Errors nobody caught (a XAML failure while building a page, say) would otherwise leave no trace.
        UnhandledException += (_, e) => DebugLog.Write($"Unhandled exception: {e.Exception}");
        if (Environment.GetEnvironmentVariable("COWORKER_TRACE_EXCEPTIONS") == "1")
            AppDomain.CurrentDomain.FirstChanceException += (_, e) => DebugLog.Write($"First-chance: {e.Exception.GetType().Name}: {e.Exception.Message}");
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        UiThread.Initialize();

        var commandLine = Environment.GetCommandLineArgs();
        var link = commandLine.FirstOrDefault(IsDeepLink);

        _mutex = new System.Threading.Mutex(true, "CoworkerWindowsApp" + InstanceSuffix, out bool isNew);
        if (link != null) DebugLog.Write($"Launched with a link (first instance: {isNew})");
        if (!isNew && commandLine.Contains(RestartedArgument) && WaitForPreviousInstance(_mutex)) isNew = true;
        if (!isNew)
        {
            // Hand over to the running instance: it handles the link, or just comes to the front.
            SendToRunningInstance(link ?? ActivateMessage);
            Environment.Exit(0);
            return;
        }

        EnsureProtocolHandler(ApiConstants.CoworkerCallbackScheme);
        EnsureProtocolHandler(ApiConstants.CoworkerAlternateScheme);
        StartupService.Apply();

        ThemeService.Shared.Initialize();
        MainWindow = new MainWindow();
        // A launch at sign-in starts quietly in the tray (the window opens from the icon).
        if (!commandLine.Contains(StartupService.BackgroundArgument)) MainWindow.Activate();

        if (link != null) _ = DeepLinkService.HandleAsync(link);

        StartPipeListener();
    }

    /// Stops background work and releases system-wide hooks. Safe to call more than once.
    public static void PrepareToQuit()
    {
        if (IsQuitting) return;
        IsQuitting = true;

        SessionService.Shared.Stop();
        RealtimeService.Shared.Disconnect();
        DictationService.Shared.Shutdown();
        TrayService.Shared.Dispose();
        SettingsStore.Flush();
    }

    public static void Quit()
    {
        PrepareToQuit();
        Current.Exit();
    }

    /// Starts a new instance and quits this one (Mac restartApplication). The new instance waits
    /// for this one to let go of the single-instance mutex.
    public static void Restart()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return;

        PrepareToQuit();
        _mutex?.ReleaseMutex();
        _mutex = null;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, RestartedArgument) { UseShellExecute = true });
        Current.Exit();
    }

    // The previous instance releases the mutex (or exits, which abandons it) right after starting us.
    private static bool WaitForPreviousInstance(System.Threading.Mutex mutex)
    {
        try
        {
            return mutex.WaitOne(TimeSpan.FromSeconds(5));
        }
        catch (System.Threading.AbandonedMutexException)
        {
            return true;
        }
    }

    private static bool IsDeepLink(string arg) =>
        arg.StartsWith($"{ApiConstants.CoworkerCallbackScheme}://", StringComparison.OrdinalIgnoreCase) ||
        arg.StartsWith($"{ApiConstants.CoworkerAlternateScheme}://", StringComparison.OrdinalIgnoreCase);

    /// Registers the URL scheme for the current user, unless it already points at an exe that
    /// exists (a dev build must not steal the installed app's links on every run).
    private static void EnsureProtocolHandler(string scheme)
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath)) return;

            using (var existing = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{scheme}\shell\open\command"))
            {
                if (RegisteredExecutable(existing?.GetValue("") as string) is { } registered && File.Exists(registered))
                    return;
            }

            using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{scheme}");
            key.SetValue("", $"URL:{scheme}");
            key.SetValue("URL Protocol", "");
            using (var iconKey = key.CreateSubKey("DefaultIcon")) iconKey.SetValue("", $"\"{exePath}\",0");
            using (var commandKey = key.CreateSubKey(@"shell\open\command")) commandKey.SetValue("", $"\"{exePath}\" \"%1\"");
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            DebugLog.Write($"Could not register {scheme}:// ({ex.Message})");
        }
    }

    /// The exe path out of a registered command line: "C:\path\Coworker.exe" "%1".
    private static string? RegisteredExecutable(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        command = command.Trim();
        if (command[0] == '"')
        {
            var end = command.IndexOf('"', 1);
            return end > 1 ? command[1..end] : null;
        }
        var space = command.IndexOf(' ');
        return space < 0 ? command : command[..space];
    }

    private static void StartPipeListener()
    {
        Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    using var server = new System.IO.Pipes.NamedPipeServerStream(PipeName, System.IO.Pipes.PipeDirection.In);
                    await server.WaitForConnectionAsync();
                    using var reader = new StreamReader(server);
                    var message = await reader.ReadLineAsync();
                    if (string.IsNullOrEmpty(message)) continue;

                    UiThread.Post(() =>
                    {
                        if (message == ActivateMessage) WindowService.Shared.Show();
                        else _ = DeepLinkService.HandleAsync(message);
                    });
                }
                catch (Exception ex)
                {
                    DebugLog.Write($"Deep-link pipe: {ex.Message}");
                    await Task.Delay(1000);
                }
            }
        });
    }

    private static void SendToRunningInstance(string message)
    {
        try
        {
            using var client = new System.IO.Pipes.NamedPipeClientStream(".", PipeName, System.IO.Pipes.PipeDirection.Out);
            client.Connect(2000);

            // Only this process, just launched by the browser or the user, may take the foreground. Hand
            // that right to the running instance, or its window can't come to the front for the link.
            if (GetNamedPipeServerProcessId(client.SafePipeHandle, out var serverProcessId))
                AllowSetForegroundWindow(serverProcessId);

            using var writer = new StreamWriter(client);
            writer.WriteLine(message);
            writer.Flush();
        }
        catch (Exception ex) when (ex is IOException or TimeoutException)
        {
            DebugLog.Write($"Could not reach the running instance ({ex.Message})");
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint serverProcessId);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
