namespace NjTrains.Web.Models;

public static class NjRailRoutes
{
    public static readonly IReadOnlyList<(string Route, string Label, string Color)> All =
    [
        ("NEC", "Northeast Corridor", "#DD3439"),
        ("NJCL", "North Jersey Coast", "#03A3DF"),
        ("MNE", "Morris & Essex", "#08A652"),
        ("MNEG", "Gladstone", "#A4C9AA"),
        ("BNTN", "Montclair-Boonton", "#E66859"),
        ("MNBN", "Main / Bergen", "#FFD411"),
        ("PASC", "Pascack Valley", "#94219A"),
        ("RARV", "Raritan Valley", "#F2A537"),
        ("ATLC", "Atlantic City", "#075AAA"),
    ];

    private static readonly Dictionary<string, string> ColorsByRoute =
        All.ToDictionary(r => r.Route, r => r.Color, StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, string> ApiLineToRoute =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Northeast Corridor Line"] = "NEC",
            ["North Jersey Coast Line"] = "NJCL",
            ["Morris & Essex Line"] = "MNE",
            ["Gladstone Branch"] = "MNEG",
            ["Montclair-Boonton Line"] = "BNTN",
            ["Main Line"] = "MNBN",
            ["Bergen County Line"] = "MNBN",
            ["Pascack Valley Line"] = "PASC",
            ["Raritan Valley Line"] = "RARV",
            ["Atlantic City Line"] = "ATLC",
        };

    public static HashSet<string> AllIds() =>
        new(All.Select(r => r.Route), StringComparer.OrdinalIgnoreCase);

    public static string ColorFor(string routeId) =>
        ColorsByRoute.TryGetValue(routeId, out var color) ? color : "#666666";

    public static bool TryRouteFromApiLine(string? trainLine, out string route)
    {
        route = "";
        if (string.IsNullOrWhiteSpace(trainLine))
        {
            return false;
        }

        var line = trainLine.Trim();
        if (ApiLineToRoute.TryGetValue(line, out route!))
        {
            return true;
        }

        foreach (var (apiName, routeId) in ApiLineToRoute)
        {
            if (line.Contains(apiName, StringComparison.OrdinalIgnoreCase)
                || apiName.Contains(line, StringComparison.OrdinalIgnoreCase))
            {
                route = routeId;
                return true;
            }
        }

        if (line.Contains("Northeast", StringComparison.OrdinalIgnoreCase))
        {
            route = "NEC";
            return true;
        }

        if (line.Contains("North Jersey Coast", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Coast Line", StringComparison.OrdinalIgnoreCase))
        {
            route = "NJCL";
            return true;
        }

        if (line.Contains("Morris", StringComparison.OrdinalIgnoreCase)
            && line.Contains("Essex", StringComparison.OrdinalIgnoreCase))
        {
            route = "MNE";
            return true;
        }

        if (line.Contains("Gladstone", StringComparison.OrdinalIgnoreCase))
        {
            route = "MNEG";
            return true;
        }

        if (line.Contains("Montclair", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Boonton", StringComparison.OrdinalIgnoreCase))
        {
            route = "BNTN";
            return true;
        }

        if (line.Contains("Bergen", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Main Line", StringComparison.OrdinalIgnoreCase))
        {
            route = "MNBN";
            return true;
        }

        if (line.Contains("Pascack", StringComparison.OrdinalIgnoreCase))
        {
            route = "PASC";
            return true;
        }

        if (line.Contains("Raritan", StringComparison.OrdinalIgnoreCase))
        {
            route = "RARV";
            return true;
        }

        if (line.Contains("Atlantic City", StringComparison.OrdinalIgnoreCase))
        {
            route = "ATLC";
            return true;
        }

        return false;
    }
}
