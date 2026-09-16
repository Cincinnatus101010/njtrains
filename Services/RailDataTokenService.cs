using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using NjTrains.Web.Options;

namespace NjTrains.Web.Services;

/// <summary>
/// Caches RailData tokens (~24h). NJ Transit limits getToken to ~10 calls/day per account.
/// </summary>
public sealed class RailDataTokenService(
    IHttpClientFactory httpClientFactory,
    IWebHostEnvironment env,
    IOptions<NjTransitOptions> options,
    ILogger<RailDataTokenService> log)
{
    private readonly NjTransitOptions _options = options.Value;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private string? _cachedToken;
    private DateTimeOffset _expiresAt = DateTimeOffset.MinValue;
    private DateTimeOffset _backoffUntil = DateTimeOffset.MinValue;
    private string? _lastError;
    private bool _diskCacheLoaded;

    /// <summary>Reason the last token fetch failed (rate limit, credentials, etc.).</summary>
    public string? LastError => _lastError;

    public async Task<string?> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.IsConfigured)
        {
            return null;
        }

        EnsureDiskCacheLoaded();

        if (_cachedToken is not null && DateTimeOffset.UtcNow < _expiresAt)
        {
            return _cachedToken;
        }

        if (DateTimeOffset.UtcNow < _backoffUntil)
        {
            return _cachedToken;
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            EnsureDiskCacheLoaded();

            if (_cachedToken is not null && DateTimeOffset.UtcNow < _expiresAt)
            {
                return _cachedToken;
            }

            if (DateTimeOffset.UtcNow < _backoffUntil)
            {
                return _cachedToken;
            }

            var http = httpClientFactory.CreateClient(nameof(RailDataTokenService));
            using var form = new MultipartFormDataContent
            {
                { new StringContent(_options.Username), "username" },
                { new StringContent(_options.Password), "password" },
            };

            using var response = await http.PostAsync(_options.TokenUrl, form, cancellationToken);
            var body = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken);
            if (!response.IsSuccessStatusCode || body is null || string.IsNullOrWhiteSpace(body.UserToken))
            {
                var detail = body?.ErrorMessage;
                ApplyFailureBackoff(detail, (int)response.StatusCode);
                return _cachedToken;
            }

            _cachedToken = body.UserToken.Trim();
            _expiresAt = DateTimeOffset.UtcNow.AddHours(23);
            _backoffUntil = DateTimeOffset.MinValue;
            _lastError = null;
            SaveDiskCache();
            log.LogInformation("NJ Transit token ready (cached until ~{Expires:u}).", _expiresAt);
            return _cachedToken;
        }
        finally
        {
            _lock.Release();
        }
    }

    private void ApplyFailureBackoff(string? detail, int httpStatus)
    {
        _lastError = !string.IsNullOrWhiteSpace(detail)
            ? detail
            : $"HTTP {httpStatus}";

        if (detail?.Contains("Daily usage limit", StringComparison.OrdinalIgnoreCase) == true)
        {
            _backoffUntil = DateTimeOffset.UtcNow.Date.AddDays(1);
            log.LogWarning(
                "NJ Transit getToken daily limit reached; will not call getToken again until {Retry:u}. Use cached token if available.",
                _backoffUntil);
            return;
        }

        _backoffUntil = DateTimeOffset.UtcNow.AddMinutes(15);
        log.LogWarning("NJ Transit getToken failed: {Detail}. Retrying after {Retry:u}.", _lastError, _backoffUntil);
    }

    private string CacheFilePath =>
        Path.Combine(env.ContentRootPath, "Data", ".raildata-token.json");

    private void EnsureDiskCacheLoaded()
    {
        if (_diskCacheLoaded)
        {
            return;
        }

        _diskCacheLoaded = true;
        var path = CacheFilePath;
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(path);
            var entry = JsonSerializer.Deserialize<DiskCacheEntry>(json);
            if (entry?.Token is null || entry.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                return;
            }

            _cachedToken = entry.Token;
            _expiresAt = entry.ExpiresAt;
            log.LogDebug("NJ Transit token loaded from local cache (expires ~{Expires:u}).", _expiresAt);
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Could not read NJ Transit token cache file.");
        }
    }

    private void SaveDiskCache()
    {
        if (_cachedToken is null)
        {
            return;
        }

        try
        {
            var path = CacheFilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var json = JsonSerializer.Serialize(new DiskCacheEntry(_cachedToken, _expiresAt));
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Could not write NJ Transit token cache file.");
        }
    }

    private sealed record DiskCacheEntry(string Token, DateTimeOffset ExpiresAt);

    private sealed class TokenResponse
    {
        [JsonPropertyName("UserToken")]
        public string? UserToken { get; set; }

        [JsonPropertyName("Authenticated")]
        public string? Authenticated { get; set; }

        [JsonPropertyName("errorMessage")]
        public string? ErrorMessage { get; set; }
    }
}
