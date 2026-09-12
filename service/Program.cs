using System.Net;
using Glowpilot.Service;
using Glowpilot.Shared;

var builder = WebApplication.CreateBuilder(args);

// Core services
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ISessionMapConfig>(_ => new SessionMapConfig(10));
builder.Services.AddSingleton<SessionMap>();

// USB device discovery — platform-specific descriptor source
if (OperatingSystem.IsWindows())
    builder.Services.AddSingleton<IPortDescriptorSource, WindowsWmiPortDescriptorSource>();
else
    builder.Services.AddSingleton<IPortDescriptorSource, UnixSysfsPortDescriptorSource>();

builder.Services.AddSingleton<IUsbDeviceLocator, UsbDeviceLocator>();

// SerialDeviceWriter is a singleton shared as both IDeviceWriter and ISerialDeviceConnection.
builder.Services.AddSingleton<SerialDeviceWriter>();
builder.Services.AddSingleton<IDeviceWriter>(sp => sp.GetRequiredService<SerialDeviceWriter>());
builder.Services.AddSingleton<ISerialDeviceConnection>(sp => sp.GetRequiredService<SerialDeviceWriter>());

// Device writer chain: DebounceWriter → SerialDeviceWriter
builder.Services.AddSingleton<DebounceWriter>(sp =>
    new DebounceWriter(sp.GetRequiredService<IDeviceWriter>()));

// Session service — also registered as a hosted service so background timers start/stop with the host
builder.Services.AddSingleton<SessionService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SessionService>());

// USB connection manager — polls for device and manages the serial port lifecycle
builder.Services.AddHostedService<DeviceConnectionManager>();

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
