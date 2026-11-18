var builder = WebApplication.CreateBuilder(args);
var serviceSettings = builder.Configuration["ServiceSettings"];
if (!string.IsNullOrWhiteSpace(serviceSettings))
{
    builder.Configuration.AddJsonFile(serviceSettings, optional: false, reloadOnChange: false)
        .AddEnvironmentVariables().AddCommandLine(args);
}
var serviceName = builder.Configuration["ServiceName"] ?? "Cherished Photos";

builder.Services.AddWindowsService(options =>
    options.ServiceName = serviceName);
if (OperatingSystem.IsWindows())
{
    builder.Services.Configure<Microsoft.Extensions.Logging.EventLog.EventLogSettings>(options =>
    {
        // The options callback executes outside the registration-time platform guard.
        if (OperatingSystem.IsWindows())
        {
            options.LogName = "Application";
            options.SourceName = serviceName;
        }
    });
}

builder.Services.AddOptions<PoolSettings>()
    .Bind(builder.Configuration.GetSection("PoolSettings"))
    .Validate(PoolSettings.IsValid, "Pool folder/filter are required; slideshow interval must be 250..86400000 ms and refresh interval 1000..86400000 ms.")
    .ValidateOnStart();

builder.Services.AddControllers();
builder.Services.AddRazorPages();
builder.Services.AddHostedService<PoolService>();
builder.Services.AddSingleton<Pool>();
builder.Services.AddSingleton<HomeAssistantState>();
builder.Services.AddHostedService<HomeAssistantBridge>();

builder.WebHost.ConfigureKestrel((context, options) =>
{
    options.Configure(context.Configuration.GetSection("Kestrel"));
});


var app = builder.Build();

app.UseStaticFiles();

app.UseRouting();

app.UseAuthorization();

app.MapRazorPages();
app.MapControllers();

app.Run();
