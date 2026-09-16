using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Microsoft.Extensions.Options;
using NjTrains.Web.Models;
using NjTrains.Web.Options;
using NjTrains.Web.Services;

namespace NjTrains.Web.Components.Pages;

public partial class SubwayMap : IAsyncDisposable
{
    private enum SearchScope { All, Subway, NjRail }

    [Inject] private MtaSubwayService Subway { get; set; } = default!;
    [Inject] private NjTransitRailService NjRail { get; set; } = default!;
    [Inject] private MapStationIndex StationIndex { get; set; } = default!;
    [Inject] private TripPlannerService TripPlanner { get; set; } = default!;
    [Inject] private IOptions<NjTransitOptions> NjOptions { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;

    private bool _mapReady;
    private bool _panelOpen = true;
    private SearchScope _scope = SearchScope.All;
    private bool _loading;
    private bool _movingOnly;
    private string? _error;
    private string _updatedText = "—";
    private readonly List<SubwayMapMarker> _trains = [];
    private PeriodicTimer? _timer;
    private CancellationTokenSource? _refreshCts;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private string _fromStationKey = "";
    private string _toStationKey = "";
    private PlannedRoute? _plannedRoute;
    private string? _planError;

    private static readonly TimeSpan AutoRefreshInterval = TimeSpan.FromSeconds(5);

    private IReadOnlyList<MapStation>? _mtaStations;
    private IReadOnlyList<MapStation>? _njtStations;

    private IReadOnlyList<MapStation> MtaStations =>
        _mtaStations ??= StationIndex.StationsForNetwork("mta");

    private IReadOnlyList<MapStation> NjStations =>
        _njtStations ??= StationIndex.StationsForNetwork("njt");

    private int NjStationCount => NjStations.Count;

    /// <summary>All live markers on the map (filters apply to the list only).</summary>
    private IReadOnlyList<SubwayMapMarker> MapTrains => _trains;

    private int NjTrainCount => _trains.Count(t => t.Network == "njt");

    private IReadOnlyList<SubwayMapMarker> LiveTrains
    {
        get
        {
            IEnumerable<SubwayMapMarker> query = _trains;
            query = _scope switch
            {
                SearchScope.Subway => query.Where(t => t.Network != "njt"),
                SearchScope.NjRail => query.Where(t => t.Network == "njt"),
                _ => query,
            };

            if (_movingOnly)
            {
                query = query.Where(t => t.InMotion);
            }

            return query.OrderBy(t => t.Route).ThenBy(t => t.StopName ?? t.Id).Take(80).ToList();
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender)
        {
            return;
        }

        var mobile = await JS.InvokeAsync<bool>("subwayMap.isMobile");
        _panelOpen = !mobile;

        await JS.InvokeVoidAsync("subwayMap.init", "subway-map", null);
        _mapReady = true;
        await JS.InvokeVoidAsync("subwayMap.setLayoutPadding", _panelOpen);
        _refreshCts = new CancellationTokenSource();
        _ = RunAutoRefreshAsync(_refreshCts.Token);
        await RefreshAsync(userInitiated: true);
        StateHasChanged();
    }

    private static bool IsLightRouteBadge(string route)
    {
        var r = route.ToUpperInvariant();
        return r is "N" or "Q" or "R" or "W" or "L" or "MNBN" or "MNEG";
    }

    private bool NjRailConfigured() => NjOptions.Value.IsConfigured;

    private static string FormatStop(SubwayMapMarker train) =>
        !string.IsNullOrWhiteSpace(train.StopName) ? train.StopName! : "Station unknown";

    private void OpenPanel() => SetPanelOpen(true);

    private void ClosePanel() => SetPanelOpen(false);

    private void SetPanelOpen(bool open)
    {
        _panelOpen = open;
        _ = InvokeAsync(async () =>
        {
            StateHasChanged();
            await Task.Yield();
            if (_mapReady)
            {
                await JS.InvokeVoidAsync("subwayMap.setLayoutPadding", open);
            }
        });
    }

    private async Task RunAutoRefreshAsync(CancellationToken cancellationToken)
    {
        _timer = new PeriodicTimer(AutoRefreshInterval);
        try
        {
            while (await _timer.WaitForNextTickAsync(cancellationToken))
            {
                await RefreshAsync(userInitiated: false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private Task FocusTrainAsync(string trainId) =>
        JS.InvokeVoidAsync("subwayMap.flyToTrain", trainId).AsTask();

    private Task ResetMapAsync() =>
        JS.InvokeVoidAsync("subwayMap.resetView").AsTask();

    private async Task PlanTripAsync()
    {
        _planError = null;
        _plannedRoute = null;

        if (string.IsNullOrWhiteSpace(_fromStationKey) || string.IsNullOrWhiteSpace(_toStationKey))
        {
            _planError = "Choose both a from and to station.";
            StateHasChanged();
            return;
        }

        if (!TripPlanner.IsAvailable)
        {
            _planError = "Route data missing. Run scripts/build-transit-graph.py.";
            StateHasChanged();
            return;
        }

        _plannedRoute = TripPlanner.Plan(_fromStationKey, _toStationKey);
        if (_plannedRoute is null)
        {
            _planError = "No route found between those stations.";
            await JS.InvokeVoidAsync("subwayMap.clearPlannedRoute");
            StateHasChanged();
            return;
        }

        await JS.InvokeVoidAsync("subwayMap.setPlannedRoute", _plannedRoute.CoordinatesLonLat);
        if (_mapReady && await JS.InvokeAsync<bool>("subwayMap.isMobile"))
        {
            SetPanelOpen(false);
        }

        StateHasChanged();
    }

    private async Task ClearPlanAsync()
    {
        _plannedRoute = null;
        _planError = null;
        _fromStationKey = "";
        _toStationKey = "";
        await JS.InvokeVoidAsync("subwayMap.clearPlannedRoute");
        StateHasChanged();
    }

    private void SwapPlanStations()
    {
        (_fromStationKey, _toStationKey) = (_toStationKey, _fromStationKey);
    }

    private Task ManualRefreshAsync() => RefreshAsync(userInitiated: true);

    private Task SetScopeAsync(SearchScope scope)
    {
        _scope = scope;
        StateHasChanged();
        return Task.CompletedTask;
    }

    private Task ToggleMovingOnlyAsync()
    {
        _movingOnly = !_movingOnly;
        StateHasChanged();
        return Task.CompletedTask;
    }

    private async Task PushMapMarkersAsync()
    {
        if (!_mapReady)
        {
            return;
        }

        await JS.InvokeVoidAsync(
            "subwayMap.updateMarkers",
            MapTrains.Select(MarkerPayload.Create).ToList());
    }

    private async Task RefreshAsync(bool userInitiated)
    {
        if (!_mapReady)
        {
            return;
        }

        if (!await _refreshLock.WaitAsync(0))
        {
            return;
        }

        if (userInitiated)
        {
            _loading = true;
            _error = null;
        }

        try
        {
            var mta = await Subway.GetLiveTrainsAsync();
            var njt = await NjRail.GetLiveTrainsAsync();
            _trains.Clear();
            _trains.AddRange(mta);
            _trains.AddRange(njt);
            _updatedText = DateTime.Now.ToString("t");

            if (njt.Count == 0 && NjRailConfigured())
            {
                var tokenIssue = NjRail.LastTokenIssue;
                _error = tokenIssue?.Contains("Daily usage limit", StringComparison.OrdinalIgnoreCase) == true
                    ? "NJ Transit token limit hit for today (10/day). Trains return tomorrow, or use a cached token after one successful login."
                    : !string.IsNullOrWhiteSpace(tokenIssue)
                        ? $"NJ Transit: {tokenIssue}"
                        : "NJ Transit feed empty — check RailData credentials or try Refresh later.";
            }
            else if (njt.Count > 0 || !NjRailConfigured())
            {
                if (_error?.Contains("NJ Transit", StringComparison.OrdinalIgnoreCase) == true)
                {
                    _error = null;
                }
            }

            await PushMapMarkersAsync();
        }
        catch (Exception)
        {
            _error = "Couldn’t load trains. Tap Refresh to try again.";
        }
        finally
        {
            _refreshLock.Release();
            if (userInitiated)
            {
                _loading = false;
            }

            StateHasChanged();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_refreshCts is not null)
        {
            await _refreshCts.CancelAsync();
            _refreshCts.Dispose();
        }

        _timer?.Dispose();
        _refreshLock.Dispose();
    }

    private sealed record MarkerPayload(
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
        bool InMotion,
        string Network,
        string MotionMode,
        string? TrainNumber)
    {
        public static MarkerPayload Create(SubwayMapMarker m) => new(
            m.Id,
            m.Route,
            m.Label,
            m.Latitude,
            m.Longitude,
            m.Color,
            m.StopId,
            m.StopName,
            m.AnchorStopId,
            m.Status,
            m.InMotion,
            m.Network,
            m.MotionMode,
            m.TrainNumber);

    }
}
