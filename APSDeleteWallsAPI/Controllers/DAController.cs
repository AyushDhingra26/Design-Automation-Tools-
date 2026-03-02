using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

namespace APSDeleteWallsAPI.Controllers;

[ApiController]
[Route("da")]
public class DAController : ControllerBase
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly APSDeleteWallsAPI.Services.ApsAuthService _auth;
    private readonly IConfiguration _cfg;

    public DAController(IHttpClientFactory httpClientFactory,
                        APSDeleteWallsAPI.Services.ApsAuthService auth,
                        IConfiguration cfg)
    {
        _httpClientFactory = httpClientFactory;
        _auth = auth;
        _cfg = cfg;
    }



    private async Task<HttpClient> Create2LeggedClientAsync()
    {
        var clientId = _cfg["APS:ClientId"]!;
        var clientSecret = _cfg["APS:ClientSecret"]!;

        var token = await _auth.Get2LeggedTokenOnlyAsync(clientId, clientSecret);

        var http = _httpClientFactory.CreateClient();
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);
        http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));

        return http;
    }

   

    private static string DaBase(string path) => $"https://developer.api.autodesk.com/da/us-east/v3/{path}";

    // --------------------------
    // Helper: DA Request + paginationToken (same as Node)
    // --------------------------
    private async Task<JsonElement> DaRequestAsync(string path, HttpMethod method, JsonElement? body = null)
    {
        // Use 3-legged ONLY for workitems endpoints, otherwise 2-legged
        HttpClient http = await Create2LeggedClientAsync();
        var urlBase = DaBase(path);

        string? page = null;
        JsonElement? firstResponse = null;
        var combinedData = new List<JsonElement>();

        while (true)
        {
            var url = page == null ? urlBase : $"{urlBase}?page={page}";
            using var req = new HttpRequestMessage(method, url);

            if (body.HasValue)
            {
                var json = body.Value.GetRawText();
                req.Content = new StringContent(json, Encoding.UTF8, "application/json");
            }

            using var resp = await http.SendAsync(req);
            var text = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
                throw new Exception($"DA error {resp.StatusCode}: {text}");

            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;

            if (firstResponse == null)
                firstResponse = root.Clone();

            // ✅ Only objects can have properties like "data" and "paginationToken"
            if (root.ValueKind == JsonValueKind.Object)
            {
                // Collect data[] across pages (if exists)
                if (root.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in dataEl.EnumerateArray())
                        combinedData.Add(item.Clone());
                }

                // Pagination token
                if (root.TryGetProperty("paginationToken", out var tokenEl) &&
                    tokenEl.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(tokenEl.GetString()))
                {
                    page = tokenEl.GetString();
                    continue;
                }
            }
            else
            {
                // If DA returns non-object (string/array), just return it (no paging logic)
                return root.Clone();
            }

            // Finalize: if multi-page, replace data in first response
            if (firstResponse.HasValue)
            {
                var final = firstResponse.Value;
                if (final.ValueKind != JsonValueKind.Object)
                    return final;

                if (!final.TryGetProperty("data", out _))
                    return final;

                // Build a JSON object with replaced data array
                using var outDoc = JsonDocument.Parse(final.GetRawText());
                var outRoot = outDoc.RootElement;

                var dict = new Dictionary<string, object?>();

                foreach (var prop in outRoot.EnumerateObject())
                {
                    if (prop.NameEquals("data"))
                        continue;

                    dict[prop.Name] = JsonSerializer.Deserialize<object>(prop.Value.GetRawText());
                }

                dict["data"] = combinedData.Select(x => JsonSerializer.Deserialize<object>(x.GetRawText())).ToList();

                var finalJson = JsonSerializer.SerializeToElement(dict);
                return finalJson;
            }

            return root.Clone();
        }
    }

    // --------------------------
    // WORKITEMS
    // --------------------------

    // POST /da/workitems

    [HttpGet("nickname")]
    public async Task<IActionResult> GetNickname()
    {
        var me = await DaRequestAsync("forgeapps/me", HttpMethod.Get);

        string nickname = me.ValueKind switch
        {
            JsonValueKind.String => me.GetString() ?? "",
            JsonValueKind.Object => (me.TryGetProperty("nickname", out var nn) && nn.ValueKind == JsonValueKind.String)
                                        ? (nn.GetString() ?? "")
                                        : "",
            _ => ""
        };

        return Ok(new { nickname });
    }
    [HttpPost("workitems")]
    public async Task<IActionResult> CreateWorkitem([FromBody] JsonElement body)
    {
        try
        {
            var result = await DaRequestAsync("workitems", HttpMethod.Post, body);
            return Ok(JsonSerializer.Deserialize<object>(result.GetRawText()));
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { message = ex.Message });
        }
    }

    // GET /da/workitems/info?id=...
    [HttpGet("workitems/info")]
    public async Task<IActionResult> WorkitemInfo([FromQuery] string id)
    {
        try
        {
            var result = await DaRequestAsync($"workitems/{id}", HttpMethod.Get);
            return Ok(JsonSerializer.Deserialize<object>(result.GetRawText()));
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { message = ex.Message });
        }
    }

    // GET /da/workitems/treeNode?id=...
    [HttpGet("workitems/treeNode")]
    public async Task<IActionResult> WorkitemsTreeNode([FromQuery] string? id)
    {
        try
        {
            // Root load
            if (string.IsNullOrWhiteSpace(id))
            {
                return Ok(new[]
                {
                    new { id = "getInputs", text = "New Workitem", children = false }
                });
            }

            // + button pressed
            if (id == "getInputs")
                return Ok(new { children = false });

            // Else list workitems
            var result = await DaRequestAsync("workitems", HttpMethod.Get);

            var items = new List<object>();
            if (result.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in dataEl.EnumerateArray())
                {
                    // workitems list is objects with id
                    if (item.TryGetProperty("id", out var idEl))
                    {
                        var wid = idEl.GetString();
                        if (!string.IsNullOrWhiteSpace(wid))
                        {
                            items.Add(new { id = wid, text = wid, type = "workitem", children = false });
                        }
                    }
                }
            }

            return Ok(items);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { message = ex.Message });
        }
    }

    // --------------------------
    // ITEMS: appbundles & activities (dynamic :type routes)
    // --------------------------

    private static (string nick, string name, string alias) GetNameParts(string full)
    {
        // Expected: "nickname.Name+alias"
        // But do NOT crash if format is different.
        if (string.IsNullOrWhiteSpace(full))
            return ("", full ?? "", "");

        var dotIndex = full.IndexOf('.');
        var plusIndex = full.IndexOf('+');

        if (dotIndex < 0 || plusIndex < 0 || plusIndex < dotIndex)
        {
            // fallback: treat full as name so UI still works
            return ("", full, "");
        }

        var nick = full.Substring(0, dotIndex);
        var name = full.Substring(dotIndex + 1, plusIndex - (dotIndex + 1));
        var alias = full.Substring(plusIndex + 1);

        return (nick, name, alias);
    }

    private static string GetFullName(string nick, string name, string alias) => $"{nick}.{name}+{alias}";

    // GET /da/{type}/treeNode?id=...
    [HttpGet("{type}/treeNode")]
    public async Task<IActionResult> ItemsTreeNode([FromRoute] string type, [FromQuery] string id)
    {
        try
        {
            id = Uri.UnescapeDataString(id ?? "#");
            var paths = id.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var level = (id == "#") ? 0 : paths.Length;

            // Root
            if (id == "#")
            {
                return Ok(new[]
                {
                    new { id = "Personal", text = "Personal", type = "folder", children = true },
                    new { id = "Shared", text = "Shared", type = "folder", children = true }
                });
            }

            // Level 1: Personal / Shared -> list items
            if (level == 1)
            {
                var isPersonal = paths[0] == "Personal";

                // GET forgeapps/me for nickname (safe)
                // GET forgeapps/me for nickname
                var me = await DaRequestAsync("forgeapps/me", HttpMethod.Get);

                string nickname = me.ValueKind switch
                {
                    JsonValueKind.String => me.GetString() ?? "",
                    JsonValueKind.Object => (me.TryGetProperty("nickname", out var nn) && nn.ValueKind == JsonValueKind.String)
                                                ? (nn.GetString() ?? "")
                                                : "",
                    _ => ""
                };

                var list = await DaRequestAsync(type, HttpMethod.Get);

                // ✅ Prevent crash: list must be an object with data[]
                if (list.ValueKind != JsonValueKind.Object)
                    return Ok(Array.Empty<object>());

                var items = new Dictionary<string, object>(); // unique by name

                if (list.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var itemEl in dataEl.EnumerateArray())
                    {
                        // ✅ Prevent crash: each item must be a string
                        if (itemEl.ValueKind != JsonValueKind.String)
                            continue;

                        var full = itemEl.GetString();
                        if (string.IsNullOrWhiteSpace(full))
                            continue;

                        // safe name split (your updated GetNameParts should be safe already)
                        (string nick, string name, string alias) = GetNameParts(full);

                        // ✅ Correct mine check + avoid empty nickname issues
                        var isMine = !string.IsNullOrWhiteSpace(nickname) &&
                                     full.StartsWith(nickname + ".", StringComparison.OrdinalIgnoreCase);

                        // same XOR logic as your Node version
                        if ((isMine && isPersonal) || (!isMine && !isPersonal))
                        {
                            if (!items.ContainsKey(name))
                            {
                                if (isMine)
                                {
                                    // Personal: keep by name so versions/aliases work
                                    items[name] = new
                                    {
                                        id = $"Personal/{name}",
                                        text = name,
                                        type = "item",
                                        children = true
                                    };
                                }
                                else
                                {
                                    // Shared: store FULL id encoded so info-click works
                                    var fullEncoded = Uri.EscapeDataString(full);
                                    items[name] = new
                                    {
                                        id = $"Shared/{fullEncoded}",
                                        text = name,
                                        type = "item",
                                        children = false
                                    };
                                }
                            }
                        }
                    }
                }

                return Ok(items.Values);
            }

            // Level 2: Personal/Item -> versions (and mark which versions have aliases)
            if (level == 2)
            {
                var appName = paths[1];

                var versions = await DaRequestAsync($"{type}/{appName}/versions", HttpMethod.Get);
                var aliases = await DaRequestAsync($"{type}/{appName}/aliases", HttpMethod.Get);

                var versionHasAlias = new HashSet<int>();
                if (aliases.TryGetProperty("data", out var aliasData) && aliasData.ValueKind == JsonValueKind.Array)
                {
                    foreach (var a in aliasData.EnumerateArray())
                    {
                        if (a.TryGetProperty("version", out var v) && v.TryGetInt32(out var vi))
                            versionHasAlias.Add(vi);
                    }
                }

                var nodes = new List<object>();
                if (versions.TryGetProperty("data", out var verData) && verData.ValueKind == JsonValueKind.Array)
                {
                    foreach (var v in verData.EnumerateArray())
                    {
                        int vid;

                        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out vid))
                        {
                            // ok
                        }
                        else if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out vid))
                        {
                            // ok (fallback if API ever returns strings)
                        }
                        else
                        {
                            continue; // skip unknown type
                        }

                        nodes.Add(new
                        {
                            id = $"{paths[0]}/{appName}/{vid}",
                            text = vid.ToString(),
                            type = "version",
                            children = versionHasAlias.Contains(vid)
                        });
                    }
                }
                return Ok(nodes);
            }

            // Level 3: Personal/Item/Version -> aliases
            if (level == 3)
            {
                var appName = paths[1];
                var version = int.Parse(paths[2]);

                var aliases = await DaRequestAsync($"{type}/{appName}/aliases", HttpMethod.Get);

                var nodes = new List<object>();
                if (aliases.TryGetProperty("data", out var aliasData) && aliasData.ValueKind == JsonValueKind.Array)
                {
                    foreach (var a in aliasData.EnumerateArray())
                    {
                        if (a.TryGetProperty("version", out var v) && v.TryGetInt32(out var vi) && vi == version)
                        {
                            var aid = a.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String ? idEl.GetString() : null;

                            if (!string.IsNullOrWhiteSpace(aid))
                            {
                                nodes.Add(new
                                {
                                    id = $"{paths[0]}/{appName}/{version}/{aid}",
                                    text = aid,
                                    type = "alias",
                                    children = false
                                });
                            }
                           
                        }
                    }
                }
                return Ok(nodes);
            }

            return Ok(Array.Empty<object>());
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { message = ex.Message });
        }
    }

    // GET /da/{type}/info?id=...&nickName=...&alias=...
    [HttpGet("{type}/info")]
    public async Task<IActionResult> ItemInfo([FromRoute] string type, [FromQuery] string id, [FromQuery] string? nickName, [FromQuery] string? alias)
    {
        try
        {
            id = Uri.UnescapeDataString(id);
            var paths = id.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var level = paths.Length;

            // level 1: direct id (full name)
            if (level == 1)
            {
                var info = await DaRequestAsync($"{type}/{paths[0]}", HttpMethod.Get);
                return Ok(JsonSerializer.Deserialize<object>(info.GetRawText()));
            }

            // level 2: Shared/Item -> need fullName from query
            // level 2: Shared/{fullEncoded}
            if (level == 2 && paths[0] == "Shared")
            {
                var fullName = Uri.UnescapeDataString(paths[1]); // nickname.Name+alias
                var info = await DaRequestAsync($"{type}/{fullName}", HttpMethod.Get);
                return Ok(JsonSerializer.Deserialize<object>(info.GetRawText()));
            }
            // level 4: Personal/Item/Version/Alias -> alias info
            if (level == 4)
            {
                if (string.IsNullOrWhiteSpace(nickName))
                    return BadRequest("nickName is required");

                var fullName = GetFullName(Uri.UnescapeDataString(nickName), paths[1], paths[3]);
                var info = await DaRequestAsync($"{type}/{fullName}", HttpMethod.Get);
                return Ok(JsonSerializer.Deserialize<object>(info.GetRawText()));
            }

            return BadRequest("Invalid id format");
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { message = ex.Message });
        }
    }

    // POST /da/{type}  (create item/version/alias)
    [HttpPost("{type}")]
    public async Task<IActionResult> CreateItem([FromRoute] string type, [FromBody] JsonElement body)
    {
        try
        {
            // body: { id, nickName, alias, receiver, body: {...} }
            if (!body.TryGetProperty("id", out var idEl))
                return BadRequest("Missing id");

            var id = idEl.GetString() ?? "";
            var paths = id.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var level = paths.Length;

            // folder level => create item
            if (level == 1)
            {
                var realBody = body.GetProperty("body");
                var res = await DaRequestAsync(type, HttpMethod.Post, realBody);
                return Ok(JsonSerializer.Deserialize<object>(res.GetRawText()));
            }

            // item level => create version
            if (level == 2)
            {
                var realBody = body.GetProperty("body");
                var res = await DaRequestAsync($"{type}/{paths[1]}/versions", HttpMethod.Post, realBody);
                return Ok(JsonSerializer.Deserialize<object>(res.GetRawText()));
            }

            // version level => create alias
            if (level == 3)
            {
                var aliasName = body.TryGetProperty("alias", out var aEl) ? aEl.GetString() : null;
                var receiver = body.TryGetProperty("receiver", out var rEl) ? rEl.GetString() : null;

                if (string.IsNullOrWhiteSpace(aliasName))
                    return BadRequest("Missing alias");

                var payload = new Dictionary<string, object?>
                {
                    ["version"] = int.Parse(paths[2]),
                    ["id"] = aliasName
                };
                if (!string.IsNullOrWhiteSpace(receiver))
                    payload["receiver"] = receiver;

                var payloadEl = JsonSerializer.SerializeToElement(payload);
                var res = await DaRequestAsync($"{type}/{paths[1]}/aliases", HttpMethod.Post, payloadEl);
                return Ok(JsonSerializer.Deserialize<object>(res.GetRawText()));
            }

            // else fallback
            var fallbackBody = body.TryGetProperty("body", out var bEl2) ? bEl2 : body;
            var fallbackRes = await DaRequestAsync(type, HttpMethod.Post, fallbackBody);
            return Ok(JsonSerializer.Deserialize<object>(fallbackRes.GetRawText()));
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { message = ex.Message });
        }
    }

    // DELETE /da/{type}/{id}
    [HttpDelete("{type}/{id}")]
    public async Task<IActionResult> DeleteItem([FromRoute] string type, [FromRoute] string id)
    {
        try
        {
            id = Uri.UnescapeDataString(id);
            var paths = id.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var level = paths.Length;

            if (level == 1)
            {
                var res = await DaRequestAsync($"{type}/{paths[0]}", HttpMethod.Delete);
                return Ok(new { response = "done" });
            }
            if (level == 2)
            {
                var res = await DaRequestAsync($"{type}/{paths[1]}", HttpMethod.Delete);
                return Ok(new { response = "done" });
            }
            if (level == 3)
            {
                var res = await DaRequestAsync($"{type}/{paths[1]}/versions/{paths[2]}", HttpMethod.Delete);
                return Ok(new { response = "done" });
            }
            if (level == 4)
            {
                var res = await DaRequestAsync($"{type}/{paths[1]}/aliases/{paths[3]}", HttpMethod.Delete);
                return Ok(new { response = "done" });
            }

            return BadRequest("Invalid id format");
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { message = ex.Message });
        }
    }
}