using OpsDashboard.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddEnvironmentVariables("DASHBOARD_");

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
