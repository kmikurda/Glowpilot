using System.Net;
using Glowpilot.Service;
using Glowpilot.Shared;

// Load configuration from the platform config file (or use defaults if absent/malformed).
var config = ConfigLoader.Load();

var builder = WebApplication.CreateBuilder(args);

// Bind only on localhost — defence-in-depth against exposure on external interfaces.
builder.WebHost.UseUrls($"http://localhost:{config.HttpPort}");

// Core services
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ISessionMapConfig>(config);
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
// Returns current Session Map, Ring State, and Device connection state.
// Application-level localhost guard — Kestrel is also bound to localhost only.
app.MapGet("/status", (HttpContext context, SessionMap map, IDeviceWriter device) =>
{
    // Null remoteIp occurs only in the in-memory test transport; production Kestrel always sets it.
    // Kestrel is also configured to bind localhost-only (UseUrls), so this is defence-in-depth.
    var remoteIp = context.Connection.RemoteIpAddress;
    if (remoteIp is not null && !IPAddress.IsLoopback(remoteIp))
        return Results.StatusCode(403);

    var sessions  = map.GetAll();
    var ringFrame = map.ComputeRingState();
    var response  = new StatusResponse
    {
        Sessions        = sessions,
        RingState       = ringFrame.State,
        DeviceConnected = device.IsConnected
    };
    return Results.Ok(response);
});

// Multi-instance protection ───────────────────────────────────────────────────
// Kestrel throws IOException when the port is already bound by another instance.
try
{
    app.Run();
}
catch (IOException)
{
    Console.Error.WriteLine(
        $"Another instance is already running on port {config.HttpPort}. Exiting.");
    Environment.Exit(0);
}

// Required for WebApplicationFactory in integration tests.
public partial class Program { }
