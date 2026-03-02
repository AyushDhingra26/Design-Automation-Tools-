using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using APSDeleteWallsAPI.Models;

namespace APSDeleteWallsAPI.Controllers;

[ApiController]
public class UserController : ControllerBase
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _config;

    public UserController(IHttpClientFactory httpClientFactory, IConfiguration config)
    {
        _httpClientFactory = httpClientFactory;
        _config = config;
    }

    // GET /user/logoff  (your JS calls this)
    [HttpGet("/user/logoff")]
    public IActionResult Logoff()
    {
        HttpContext.Session.Clear();
        return Content("/"); // your JS does: location.href = oauthUrl;
    }

    // POST /user/token  (your JS calls this)
    [HttpPost("/user/token")]
    public async Task<IActionResult> Token([FromBody] TokenRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.client_id) || string.IsNullOrWhiteSpace(req.client_secret))
            return BadRequest("client_id and client_secret are required");

        // If already have token in session and not expired, return it
        var token = HttpContext.Session.GetString("access_token");
        var expUnix = HttpContext.Session.GetString("expires_at_unix");

        if (!string.IsNullOrWhiteSpace(token) && long.TryParse(expUnix, out var exp))
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (now < exp - 60) // keep 60s buffer
            {
                return Ok(new { token, expires_in = (exp - now) });
            }
        }

        // Get new token from APS
        var scopes = _config["APS:Scopes"] ?? "data:read data:write data:create bucket:create code:all";

        var http = _httpClientFactory.CreateClient();

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = req.client_id!,
            ["client_secret"] = req.client_secret!,
            ["scope"] = scopes
        };

        using var resp = await http.PostAsync(
            "https://developer.api.autodesk.com/authentication/v2/token",
            new FormUrlEncodedContent(form)
        );

        var json = await resp.Content.ReadAsStringAsync();

        if (!resp.IsSuccessStatusCode)
        {
            // show APS error back to UI
            return StatusCode((int)resp.StatusCode, json);
        }

        // Parse APS response: { access_token, token_type, expires_in }
        using var doc = JsonDocument.Parse(json);
        var accessToken = doc.RootElement.GetProperty("access_token").GetString();
        var expiresIn = doc.RootElement.GetProperty("expires_in").GetInt32();

        // Save in session like Node req.session
        HttpContext.Session.SetString("access_token", accessToken ?? "");
        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn).ToUnixTimeSeconds();
        HttpContext.Session.SetString("expires_at_unix", expiresAt.ToString());

        return Ok(new { token = accessToken, expires_in = expiresIn });
    }
}