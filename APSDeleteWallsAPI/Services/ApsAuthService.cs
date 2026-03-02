using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace APSDeleteWallsAPI.Services;

public class ApsAuthService
{
    private readonly HttpClient _http;
    private readonly IConfiguration _cfg;
    private readonly IHttpContextAccessor _ctx;

    public ApsAuthService(HttpClient http, IConfiguration cfg, IHttpContextAccessor ctx)
    {
        _http = http;
        _cfg = cfg;
        _ctx = ctx;
    }

    /// <summary>
    /// Prefer 3-legged token from session (Personal). If not present, fall back to 2-legged (Shared).
    /// </summary>
    public async Task<string> GetTokenAsync(string clientId, string clientSecret)
    {

        // 1) Try 3-legged from session
        var session = _ctx.HttpContext?.Session;
        if (session != null)
        {
            var access = session.GetString("aps_access_token");
            var refresh = session.GetString("aps_refresh_token");
            var expiresAtStr = session.GetString("aps_expires_at_utc");

            // If access token exists and not expired => use it
            if (!string.IsNullOrWhiteSpace(access) &&
                DateTime.TryParse(expiresAtStr, out var expiresAtUtc) &&
                DateTime.UtcNow < expiresAtUtc)
            {
                return access!;
            }

            // If expired but refresh token exists => refresh it
            if (!string.IsNullOrWhiteSpace(refresh))
            {
                var refreshed = await Refresh3LeggedAsync(clientId, clientSecret, refresh!);

                session.SetString("aps_access_token", refreshed.AccessToken);
                session.SetString("aps_refresh_token", refreshed.RefreshToken);
                session.SetString("aps_expires_at_utc", DateTime.UtcNow.AddSeconds(refreshed.ExpiresIn - 60).ToString("O"));

                return refreshed.AccessToken;
            }
        }

        // 2) Fallback: 2-legged (Shared)
        return await Get2LeggedTokenAsync(clientId, clientSecret);
    }
    public Task<string> Get2LeggedTokenOnlyAsync(string clientId, string clientSecret)
    {
        return Get2LeggedTokenAsync(clientId, clientSecret);
    }

    private async Task<string> Get2LeggedTokenAsync(string clientId, string clientSecret)
    {
        var url = $"{_cfg["APS:AuthBase"]}/authentication/v2/token";

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            // use scopes from config if present, else keep your default
            ["scope"] = _cfg["APS:Scopes"] ?? "data:read data:write data:create bucket:create code:all"
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(form)
        };

        using var res = await _http.SendAsync(req);
        var json = await res.Content.ReadAsStringAsync();

        if (!res.IsSuccessStatusCode)
            throw new Exception($"2-legged token error: {res.StatusCode} {json}");

        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("access_token").GetString()!;
    }

    private async Task<(string AccessToken, string RefreshToken, int ExpiresIn)> Refresh3LeggedAsync(
        string clientId,
        string clientSecret,
        string refreshToken)
    {
        var url = $"{_cfg["APS:AuthBase"]}/authentication/v2/token";

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken
            })
        };

        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}"));
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);

        using var res = await _http.SendAsync(req);
        var json = await res.Content.ReadAsStringAsync();

        if (!res.IsSuccessStatusCode)
            throw new Exception($"3-legged refresh error: {res.StatusCode} {json}");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var access = root.GetProperty("access_token").GetString()!;
        var expiresIn = root.GetProperty("expires_in").GetInt32();

        // Sometimes refresh_token is returned, sometimes not; keep old if missing
        var newRefresh = root.TryGetProperty("refresh_token", out var rt) && rt.ValueKind == JsonValueKind.String
            ? rt.GetString()!
            : refreshToken;

        return (access, newRefresh, expiresIn);
    }
}