namespace NjTrains.Web.Models;

public sealed record MapStation(
    string Id,
    string Name,
    string Network,
    double Latitude,
    double Longitude)
{
    public string Key => $"{Network}:{Id}";
}
