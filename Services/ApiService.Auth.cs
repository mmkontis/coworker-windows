using System.Text.Json.Serialization;
using Coworker.Helpers;

namespace Coworker.Services;

public partial class ApiService
{
    private static readonly ApiRequestOptions NoRefresh = new() { RefreshOnUnauthorized = false, Timeout = TimeSpan.FromSeconds(15) };

    /// Exchanges the one-time connect token from the sign-in deep link for a session.
    /// Network only: <see cref="SessionService"/> stores the result.
    public async Task<ConnectDeviceResult?> ConnectDevice(string connectToken, string? email = null)
    {
        var options = new ApiRequestOptions { Authenticated = false, RefreshOnUnauthorized = false };
        var body = new { device_id = GetDeviceId(), connect_token = connectToken };
        var dto = await _api.SendJsonAsync<ConnectDeviceResponse>(HttpMethod.Post, ApiConstants.ConnectDeviceEndpoint, body, options);
        return ToResult(dto, email);
    }

    /// Mac reconnectDevice(): re-registers this device with the current Bearer token and returns a
    /// fresh access token. Used on launch, every 30 minutes and after a 401/403.
    public async Task<ConnectDeviceResult?> ReconnectDevice()
    {
        var dto = await _api.SendJsonAsync<ConnectDeviceResponse>(
            HttpMethod.Post, ApiConstants.ConnectDeviceEndpoint, new { device_id = GetDeviceId() }, NoRefresh);
        return ToResult(dto, null);
    }

    /// Tells the server this device is going away. Failures are the caller's to ignore.
    public async Task DisconnectDevice()
    {
        await _api.SendAsync(HttpMethod.Post, "/coworker/disconnect-device",
            ApiClient.JsonBody(new { device_id = GetDeviceId() }), NoRefresh);
    }

    public async Task<UserProfile> GetUserMe()
    {
        var dto = await _api.GetAsync<UserProfileDto>("/coworker/user/me", ApiRequestOptions.ViaWebProxy);
        var fullName = string.Join(' ', new[] { dto.FirstName, dto.LastName }.Where(part => !string.IsNullOrWhiteSpace(part))).Trim();
        return new UserProfile(dto.Id ?? "", dto.Email ?? "", dto.DeviceId, dto.TestedTypingWpm,
            fullName.Length > 0 ? fullName : null, string.IsNullOrWhiteSpace(dto.ProfilePic) ? null : dto.ProfilePic);
    }

    private static ConnectDeviceResult? ToResult(ConnectDeviceResponse dto, string? fallbackEmail)
    {
        if (string.IsNullOrEmpty(dto.AccessToken)) return null;

        var userId = dto.UserId;
        if (string.IsNullOrEmpty(userId)) userId = JwtClaims.Get(dto.AccessToken, "sub") ?? "";

        return new ConnectDeviceResult(dto.AccessToken, userId, dto.Email ?? fallbackEmail ?? "",
            dto.CompanyId, dto.IsAcademic, dto.IsPremium);
    }

    private record ConnectDeviceResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("user_id")] string? UserId,
        [property: JsonPropertyName("email")] string? Email,
        [property: JsonPropertyName("company_id")] string? CompanyId,
        [property: JsonPropertyName("is_academic")] bool? IsAcademic,
        [property: JsonPropertyName("is_premium")] bool? IsPremium);

    private record UserProfileDto(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("email")] string? Email,
        [property: JsonPropertyName("device_id")] string? DeviceId,
        [property: JsonPropertyName("tested_typing_wpm")] int? TestedTypingWpm,
        [property: JsonPropertyName("first_name")] string? FirstName = null,
        [property: JsonPropertyName("last_name")] string? LastName = null,
        [property: JsonPropertyName("profile_pic")] string? ProfilePic = null);

    public record ConnectDeviceResult(string AccessToken, string UserId, string Email, string? CompanyId = null,
        bool? IsAcademic = null, bool? IsPremium = null);

    /// <param name="PhotoUrl">The Google profile photo saved at sign-up, when there is one.</param>
    public record UserProfile(string Id, string Email, string? DeviceId, int? TestedTypingWpm,
        string? FullName = null, string? PhotoUrl = null);
}
