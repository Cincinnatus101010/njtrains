using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using NjTrains.Web.Options;

namespace NjTrains.Web.Services;

/// <summary>
/// Caches RailData tokens (~24h). NJ Transit limits getToken to ~10 calls/day per account.
/// </summary>
public sealed class RailDataTokenService(
    IHttpClientFactory httpClientFactory,
    IOptions<NjTransitOptions> options,
    ILogger<RailDataTokenService> log)
{
    private readonly NjTransitOptions _options = options.Value;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private string? _cachedToken;
    private DateTimeOffset _expiresAt = DateTimeOffset.MinValue;

    public async Task<string?> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.IsConfigured)
        {
            return null;
        }

        if (_cachedToken is not null && DateTimeOffset.UtcNow < _expiresAt)
        {
            return _cachedToken;
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_cachedToken is not null && DateTimeOffset.UtcNow < _expiresAt)
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
                if (!string.IsNullOrWhiteSpace(detail))
                {
                    log.LogWarning("NJ Transit getToken failed: {Detail}", detail);
                }
                else
                {
                    log.LogWarning("NJ Transit getToken failed: HTTP {Status}", (int)response.StatusCode);
                }

                return null;
            }

            _cachedToken = body.UserToken.Trim();
            _expiresAt = DateTimeOffset.UtcNow.AddHours(23);
            log.LogDebug("NJ Transit token refreshed (expires ~{Expires:u}).", _expiresAt);
            return _cachedToken;
        }
        finally
        {
            _lock.Release();
        }
    }

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
