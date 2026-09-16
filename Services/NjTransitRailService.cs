using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using NjTrains.Web.Models;
using NjTrains.Web.Options;

namespace NjTrains.Web.Services;

public sealed class NjTransitRailService(
    IHttpClientFactory httpClientFactory,
    RailDataTokenService tokens,
    NjRailStopLookup stops,
    IOptions<NjTransitOptions> options,
    ILogger<NjTransitRailService> log)
{
    private readonly NjTransitOptions _options = options.Value;

    public string? LastTokenIssue => tokens.LastError;

    public async Task<IReadOnlyList<SubwayMapMarker>> GetLiveTrainsAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.IsConfigured)
        {
            return [];
        }

        var token = await tokens.GetTokenAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(token))
        {
            if (!string.IsNullOrWhiteSpace(tokens.LastError))
            {
                log.LogDebug("NJ Transit: no token ({Reason}).", tokens.LastError);
            }

            return [];
        }

        try
        {
            var http = httpClientFactory.CreateClient(nameof(NjTransitRailService));
            using var form = new MultipartFormDataContent
            {
                { new StringContent(token), "token" },
            };

            var url = $"{_options.ApiBaseUrl.TrimEnd('/')}/getVehicleData";
            using var response = await http.PostAsync(url, form, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                log.LogWarning("NJ Transit getVehicleData HTTP {Status}", (int)response.StatusCode);
                return [];
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                log.LogWarning("NJ Transit getVehicleData returned non-array payload.");
                return [];
            }

            var list = new List<SubwayMapMarker>();
            foreach (var row in doc.RootElement.EnumerateArray())
            {
                var marker = ParseVehicle(row);
                if (marker is not null)
                {
                    list.Add(marker);
                }
            }

            log.LogDebug("NJ Transit live trains: {Count}", list.Count);
            return list;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "NJ Transit getVehicleData failed");
            return [];
        }
    }

    private SubwayMapMarker? ParseVehicle(JsonElement v)
    {
        var trainLine = GetString(v, "TRAIN_LINE");
        if (!NjRailRoutes.TryRouteFromApiLine(trainLine, out var route))
        {
            return null;
        }

        var trainNumber = GetString(v, "ID");
        trainNumber = string.IsNullOrWhiteSpace(trainNumber) ? null : trainNumber.Trim();
        var nextStop = GetString(v, "NEXT_STOP");
        nextStop = string.IsNullOrWhiteSpace(nextStop) ? null : nextStop.Trim();
        var anchorStopId = stops.GetAnchorStopId(nextStop);

        if (!TryResolveLocation(v, nextStop, out var latitude, out var longitude))
        {
            return null;
        }

        var lateMin = ParseLateMinutes(GetString(v, "SEC_LATE"));
        var status = StatusLabel(lateMin);
        var inMotion = nextStop is not null || lateMin > 0;
        var label = !string.IsNullOrWhiteSpace(nextStop) ? nextStop : status;

        return new SubwayMapMarker(
            Id: $"njt-{trainNumber ?? Guid.NewGuid().ToString("N")[..8]}",
            Route: route,
            Label: label,
            Latitude: latitude,
            Longitude: longitude,
            Color: NjRailRoutes.ColorFor(route),
            StopId: anchorStopId,
            StopName: nextStop,
            AnchorStopId: anchorStopId,
            Status: status,
            InMotion: inMotion,
            Network: "njt",
            MotionMode: "stop",
            TrainNumber: trainNumber);
    }

    private bool TryResolveLocation(JsonElement v, string? nextStop, out double latitude, out double longitude)
    {
        if (TryParseCoordinate(v, "LATITUDE", out latitude) && TryParseCoordinate(v, "LONGITUDE", out longitude)
            && (Math.Abs(latitude) >= 0.01 || Math.Abs(longitude) >= 0.01))
        {
            return true;
        }

        if (stops.TryGetCoordinates(nextStop, out latitude, out longitude))
        {
            return true;
        }

        latitude = 0;
        longitude = 0;
        return false;
    }

    private static bool TryParseCoordinate(JsonElement row, string property, out double value)
    {
        value = 0;
        if (!row.TryGetProperty(property, out var el))
        {
            return false;
        }

        return el.ValueKind switch
        {
            JsonValueKind.String => double.TryParse(el.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value),
            JsonValueKind.Number => el.TryGetDouble(out value),
            _ => false,
        };
    }

    private static string? GetString(JsonElement row, string property)
    {
        if (!row.TryGetProperty(property, out var el))
        {
            return null;
        }

        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Number => el.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    private static string StatusLabel(int lateMin) => lateMin switch
    {
        <= 0 => "Between stations",
        1 => "1 min late",
        _ => $"{lateMin} min late",
    };

    private static int ParseLateMinutes(string? secLate)
    {
        if (!int.TryParse(secLate, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sec))
        {
            return 0;
        }

        return Math.Max(0, sec / 60);
    }
}
