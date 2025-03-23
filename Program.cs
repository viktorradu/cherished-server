var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<PoolSettings>(builder.Configuration.GetSection("PoolSettings"));

builder.Services.AddControllers();
builder.Services.AddRazorPages();
builder.Services.AddHostedService<PoolService>();
builder.Services.AddSingleton<Pool>();

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
