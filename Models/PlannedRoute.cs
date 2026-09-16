namespace NjTrains.Web.Models;

public sealed record PlannedRoute(
    IReadOnlyList<RouteStep> Steps,
    IReadOnlyList<double[]> CoordinatesLonLat,
    int StopCount);

public sealed record RouteStep(
    string Kind,
    string? Route,
    string FromName,
    string ToName,
    string? Color);
