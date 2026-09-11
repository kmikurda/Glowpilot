using System.Net;
using Glowpilot.Service;
using Glowpilot.Shared;

var builder = WebApplication.CreateBuilder(args);

// Core services
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ISessionMapConfig>(_ => new SessionMapConfig(10));
builder.Services.AddSingleton<SessionMap>();

// Device writer chain: DebounceWriter(SerialDeviceWriter)
builder.Services.AddSingleton<IDeviceWriter, SerialDeviceWriter>();
builder.Services.AddSingleton<DebounceWriter>(sp =>
    new DebounceWriter(sp.GetRequiredService<IDeviceWriter>()));

// Session service — also registered as a hosted service so background timers start/stop with the host
builder.Services.AddSingleton<SessionService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SessionService>());

var app = builder.Build();

// POST /event ─────────────────────────────────────────────────────────────────
// Accepts a Session Event, updates Session Map, drives Device Writer.
app.MapPost("/event", (SessionEvent evt, SessionService service) =>
{
    service.HandleEvent(evt);
    return Results.Ok();
});

// GET /status ─────────────────────────────────────────────────────────────────
// Returns the current Session Map as JSON. Localhost only.
app.MapGet("/status", (HttpContext context, SessionMap map) =>
{
    var remoteIp = context.Connection.RemoteIpAddress;
    if (remoteIp is null || !IPAddress.IsLoopback(remoteIp))
        return Results.Forbid();

    return Results.Ok(map.GetAll());
});

app.Run("http://localhost:57491");

// Required for WebApplicationFactory in integration tests.
public partial class Program { }
