namespace NjTrains.Web.Options;

public sealed class MtaOptions
{
    public const string SectionName = "Mta";

    public string ApiKey { get; set; } = "";

    public bool HasApiKey => !string.IsNullOrWhiteSpace(ApiKey);
}
