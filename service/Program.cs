using System.Net;
using Glowpilot.Service;
using Glowpilot.Shared;

// File log — the Service runs windowless (no console), so this is where its diagnostics go.
// Created before the config is loaded so a malformed config file is reported too; the file
// itself is only opened once the first entry is written.
var fileLog = new FileLoggerProvider(FileLoggerProvider.DefaultDirectory);
var log = fileLog.CreateLogger("Program");

// A throwing timer callback or background thread would otherwise kill the process without a
// trace — Task Scheduler just starts it again a minute later.
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
    log.LogCritical(e.ExceptionObject as Exception, "Unhandled exception — service is terminating");

// Load configuration from the platform config file (or use defaults if absent/malformed).
var config = ConfigLoader.Load(log);

var builder = WebApplication.CreateBuilder(args);

// Logging: level from config; ASP.NET's per-request chatter stays at Warning. Registered through a
// factory so the container owns (and disposes) the provider.
builder.Logging.SetMinimumLevel(FileLoggerProvider.ParseLevel(config.LogLevel));
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.Services.AddSingleton<ILoggerProvider>(_ => fileLog);

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
app.Logger.LogInformation("Starting Glowpilot service (pid {Pid}) with {Config}", Environment.ProcessId, config);

try
{
    app.Run();
}
catch (IOException)
{
    Console.Error.WriteLine(
        $"Another instance is already running on port {config.HttpPort}. Exiting.");
    log.LogWarning("Another instance is already running on port {Port}. Exiting.", config.HttpPort);
    Environment.Exit(0);
}

// Required for WebApplicationFactory in integration tests.
public partial class Program { }
