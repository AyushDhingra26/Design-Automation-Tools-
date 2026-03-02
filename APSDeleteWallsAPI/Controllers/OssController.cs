using APSDeleteWallsAPI.Services;
using Microsoft.AspNetCore.Mvc;

namespace APSDeleteWallsAPI.Controllers;

[ApiController]
[Route("oss")]
public class OssController : ControllerBase
{
    private readonly OssService _oss;
    private readonly ApsAuthService _auth;
    private readonly IConfiguration _cfg;

    public OssController(OssService oss, ApsAuthService auth, IConfiguration cfg)
    {
        _oss = oss;
        _auth = auth;
        _cfg = cfg;
    }

    // ✅ 2-legged token (no session)
    private async Task<string> GetTokenAsync()
    {
        var clientId = _cfg["APS:ClientId"]!;
        var clientSecret = _cfg["APS:ClientSecret"]!;
        return await _auth.Get2LeggedTokenOnlyAsync(clientId, clientSecret);
    }

    // ---------------------------
    // 1) Ensure bucket
    // ---------------------------
    [HttpPost("bucket/ensure")]
    public async Task<IActionResult> EnsureBucket([FromBody] EnsureBucketDto dto)
    {
        try
        {
            var token = await GetTokenAsync();
            await _oss.EnsureBucketAsync(token, dto.bucketKey);
            return Ok(new { ok = true, bucketKey = dto.bucketKey });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { message = ex.Message });
        }
    }

    public record EnsureBucketDto(string bucketKey);

    // ---------------------------
    // 2) Upload input RVT and return INPUT signed GET URL
    // ---------------------------
    [HttpPost("upload-revit")]
    [RequestSizeLimit(1024L * 1024L * 1024L)] // 1GB (adjust)
    public async Task<IActionResult> UploadRevit([FromForm] UploadRevitDto dto)
    {
        try
        {
            if (dto.file == null || dto.file.Length == 0)
                return BadRequest("No file uploaded.");

            var token = await GetTokenAsync();

            await _oss.EnsureBucketAsync(token, dto.bucketKey);

            var objectKey = string.IsNullOrWhiteSpace(dto.objectKey)
                ? dto.file.FileName
                : dto.objectKey;

            // 1) signed S3 upload
            var (uploadKey, s3Url) = await _oss.GetSignedS3UploadAsync(token, dto.bucketKey, objectKey);

            // 2) upload binary to S3 url[0]
            using (var stream = dto.file.OpenReadStream())
                await _oss.UploadToS3Async(s3Url, stream);

            // 3) complete
            await _oss.CompleteSignedS3UploadAsync(token, dto.bucketKey, objectKey, uploadKey);

            // 4) create signed GET input url
            var inputSignedGet = await _oss.CreateSignedUrlAsync(token, dto.bucketKey, objectKey, "read", 180);

            return Ok(new
            {
                bucketKey = dto.bucketKey,
                objectKey,
                inputSignedGet
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { message = ex.Message });
        }
    }

    public class UploadRevitDto
    {
        [FromForm] public string bucketKey { get; set; } = "";
        [FromForm] public string? objectKey { get; set; } // e.g. input1.rvt
        [FromForm] public IFormFile file { get; set; } = default!;
    }

    // ---------------------------
    // 3) Create OUTPUT signed PUT url for DA
    // ---------------------------
    [HttpPost("signed-output")]
    public async Task<IActionResult> CreateOutputSignedPut([FromBody] OutputSignedDto dto)
    {
        try
        {
            var token = await GetTokenAsync();

            await _oss.EnsureBucketAsync(token, dto.bucketKey);

            var signedPut = await _oss.CreateSignedUrlAsync(token, dto.bucketKey, dto.objectKey, "write", 180);

            return Ok(new { outputSignedPut = signedPut });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { message = ex.Message });
        }
    }

    public record OutputSignedDto(string bucketKey, string objectKey);

    // ---------------------------
    // 4) Create OUTPUT signed GET download url
    // ---------------------------
    [HttpPost("signed-download")]
    public async Task<IActionResult> SignedDownload([FromBody] SignedDownloadDto dto)
    {
        try
        {
            var token = await GetTokenAsync();

            var signedGet = await _oss.CreateSignedUrlAsync(token, dto.bucketKey, dto.objectKey, "read", 180);

            return Ok(new { url = signedGet });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { message = ex.Message });
        }
    }

    public record SignedDownloadDto(string bucketKey, string objectKey);
}