using System.Reflection;

namespace Coworker.Services.Updates;

/// <summary>
/// The Ed25519 public key that every update installer must verify against (Mac: Info.plist SUPublicEDKey).
/// It is set at build time from the <c>UpdatePublicKey</c> property in Coworker.csproj, or on the command
/// line: <c>dotnet publish -p:UpdatePublicKey=&lt;base64&gt;</c>. Generate the key pair with
/// <c>installer\tools\UpdateSigner keygen</c> (see installer\README.md); the private key never goes in the repo.
/// While the property is empty the app has no way to trust an update, so updating is switched off.
/// </summary>
public static class UpdateKeys
{
    public const string BuildPropertyName = "UpdatePublicKey";

    public static string Ed25519PublicKey { get; } = Resolve();

    public static bool IsConfigured => !string.IsNullOrWhiteSpace(Ed25519PublicKey);

    private static string Resolve()
    {
#if DEBUG
        // Dev/test only: exercise the updater against a mock feed signed with a throwaway key.
        var overrideKey = Environment.GetEnvironmentVariable("COWORKER_UPDATE_PUBLIC_KEY")?.Trim();
        if (!string.IsNullOrEmpty(overrideKey)) return overrideKey;
#endif
        return typeof(UpdateKeys).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == BuildPropertyName)?.Value?.Trim() ?? "";
    }
}
