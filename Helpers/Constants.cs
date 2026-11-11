using System.Reflection;

namespace Coworker.Helpers;

public static class ApiConstants
{
    // Mac reference: APIConstants in Constants.swift (nodeProductionURL / publicWebAppURL).
    public const string ProductionApiBaseUrl = "https://api.humanlike.co";
    public const string ProductionWebBaseUrl = "https://humanlike.co";
    public const string CoworkerCallbackScheme = "coworker-auth";
    public const string CoworkerAlternateScheme = "coworker";
    public const string ConnectDeviceEndpoint = "/coworker/connect-device";

    /// Humanlike-node API host. Override with COWORKER_API_BASE (e.g. http://localhost:1001).
    public static string ApiBaseUrl { get; } = ResolveBaseUrl("COWORKER_API_BASE", ProductionApiBaseUrl);

    /// Web app host. Override with COWORKER_WEB_BASE (e.g. http://localhost:1000). Keep it on the
    /// canonical host: .NET drops the Authorization header on a cross-host redirect.
    public static string WebBaseUrl { get; } = ResolveBaseUrl("COWORKER_WEB_BASE", ProductionWebBaseUrl);

    /// Web proxy for the coworker API; the Mac tries it before the node for GETs.
    public static string WebProxyBaseUrl => $"{WebBaseUrl}/api/coworker";

    /// Realtime socket, derived from the API host the way the Mac does (wss for https, ws for http).
    public static string WebSocketUrl => ApiBaseUrl.Replace("https://", "wss://").Replace("http://", "ws://") + "/coworker";

    public static string CoworkerLoginUrl => $"{WebBaseUrl}/coworker/login";
    public static string CoworkerConnectUrl => $"{WebBaseUrl}/coworker/connect";
    public static string CoworkerDashboardUrl => $"{WebBaseUrl}/coworker/dashboard";
    public static string CoworkerBillingUrl => $"{WebBaseUrl}/coworker/billing";
    public static string CoworkerLearnMoreUrl => $"{WebBaseUrl}/coworker";

    /// Marketing version (Mac CFBundleShortVersionString), single-sourced from Coworker.csproj (AppVersion).
    public static string AppVersion { get; } = ReadAppVersion();

    /// Build number (Mac CFBundleVersion, appcast sparkle:version): 4th part of the file version, from AppBuild.
    public static string AppBuild { get; } = ReadAppBuild();

    /// "1.2.0 (7)", the About string on the Mac ("{short} ({build})").
    public static string AppVersionDisplay => $"{AppVersion} ({AppBuild})";

    private static string ResolveBaseUrl(string envVar, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(envVar)?.Trim();
        if (!string.IsNullOrEmpty(value) &&
            Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
        {
            return value.TrimEnd('/');
        }
        return fallback;
    }

    private static string ReadAppVersion()
    {
        var info = typeof(ApiConstants).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(info)) return "1.0.0";
        var plus = info.IndexOf('+');
        return plus < 0 ? info : info[..plus];
    }

    private static string ReadAppBuild()
    {
        var file = typeof(ApiConstants).Assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version;
        var parts = file?.Split('.');
        return parts is { Length: >= 4 } && int.TryParse(parts[3], out var build) ? build.ToString() : "1";
    }
}

public static class LanguageOption
{
    public static readonly (string Code, string Name)[] Languages = new[]
    {
        ("en", "English"),
        ("es", "Spanish"),
        ("fr", "French"),
        ("de", "German"),
        ("it", "Italian"),
        ("pt", "Portuguese"),
        ("nl", "Dutch"),
        ("pl", "Polish"),
        ("ja", "Japanese"),
        ("ko", "Korean"),
        ("zh", "Chinese"),
        ("ar", "Arabic"),
        ("hi", "Hindi"),
        ("ru", "Russian"),
        ("tr", "Turkish"),
        ("sv", "Swedish"),
        ("da", "Danish"),
        ("fi", "Finnish"),
        ("no", "Norwegian"),
        ("el", "Greek"),
        ("he", "Hebrew"),
        ("th", "Thai"),
        ("vi", "Vietnamese"),
        ("uk", "Ukrainian"),
        ("cs", "Czech"),
        ("ro", "Romanian"),
        ("hu", "Hungarian"),
        ("auto", "Auto-detect"),
    };
}
