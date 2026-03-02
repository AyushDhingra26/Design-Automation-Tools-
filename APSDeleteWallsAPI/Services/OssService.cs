using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace APSDeleteWallsAPI.Services;

public class OssService
{
    private readonly HttpClient _http;
    private readonly IConfiguration _cfg;

    public OssService(HttpClient http, IConfiguration cfg)
    {
        _http = http;
        _cfg = cfg;
    }

    private string OssBase => _cfg["Aps:OssBase"]!;
    private string Region => _cfg["Aps:Region"] ?? "US";

    public async Task EnsureBucketAsync(string token, string bucketKey)
    {
        var url = $"{OssBase}/buckets";
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Content = new StringContent(
            JsonSerializer.Serialize(new { bucketKey, policyKey = "persistent" }),
            Encoding.UTF8,
            "application/json"
        );

        using var res = await _http.SendAsync(req);
        var body = await res.Content.ReadAsStringAsync();

        // If bucket exists, APS returns 409. We treat it as OK.
        if (res.IsSuccessStatusCode) return;
        if ((int)res.StatusCode == 409) return;

        throw new Exception($"Create bucket failed: {res.StatusCode} {body}");
    }

    public async Task<(string uploadKey, string s3Url)> GetSignedS3UploadAsync(string token, string bucketKey, string objectKey)
    {
        var url = $"{OssBase}/buckets/{bucketKey}/objects/{objectKey}/signeds3upload";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var res = await _http.SendAsync(req);
        var body = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode) throw new Exception($"Get signeds3upload failed: {res.StatusCode} {body}");

        using var doc = JsonDocument.Parse(body);
        var uploadKey = doc.RootElement.GetProperty("uploadKey").GetString()!;
        var s3Url = doc.RootElement.GetProperty("urls")[0].GetString()!;
        return (uploadKey, s3Url);
    }

    public async Task UploadToS3Async(string s3Url, Stream fileStream)
    {
        // IMPORTANT: This PUT is to Amazon S3 URL, no Bearer token.
        using var req = new HttpRequestMessage(HttpMethod.Put, s3Url);
        req.Content = new StreamContent(fileStream);

        using var res = await _http.SendAsync(req);
        var body = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode) throw new Exception($"S3 upload failed: {res.StatusCode} {body}");
    }

    public async Task CompleteSignedS3UploadAsync(string token, string bucketKey, string objectKey, string uploadKey)
    {
        var url = $"{OssBase}/buckets/{bucketKey}/objects/{objectKey}/signeds3upload";
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Content = new StringContent(JsonSerializer.Serialize(new { uploadKey }), Encoding.UTF8, "application/json");

        using var res = await _http.SendAsync(req);
        var body = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode) throw new Exception($"Complete upload failed: {res.StatusCode} {body}");
    }

    public async Task<string> CreateSignedUrlAsync(string token, string bucketKey, string objectKey, string access /*read|write*/, int minutes = 180)
    {
        var url = $"{OssBase}/buckets/{bucketKey}/objects/{objectKey}/signed?access={access}&useCdn=true&region={Region}";
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Content = new StringContent(
            JsonSerializer.Serialize(new { minutesExpiration = minutes, singleUse = false }),
            Encoding.UTF8,
            "application/json"
        );

        using var res = await _http.SendAsync(req);
        var body = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode) throw new Exception($"Create signed url failed: {res.StatusCode} {body}");

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("signedUrl").GetString()!;
    }
}