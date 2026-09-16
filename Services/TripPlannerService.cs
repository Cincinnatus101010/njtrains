using System.Text.Json;
using NjTrains.Web.Models;

namespace NjTrains.Web.Services;

public sealed class TripPlannerService(IWebHostEnvironment env, ILogger<TripPlannerService> log)
{
    private readonly Lazy<Graph?> _graph = new(() => Load(env, log));

    public bool IsAvailable => _graph.Value is not null;

    public PlannedRoute? Plan(string fromKey, string toKey)
    {
        var graph = _graph.Value;
        if (graph is null
            || !graph.Nodes.ContainsKey(fromKey)
            || !graph.Nodes.ContainsKey(toKey))
        {
            return null;
        }

        if (fromKey.Equals(toKey, StringComparison.OrdinalIgnoreCase))
        {
            var node = graph.Nodes[fromKey];
            return new PlannedRoute(
                [new RouteStep("stay", null, node.Name, node.Name, null)],
                [[node.Lon, node.Lat]],
                1);
        }

        var prev = new Dictionary<string, (string From, string Route)>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        queue.Enqueue(fromKey);
        prev[fromKey] = (fromKey, "");

        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            if (cur == toKey)
            {
                break;
            }

            if (!graph.Adjacency.TryGetValue(cur, out var edges))
            {
                continue;
            }

            foreach (var edge in edges)
            {
                if (prev.ContainsKey(edge.To))
                {
                    continue;
                }

                prev[edge.To] = (cur, edge.Route);
                queue.Enqueue(edge.To);
            }
        }

        if (!prev.ContainsKey(toKey))
        {
            return null;
        }

        var pathNodes = new List<string>();
        var pathEdges = new List<string>();
        for (var at = toKey; at != fromKey;)
        {
            pathNodes.Add(at);
            var (from, route) = prev[at];
            pathEdges.Add(route);
            at = from;
        }

        pathNodes.Add(fromKey);
        pathNodes.Reverse();
        pathEdges.Reverse();

        var steps = BuildSteps(graph, pathNodes, pathEdges);
        var coords = pathNodes
            .Select(k => graph.Nodes[k])
            .Select(n => new[] { n.Lon, n.Lat })
            .ToList();

        return new PlannedRoute(steps, coords, pathNodes.Count);
    }

    private static IReadOnlyList<RouteStep> BuildSteps(Graph graph, List<string> nodes, List<string> edgeRoutes)
    {
        var steps = new List<RouteStep>();
        if (nodes.Count < 2)
        {
            return steps;
        }

        var segStart = 0;
        for (var i = 0; i < edgeRoutes.Count; i++)
        {
            var route = edgeRoutes[i];
            var nextRoute = i + 1 < edgeRoutes.Count ? edgeRoutes[i + 1] : null;
            if (nextRoute != null && string.Equals(route, nextRoute, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var fromNode = graph.Nodes[nodes[segStart]];
            var toNode = graph.Nodes[nodes[i + 1]];
            var kind = route.Equals("walk", StringComparison.OrdinalIgnoreCase) ? "walk" : "ride";
            steps.Add(new RouteStep(
                kind,
                kind == "ride" ? route : null,
                fromNode.Name,
                toNode.Name,
                kind == "ride" ? LiveMapRoutes.ColorFor(route) : null));

            segStart = i + 1;
        }

        return steps;
    }

    private sealed class Graph
    {
        public required Dictionary<string, Node> Nodes { get; init; }
        public required Dictionary<string, List<Edge>> Adjacency { get; init; }
    }

    private sealed record Node(string Name, double Lat, double Lon, string Network);

    private sealed record Edge(string To, string Route);

    private static Graph? Load(IWebHostEnvironment env, ILogger log)
    {
        var path = Path.Combine(env.ContentRootPath, "Data", "transit-graph.json");
        if (!File.Exists(path))
        {
            log.LogWarning("Trip planner graph missing at {Path}. Run scripts/build-transit-graph.py.", path);
            return null;
        }

        try
        {
            using var stream = File.OpenRead(path);
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;
            var nodes = new Dictionary<string, Node>(StringComparer.Ordinal);
            foreach (var prop in root.GetProperty("nodes").EnumerateObject())
            {
                var o = prop.Value;
                nodes[prop.Name] = new Node(
                    o.GetProperty("name").GetString() ?? prop.Name,
                    o.GetProperty("lat").GetDouble(),
                    o.GetProperty("lon").GetDouble(),
                    o.GetProperty("network").GetString() ?? "mta");
            }

            var adj = new Dictionary<string, List<Edge>>(StringComparer.Ordinal);
            foreach (var e in root.GetProperty("edges").EnumerateArray())
            {
                var from = e.GetProperty("from").GetString()!;
                var to = e.GetProperty("to").GetString()!;
                var route = e.GetProperty("route").GetString() ?? "?";
                if (!adj.TryGetValue(from, out var list))
                {
                    list = [];
                    adj[from] = list;
                }

                list.Add(new Edge(to, route));
            }

            log.LogDebug("Trip planner loaded {Nodes} nodes.", nodes.Count);
            return new Graph { Nodes = nodes, Adjacency = adj };
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Failed to load trip planner graph.");
            return null;
        }
    }
}
