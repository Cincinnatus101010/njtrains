namespace NjTrains.Web.Options;

public sealed class NjTransitOptions
{
    public const string SectionName = "NjTransit";

    /// <summary>Production RailData API, e.g. getVehicleDataJSON.</summary>
    public string ApiBaseUrl { get; set; } = "https://raildata.njtransit.com/api/TrainData/";

    public string TokenUrl { get; set; } = "https://raildata.njtransit.com/api/TrainData/getToken";

    public string Username { get; set; } = "";

    public string Password { get; set; } = "";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Username) && !string.IsNullOrWhiteSpace(Password);
}
