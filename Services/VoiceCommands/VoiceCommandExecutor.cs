using Coworker.Helpers;
using Coworker.Models;
using Windows.System;

namespace Coworker.Services.VoiceCommands;

/// <summary>
/// Runs the steps of a voice command (Mac: executeCommand in VoiceCommandManager.swift). open_web_page,
/// open_app and create_chat run on this machine; every other action runs on the server.
/// </summary>
public sealed class VoiceCommandExecutor
{
    private const string ContextPlaceholder = "{{context}}";

    /// Opens a URI with its default handler. Replaceable so tests do not open a browser.
    public Func<Uri, Task<bool>> OpenUri { get; init; } = async uri => await Launcher.LaunchUriAsync(uri);

    public Func<Task<IReadOnlyList<InstalledApp>>> GetInstalledApps { get; init; } = ShellApps.GetInstalledAppsAsync;

    public Func<string, Task<bool>> LaunchProgram { get; init; } = AppLauncher.LaunchAsync;

    /// <param name="transcript">The full utterance; the server extracts the argument from it again.</param>
    /// <param name="arguments">The spoken argument found by the matcher (or typed into the context input).</param>
    public async Task<VoiceCommandExecuteResult> ExecuteAsync(VoiceCommand command, string transcript, string arguments)
    {
        var steps = command.AllActions;
        if (steps.Count <= 1)
            return await ExecuteStepAsync(command, steps[0], 0, transcript, arguments);

        // Steps run one after another; the chain stops at the first failure.
        var results = new List<string>();
        var allSucceeded = true;
        for (var i = 0; i < steps.Count && allSucceeded; i++)
        {
            var result = await ExecuteStepAsync(command, steps[i], i, transcript, arguments);
            results.Add(result.Result);
            allSucceeded = result.Success;
        }

        return Outcome(command, "multi_action", allSucceeded, string.Join(" → ", results));
    }

    private Task<VoiceCommandExecuteResult> ExecuteStepAsync(VoiceCommand command, VoiceCommandAction step, int index,
        string transcript, string arguments) => step.ActionType switch
        {
            "open_web_page" => OpenWebPageAsync(command, step, arguments),
            "open_app" => OpenAppAsync(command, step),
            "create_chat" => OpenChatAsync(command, step, arguments),
            _ => ExecuteOnServerAsync(command, step, index, transcript),
        };

    private async Task<VoiceCommandExecuteResult> OpenWebPageAsync(VoiceCommand command, VoiceCommandAction step, string context)
    {
        var template = step.GetConfig("url") ?? "";
        if (template.Length == 0) return Outcome(command, step, false, "No URL configured");

        var url = template.Replace(ContextPlaceholder, Uri.EscapeDataString(context));
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return Outcome(command, step, false, $"Invalid URL: {template}");

        return await OpenAsync(command, step, uri, $"Opened {uri.OriginalString}", $"Failed to open {uri.OriginalString}");
    }

    private async Task<VoiceCommandExecuteResult> OpenChatAsync(VoiceCommand command, VoiceCommandAction step, string context)
    {
        var message = (step.GetConfig("message") ?? ContextPlaceholder).Replace(ContextPlaceholder, context);
        var url = $"{ApiConstants.WebBaseUrl}/coworker/dashboard/chat?message={Uri.EscapeDataString(message)}";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return Outcome(command, step, false, "Invalid chat URL");

        return await OpenAsync(command, step, uri, "Opened chat", "Failed to open chat");
    }

    private async Task<VoiceCommandExecuteResult> OpenAsync(VoiceCommand command, VoiceCommandAction step, Uri uri,
        string success, string failure)
    {
        var opened = await OpenUri(uri);
        return Outcome(command, step, opened, opened ? success : failure);
    }

    private async Task<VoiceCommandExecuteResult> OpenAppAsync(VoiceCommand command, VoiceCommandAction step)
    {
        var bundleId = (step.GetConfig("bundle_identifier") ?? "").Trim();
        var appPath = (step.GetConfig("app_path") ?? "").Trim();
        var appName = (step.GetConfig("app_name") ?? "").Trim();
        if (bundleId.Length == 0 && appPath.Length == 0)
            return Outcome(command, step, false, "No app configured");

        IReadOnlyList<InstalledApp> installed;
        try
        {
            installed = await GetInstalledApps();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidCastException or IOException)
        {
            DebugLog.Write($"Voice command: app list unavailable ({ex.Message})");
            installed = Array.Empty<InstalledApp>();
        }

        var app = AppLauncher.Resolve(installed, bundleId, appPath, appName);
        if (app == null)
        {
            var label = appName.Length > 0 ? appName : bundleId.Length == 0 ? appPath : bundleId;
            return Outcome(command, step, false, $"App not found: {label}");
        }

        var launched = await LaunchProgram(app.Target);
        return Outcome(command, step, launched, launched ? $"Opened {app.Name}" : $"Failed to open {app.Name}");
    }

    // The server runs the command's top-level action, which is the first step. It never sees a later
    // step (the Mac sends the same request for every step and so repeats step 1), so a later
    // server-side step is reported as failed instead of running the wrong action a second time.
    private static async Task<VoiceCommandExecuteResult> ExecuteOnServerAsync(VoiceCommand command, VoiceCommandAction step,
        int index, string transcript)
    {
        if (index > 0)
            return Outcome(command, step, false, $"Step {index + 1} runs on the server and can only be the first step");

        try
        {
            return await ApiService.Shared.ExecuteVoiceCommand(command.Id, transcript);
        }
        catch (ApiException ex)
        {
            DebugLog.Write($"Voice command: execution failed ({ex.Kind} {ex.StatusCode})");
            return Outcome(command, step, false, ex.Message);
        }
    }

    private static VoiceCommandExecuteResult Outcome(VoiceCommand command, VoiceCommandAction step, bool success, string text) =>
        Outcome(command, step.ActionType, success, text);

    private static VoiceCommandExecuteResult Outcome(VoiceCommand command, string actionType, bool success, string text) => new()
    {
        Success = success,
        Result = text,
        IconName = command.IconName,
        IconColor = command.IconColor,
        ActionType = actionType,
    };
}
