using NjTrains.Web.Models;

namespace NjTrains.Web.Services;

public sealed class MapStationIndex
{
    private readonly Lazy<IReadOnlyList<StationEntry>> _stations;

    public MapStationIndex(IWebHostEnvironment env, ILogger<MapStationIndex> log)
    {
        _stations = new Lazy<IReadOnlyList<StationEntry>>(() => Load(env, log));
    }

    public IReadOnlyList<MapStation> AllStations() =>
        _stations.Value
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .Select(ToMapStation)
            .ToList();

    public IReadOnlyList<MapStation> StationsForNetwork(string network) =>
        _stations.Value
            .Where(s => s.Network.Equals(network, StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .Select(ToMapStation)
            .ToList();

    private static MapStation ToMapStation(StationEntry s) =>
        new(s.Id, s.Name, s.Network, s.Lat, s.Lon);

    private sealed record StationEntry(string Id, string Name, string Network, double Lat, double Lon);

    private static IReadOnlyList<StationEntry> Load(IWebHostEnvironment env, ILogger log)
    {
        var list = new List<StationEntry>();
        LoadMta(env, list);
        LoadNj(env, list);
        var nj = list.Count(s => s.Network == "njt");
        var mta = list.Count(s => s.Network == "mta");
        if (nj == 0)
        {
            log.LogWarning("NJ Rail station index is empty; check Data/njt-stops.txt or nj-rail-stops.geojson.");
        }

        log.LogDebug("Station index: {Mta} subway, {Nj} NJ Rail.", mta, nj);
        return list;
    }

    private static void LoadMta(IWebHostEnvironment env, List<StationEntry> list)
    {
        var path = Path.Combine(env.ContentRootPath, "Data", "mta-stops.txt");
        if (!File.Exists(path))
        {
            return;
        }

        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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

            var locationType = parts.Length > 4 ? parts[4].Trim() : "";
            if (locationType != "1")
            {
                continue;
            }

            var id = parts[0].Trim();
            var name = parts[1].Trim();
            if (!seenNames.Add(name))
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

            list.Add(new StationEntry(id, name, "mta", lat, lon));
        }
    }

    private static void LoadNj(IWebHostEnvironment env, List<StationEntry> list)
    {
        var byId = new Dictionary<string, StationEntry>(StringComparer.OrdinalIgnoreCase);

        var txtPath = Path.Combine(env.ContentRootPath, "Data", "njt-stops.txt");
        if (File.Exists(txtPath))
        {
            MergeNjFromCsv(byId, txtPath);
        }

        var geoPath = Path.Combine(env.WebRootPath, "data", "nj-rail-stops.geojson");
        if (File.Exists(geoPath))
        {
            MergeNjFromGeoJson(byId, geoPath);
        }

        list.AddRange(byId.Values);
    }

    private static void MergeNjFromCsv(Dictionary<string, StationEntry> byId, string path)
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

            var id = parts[0].Trim();
            var name = parts[1].Trim();
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
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

            byId[id] = new StationEntry(id, name, "njt", lat, lon);
        }
    }

    private static void MergeNjFromGeoJson(Dictionary<string, StationEntry> byId, string path)
    {
        using var stream = File.OpenRead(path);
        using var doc = System.Text.Json.JsonDocument.Parse(stream);
        foreach (var feat in doc.RootElement.GetProperty("features").EnumerateArray())
        {
            var props = feat.GetProperty("properties");
            var id = props.GetProperty("stop_id").GetString() ?? "";
            var name = props.GetProperty("name").GetString() ?? "";
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var coords = feat.GetProperty("geometry").GetProperty("coordinates");
            var lon = coords[0].GetDouble();
            var lat = coords[1].GetDouble();
            byId.TryAdd(id, new StationEntry(id, name, "njt", lat, lon));
        }
    }
}
