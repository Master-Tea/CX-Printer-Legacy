using System.Text.Json;
using CxPrint.Core;
using CxPrint.Service;
using CxPrint.Windows;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Hosting.WindowsServices;

if (args.Length>0 && ServiceSetup.IsCommand(args[0])) return await ServiceSetup.RunAsync(args);
if (args.Length==1 && args[0]=="--list-printers")
{
    Console.WriteLine(JsonSerializer.Serialize(PrinterCatalog.Enumerate(),Product.Json)); return 0;
}
var debugMode=args.Length==1 && args[0]=="--console";
if (args.Length>0 && !debugMode) { Console.Error.WriteLine("不支持的参数。"); return 2; }
if (debugMode)
{
    Paths.UseDebugDirectory(Path.Combine(AppContext.BaseDirectory,"DebugData"));
    Directory.CreateDirectory(Paths.Root);
    if (!File.Exists(Paths.Settings))
        File.WriteAllText(Paths.Settings,JsonSerializer.Serialize(new BridgeSettings
        {
            OwnerSid=System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value,
            Port=18631,AutoStart=false
        },Product.Json));
}

try
{
    var builder=WebApplication.CreateBuilder(new WebApplicationOptions { Args=[],ContentRootPath=AppContext.BaseDirectory });
    builder.Host.UseWindowsService(options=>options.ServiceName=Product.ServiceName);
    builder.Logging.ClearProviders();
    if (debugMode) builder.Logging.AddConsole();
    builder.Logging.AddFilter("Microsoft.AspNetCore",LogLevel.Warning);
    builder.Logging.AddProvider(new FileLog());
    var state=new StateStore();
    builder.WebHost.ConfigureKestrel(options=>
    {
        options.AddServerHeader=false;
        options.Limits.MaxRequestBodySize=Product.MaxDocumentBytes+1024*1024;
        options.Limits.MaxConcurrentConnections=64;
        options.Limits.RequestHeadersTimeout=TimeSpan.FromSeconds(15);
        options.Limits.KeepAliveTimeout=TimeSpan.FromMinutes(2);
        if (debugMode) options.ListenLocalhost(state.Settings.Port,listen=>listen.Protocols=HttpProtocols.Http1);
        else options.ListenAnyIP(state.Settings.Port,listen=>listen.Protocols=HttpProtocols.Http1);
    });
    builder.Services.AddSingleton(state);
    builder.Services.AddSingleton<DigestAuthentication>();
    builder.Services.AddSingleton<IPrintBackend,WindowsPrintBackend>();
    builder.Services.AddSingleton<PrinterMonitor>(); builder.Services.AddHostedService(p=>p.GetRequiredService<PrinterMonitor>());
    builder.Services.AddSingleton<JobManager>(); builder.Services.AddHostedService(p=>p.GetRequiredService<JobManager>());
    builder.Services.AddSingleton<DiscoveryWorker>(); builder.Services.AddHostedService(p=>p.GetRequiredService<DiscoveryWorker>());
    builder.Services.AddHostedService<ControlWorker>();
    builder.Services.AddSingleton<IppEndpoint>();
    var app=builder.Build();
    app.Logger.LogInformation("后台版本 {Version}；CxPrint.Service.dll SHA-256 {Sha256}",ServiceBuild.Version,ServiceBuild.Sha256);
    app.MapPost("/printers/{printerId}",(HttpContext context,IppEndpoint endpoint)=>endpoint.HandleAsync(context));
    app.MapPost("/jobs/{jobId:int}",(HttpContext context,IppEndpoint endpoint)=>endpoint.HandleAsync(context));
    app.MapGet("/",()=>Results.Text("CX Printer Bridge — IPP endpoint: /printers/{id}\n请使用本机 CX Printer 管理共享。","text/plain; charset=utf-8"));
    await app.RunAsync(); return Environment.ExitCode;
}
catch (Exception ex)
{
    using var provider=new FileLog(); var log=provider.CreateLogger("Startup"); log.LogCritical(ex,"打印服务启动失败。");
    Console.Error.WriteLine(ex.Message); return 1; // Non-zero exit lets SCM recovery restart a failed process.
}
