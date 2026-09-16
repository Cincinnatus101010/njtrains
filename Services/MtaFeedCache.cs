using Microsoft.Extensions.Options;
using NjTrains.Web.Options;
using TransitRealtime;

namespace NjTrains.Web.Services;

/// <summary>Caches merged MTA GTFS-RT entities so vehicle + departure parsing share one fetch.</summary>
public sealed class MtaFeedCache(
    IHttpClientFactory httpClientFactory,
    IOptions<MtaOptions> options,
    ILogger<MtaFeedCache> log)
{
    private readonly MtaOptions _options = options.Value;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private IReadOnlyList<FeedEntity> _entities = [];
    private DateTime _fetchedAtUtc = DateTime.MinValue;

    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(12);

    public async Task<IReadOnlyList<FeedEntity>> GetEntitiesAsync(CancellationToken cancellationToken = default)
    {
        if (DateTime.UtcNow - _fetchedAtUtc < CacheTtl && _entities.Count > 0)
        {
            return _entities;
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (DateTime.UtcNow - _fetchedAtUtc < CacheTtl && _entities.Count > 0)
            {
                return _entities;
            }

            var http = httpClientFactory.CreateClient(nameof(MtaSubwayService));
            var batches = await Task.WhenAll(
                MtaFeedUrls.SubwayRealtime.Select(url => FetchFeedAsync(http, url, cancellationToken)));

            _entities = batches.SelectMany(b => b).ToList();
            _fetchedAtUtc = DateTime.UtcNow;
            return _entities;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<IReadOnlyList<FeedEntity>> FetchFeedAsync(
        HttpClient http,
        string url,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == System.Net.HttpStatusCode.Forbidden && !_options.HasApiKey)
                {
                    log.LogWarning("MTA feed returned 403; configure Mta:ApiKey if required.");
                }
                else
                {
                    log.LogDebug("MTA feed {Url} returned {Status}", url, (int)response.StatusCode);
                }

                return [];
            }

            var feed = FeedMessage.Parser.ParseFrom(await response.Content.ReadAsByteArrayAsync(cancellationToken));
            return feed.Entity;
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Failed MTA feed {Url}", url);
            return [];
        }
    }
}
