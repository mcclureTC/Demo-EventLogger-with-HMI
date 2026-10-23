using System.Text.Json.Serialization;
using EventLoggerDemo.Hmi;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<PlcOptions>(builder.Configuration.GetSection("Plc"));
builder.Services.AddSingleton<PlcGateway>();
builder.Services.AddSingleton<EventLoggerGateway>();
builder.Services.Configure<BrokerOptions>(builder.Configuration.GetSection("Broker"));
builder.Services.AddSingleton<BrokerClient>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<BrokerClient>());
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var app = builder.Build();

app.UseDefaultFiles();   // serves wwwroot/index.html at /
app.UseStaticFiles();

var api = app.MapGroup("/api");

// PLC (ADS, GVL_Hmi)
api.MapGet("/status", (PlcGateway plc) => plc.ReadStatusAsync());
api.MapPost("/requests", (RequestBody body, PlcGateway plc) => plc.SendRequestAsync(body));
api.MapPut("/oven-temperature", (TemperatureBody body, PlcGateway plc) =>
    plc.SetOvenTemperatureAsync(Math.Clamp(body.Value, 0, 150)));

// EventLogger
api.MapGet("/alarms", (EventLoggerGateway logger) => Guard(logger.GetActiveAlarms));
api.MapGet("/events", (EventLoggerGateway logger) => Guard(logger.GetLoggedEvents));
api.MapPost("/alarms/confirm", (AlarmKey key, EventLoggerGateway logger) =>
    Guard(() => logger.Confirm(key)
        ? new RequestResult(true, "Alarm confirmed.")
        : new RequestResult(false, "That alarm is no longer active.")));
api.MapPost("/alarms/confirm-all", (EventLoggerGateway logger) =>
    Guard(() =>
    {
        logger.ConfirmAll();
        return new RequestResult(true, "All alarms confirmed.");
    }));

// MQTT broker (what the PLC published)
api.MapGet("/broker", (BrokerClient broker) => broker.GetView());

var url = (builder.Configuration["Urls"] ?? "http://localhost:5000").Split(';')[0];

// Open the page only once the server is actually listening.
app.Lifetime.ApplicationStarted.Register(() =>
{
    app.Logger.LogInformation("Event Logger Demo HMI is running at {Url}", url);
    if (builder.Configuration.GetValue("OpenBrowser", true))
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            app.Logger.LogWarning("Could not open a browser ({Message}). Open {Url} by hand.", ex.Message, url);
        }
    }
});

app.Run();

// The EventLogger being unreachable is reported to the page rather than thrown as a 500.
static IResult Guard<T>(Func<T> action)
{
    try
    {
        return Results.Ok(action());
    }
    catch (Exception ex)
    {
        return Results.Problem(detail: ex.Message, title: "EventLogger not reachable", statusCode: 503);
    }
}
