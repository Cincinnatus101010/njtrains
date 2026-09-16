namespace NjTrains.Web.Services;

public sealed class MtaStopLookup
{
    private readonly Lazy<StopData> _data;

    public MtaStopLookup(IWebHostEnvironment env, ILogger<MtaStopLookup> log)
    {
        _data = new Lazy<StopData>(() => Load(env, log));
    }

    /// <summary>Parent station id used for stable map anchoring (ignores N/S platform flicker).</summary>
    public string? GetAnchorStopId(string? stopId)
    {
        if (string.IsNullOrWhiteSpace(stopId))
        {
            return null;
        }

        var id = stopId.Trim();
        return _data.Value.AnchorByStop.TryGetValue(id, out var anchor) ? anchor : id;
    }

    public bool TryGetCoordinates(string? stopId, out double latitude, out double longitude)
    {
        latitude = 0;
        longitude = 0;
        if (!TryGetStop(stopId, out var stop))
        {
            return false;
        }

        latitude = stop.Lat;
        longitude = stop.Lon;
        return true;
    }

    public string? GetStopName(string? stopId) =>
        TryGetStop(stopId, out var stop) ? stop.Name : null;

    private bool TryGetStop(string? stopId, out (double Lat, double Lon, string Name) stop)
    {
        stop = default;
        if (string.IsNullOrWhiteSpace(stopId))
        {
            return false;
        }

        return _data.Value.Stops.TryGetValue(stopId.Trim(), out stop);
    }

    private sealed record StopData(
        IReadOnlyDictionary<string, (double Lat, double Lon, string Name)> Stops,
        IReadOnlyDictionary<string, string> AnchorByStop);

    private static StopData Load(IWebHostEnvironment env, ILogger<MtaStopLookup> log)
    {
        var path = Path.Combine(env.ContentRootPath, "Data", "mta-stops.txt");
        if (!File.Exists(path))
        {
            log.LogWarning("Stops file missing at {Path}.", path);
            return new StopData(
                new Dictionary<string, (double, double, string)>(StringComparer.Ordinal),
                new Dictionary<string, string>(StringComparer.Ordinal));
        }

        var stops = new Dictionary<string, (double Lat, double Lon, string Name)>(StringComparer.Ordinal);
        var anchorByStop = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var line in File.ReadLines(path).Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var parts = line.Split(',');
            if (parts.Length < 4)
            {
                continue;
            }

            var id = parts[0].Trim();
            if (id.Length == 0 || stops.ContainsKey(id))
            {
                continue;
            }

            if (!double.TryParse(parts[2], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var lat)
                || !double.TryParse(parts[3], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var lon))
            {
                continue;
            }

            var name = parts[1].Trim();
            stops[id] = (lat, lon, name);

            var parent = parts.Length > 5 ? parts[5].Trim() : "";
            var anchor = parent.Length > 0 ? parent : id;
            anchorByStop[id] = anchor;
        }

        log.LogDebug("Loaded {Count} MTA stops.", stops.Count);
        return new StopData(stops, anchorByStop);
    }
}
