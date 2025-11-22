using System.Net.Http.Json;
using System.Text.Json;
using OpsDashboard.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddEnvironmentVariables();

builder.Services.AddRazorPages();
builder.Services.Configure<DashboardOptions>(builder.Configuration.GetSection("Dashboard"));
builder.Services.AddHttpClient("engine");
builder.Services.AddSingleton<HealthClient>();
builder.Services.AddSingleton<MetricsClient>();
builder.Services.AddSingleton<MetricsParser>();

var app = builder.Build();

app.UseStaticFiles();
app.UseRouting();
app.MapRazorPages();

app.Run();
