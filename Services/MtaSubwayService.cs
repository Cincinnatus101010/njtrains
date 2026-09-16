using NjTrains.Web.Models;
using TransitRealtime;

namespace NjTrains.Web.Services;

public sealed class MtaSubwayService(MtaFeedCache feeds, MtaStopLookup stopLookup, ILogger<MtaSubwayService> log)
{
    public async Task<IReadOnlyList<SubwayMapMarker>> GetLiveTrainsAsync(CancellationToken cancellationToken = default)
    {
        var entities = await feeds.GetEntitiesAsync(cancellationToken);
        var byId = new Dictionary<string, SubwayMapMarker>(StringComparer.Ordinal);
        foreach (var entity in entities)
        {
            var train = ParseVehicle(entity, stopLookup);
            if (train is not null)
            {
                byId[train.Id] = train;
            }
        }

        var list = byId.Values
            .OrderBy(t => t.Route, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.Id)
            .ToList();

        log.LogDebug("MTA live trains: {Count}", list.Count);
        return list;
    }

    private static SubwayMapMarker? ParseVehicle(FeedEntity entity, MtaStopLookup stops)
    {
        if (entity.Vehicle is not { } vehicle)
        {
            return null;
        }

        var route = RouteId(vehicle.Trip?.RouteId);
        if (route == "?")
        {
            route = RouteId(entity.TripUpdate?.Trip?.RouteId);
        }

        var rawStopId = string.IsNullOrWhiteSpace(vehicle.StopId) ? null : vehicle.StopId.Trim();
        var anchorStopId = stops.GetAnchorStopId(rawStopId);

        if (!TryResolveLocation(vehicle, stops, anchorStopId, rawStopId, out var latitude, out var longitude))
        {
            return null;
        }

        var trainId = vehicle.Vehicle?.Id ?? entity.Id;
        var stopId = rawStopId;
        var stopName = stops.GetStopName(stopId) ?? stops.GetStopName(anchorStopId);
        var status = StatusLabel(vehicle.CurrentStatus);
        var inMotion = vehicle.CurrentStatus is
            VehiclePosition.Types.VehicleStopStatus.IncomingAt
            or VehiclePosition.Types.VehicleStopStatus.InTransitTo;
        var label = !string.IsNullOrWhiteSpace(stopName) ? stopName : status;

        return new SubwayMapMarker(
            Id: $"train-{trainId}",
            Route: route,
            Label: label,
            Latitude: latitude,
            Longitude: longitude,
            Color: SubwayRoutes.ColorFor(route),
            StopId: stopId,
            StopName: stopName,
            AnchorStopId: anchorStopId,
            Status: status,
            InMotion: inMotion);
    }

    private static bool TryResolveLocation(
        VehiclePosition vehicle,
        MtaStopLookup stops,
        string? anchorStopId,
        string? rawStopId,
        out double latitude,
        out double longitude)
    {
        if (!string.IsNullOrWhiteSpace(rawStopId) && stops.TryGetCoordinates(rawStopId, out latitude, out longitude))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(anchorStopId)
            && stops.TryGetCoordinates(anchorStopId, out latitude, out longitude))
        {
            return true;
        }

        if (vehicle.Position is { } pos
            && (Math.Abs(pos.Latitude) >= 0.01 || Math.Abs(pos.Longitude) >= 0.01))
        {
            latitude = pos.Latitude;
            longitude = pos.Longitude;
            return true;
        }

        latitude = 0;
        longitude = 0;
        return false;
    }

    private static string RouteId(string? routeId) =>
        string.IsNullOrWhiteSpace(routeId) ? "?" : routeId.Trim().ToUpperInvariant();

    private static string StatusLabel(VehiclePosition.Types.VehicleStopStatus status) => status switch
    {
        VehiclePosition.Types.VehicleStopStatus.IncomingAt => "Approaching",
        VehiclePosition.Types.VehicleStopStatus.StoppedAt => "At platform",
        VehiclePosition.Types.VehicleStopStatus.InTransitTo => "Between stations",
        _ => "En route",
    };
}
