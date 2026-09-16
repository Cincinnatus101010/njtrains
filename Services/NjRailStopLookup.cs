namespace NjTrains.Web.Services;

public sealed class NjRailStopLookup
{
    private readonly Lazy<StopIndex> _index;

    public NjRailStopLookup(IWebHostEnvironment env, ILogger<NjRailStopLookup> log)
    {
        _index = new Lazy<StopIndex>(() => Load(env, log));
    }

    public string? GetAnchorStopId(string? stopName) =>
        string.IsNullOrWhiteSpace(stopName) ? null : Normalize(stopName);

    public bool TryGetCoordinates(string? stopName, out double latitude, out double longitude)
    {
        latitude = 0;
        longitude = 0;
        if (string.IsNullOrWhiteSpace(stopName))
        {
            return false;
        }

        var key = Normalize(stopName);
        var index = _index.Value;
        if (index.ByKey.TryGetValue(key, out var exact))
        {
            latitude = exact.Lat;
            longitude = exact.Lon;
            return true;
        }

        foreach (var (nameKey, coords) in index.ByKey)
        {
            if (nameKey.Contains(key, StringComparison.Ordinal) || key.Contains(nameKey, StringComparison.Ordinal))
            {
                latitude = coords.Lat;
                longitude = coords.Lon;
                return true;
            }
        }

        return false;
    }

    private static string Normalize(string name) =>
        name.Trim().ToUpperInvariant().Replace(" STATION", "", StringComparison.Ordinal);

    private sealed record StopIndex(IReadOnlyDictionary<string, (double Lat, double Lon)> ByKey);

    private static StopIndex Load(IWebHostEnvironment env, ILogger log)
    {
        var map = new Dictionary<string, (double Lat, double Lon)>(StringComparer.Ordinal);
        var path = Path.Combine(env.ContentRootPath, "Data", "njt-stops.txt");
        if (File.Exists(path))
        {
            LoadFromCsv(map, path);
        }

        var geoPath = Path.Combine(env.WebRootPath, "data", "nj-rail-stops.geojson");
        if (File.Exists(geoPath))
        {
            LoadFromGeoJson(map, geoPath);
        }

        if (map.Count == 0)
        {
            log.LogWarning("NJ stops missing (njt-stops.txt and nj-rail-stops.geojson).");
        }
        else
        {
            log.LogDebug("Loaded {Count} NJ rail stops.", map.Count);
        }

        return new StopIndex(map);
    }

    private static void LoadFromCsv(Dictionary<string, (double Lat, double Lon)> map, string path)
    {
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

            var name = parts[1].Trim();
            if (!double.TryParse(parts[2], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var lat)
                || !double.TryParse(parts[3], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var lon))
            {
                continue;
            }

            var key = Normalize(name);
            map.TryAdd(key, (lat, lon));
        }
    }

    private static void LoadFromGeoJson(Dictionary<string, (double Lat, double Lon)> map, string path)
    {
        using var stream = File.OpenRead(path);
        using var doc = System.Text.Json.JsonDocument.Parse(stream);
        foreach (var feat in doc.RootElement.GetProperty("features").EnumerateArray())
        {
            var props = feat.GetProperty("properties");
            var name = props.GetProperty("name").GetString();
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var coords = feat.GetProperty("geometry").GetProperty("coordinates");
            var lon = coords[0].GetDouble();
            var lat = coords[1].GetDouble();
            map.TryAdd(Normalize(name), (lat, lon));
        }
    }
}
