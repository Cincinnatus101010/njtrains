using Microsoft.Extensions.Options;
using NjTrains.Web.Components;
using NjTrains.Web.Options;
using NjTrains.Web.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<MtaOptions>(options =>
{
    builder.Configuration.GetSection(MtaOptions.SectionName).Bind(options);
    var fromEnv = Environment.GetEnvironmentVariable("MTA_API_KEY");
    if (!string.IsNullOrWhiteSpace(fromEnv))
    {
        options.ApiKey = fromEnv;
    }
});

builder.Services.Configure<NjTransitOptions>(options =>
{
    builder.Configuration.GetSection(NjTransitOptions.SectionName).Bind(options);
    var user = Environment.GetEnvironmentVariable("NJTRANSIT_USERNAME");
    var pass = Environment.GetEnvironmentVariable("NJTRANSIT_PASSWORD");
    if (!string.IsNullOrWhiteSpace(user))
    {
        options.Username = user;
    }

    if (!string.IsNullOrWhiteSpace(pass))
    {
        options.Password = pass;
    }
});

builder.Services.AddHttpClient(nameof(RailDataTokenService), client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddSingleton<RailDataTokenService>();

builder.Services.AddHttpClient(nameof(MtaSubwayService), (sp, client) =>
{
    client.Timeout = TimeSpan.FromSeconds(20);
    var key = sp.GetRequiredService<IOptions<MtaOptions>>().Value.ApiKey;
    if (!string.IsNullOrWhiteSpace(key))
    {
        client.DefaultRequestHeaders.Add("x-api-key", key);
    }
});

builder.Services.AddHttpClient(nameof(NjTransitRailService), client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddSingleton<MtaStopLookup>();
builder.Services.AddSingleton<NjRailStopLookup>();
builder.Services.AddSingleton<MapStationIndex>();
builder.Services.AddSingleton<MtaFeedCache>();
builder.Services.AddSingleton<MtaSubwayService>();
builder.Services.AddSingleton<TripPlannerService>();
builder.Services.AddSingleton<NjTransitRailService>();
builder.Services.AddRazorComponents().AddInteractiveServerComponents();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();
