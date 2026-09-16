namespace NjTrains.Web.Models;

public static class LiveMapRoutes
{
    public static string ColorFor(string routeId)
    {
        if (SubwayRoutes.AllIds().Contains(routeId))
        {
            return SubwayRoutes.ColorFor(routeId);
        }

        return NjRailRoutes.ColorFor(routeId);
    }
}
