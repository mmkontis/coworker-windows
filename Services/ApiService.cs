using System.Text.Json;

namespace Coworker.Services;

/// <summary>
/// Typed endpoints of the coworker API. Split by area into ApiService.*.cs so feature slices add
/// their endpoints in their own file. Everything goes through <see cref="ApiClient"/> (auth,
/// refresh-retry, timeouts, typed <see cref="ApiException"/>).
/// </summary>
public partial class ApiService
{
    public static ApiService Shared { get; } = new();

    private readonly ApiClient _api = ApiClient.Shared;

    private ApiService() { }

    private static JsonSerializerOptions JsonOpts => ApiClient.Json;

    private static string GetDeviceId() => SessionService.Shared.DeviceId;
}
