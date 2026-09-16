namespace NjTrains.Web.Models;

public sealed record SubwayMapMarker(
    string Id,
    string Route,
    string Label,
    double Latitude,
    double Longitude,
    string Color,
    string? StopId,
    string? StopName,
    string? AnchorStopId,
    string Status,
    bool InMotion = false,
    string Network = "mta",
    string MotionMode = "stop",
    string? TrainNumber = null);
