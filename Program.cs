using PrinterCommunicate;

var builder = Host.CreateApplicationBuilder(args);

// This ensures Windows can stop, start, and manage the lifecycle natively via service manager
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "ScannerProxy";
});

builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();