using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using static SIPSorcery.net.RTP.Packetisation.MJPEGPacketiser;

Log.Logger = new LoggerConfiguration()
    .Enrich.FromLogContext()
    .Enrich.WithThreadId()
    .WriteTo.Async(a => a.Console(
        outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz}] [{Level:u3}] ({ThreadId}) {Message:lj}{NewLine}{Exception}"))
    .WriteTo.Async(a => a.File($"{AppDomain.CurrentDomain.BaseDirectory}/logs/log.txt", rollingInterval: RollingInterval.Day, rollOnFileSizeLimit: true))
    .CreateLogger();


HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

builder.Services
    .AddLogging(loggingBuilder =>
    {
        loggingBuilder.AddSerilog();

    })
    .AddSingleton<SipWebRtcGateway>();

IHost host = builder.Build();


try
{
    Log.Logger.Information("Starting SIP-WebRTC Gateway with Video Support...");

    // Create and start the gateway
    var gateway = host.Services.GetRequiredService<SipWebRtcGateway>();

    // Handle Ctrl+C gracefully
    Console.CancelKeyPress += (sender, e) =>
    {
        e.Cancel = true;
        Log.Logger.Information("Shutting down gateway...");
        gateway.Stop();
        Environment.Exit(0);
    };

    // Start the gateway
    await gateway.Start();
    Console.WriteLine("SIP-WebRTC Gateway is running. Press Ctrl+C to stop.");
    await Console.In.ReadLineAsync();
}
catch (Exception ex)
{
    Log.Logger.Error(ex, "Fatal error occurred");
    Console.WriteLine($"Fatal error: {ex.Message}");
    Environment.Exit(1);
}