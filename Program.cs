using FilesClient;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using System.Globalization;
using ToolsServer;
using YoutubeExplode;
using ToolsServer.Services.Jarvis;
using ToolsServer.Services.Jarvis.Tools;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();
builder.Services.AddSignalR();
builder.Services.AddScoped<YoutubeClient>();
// JARVIS: register HttpClient + tools + orchestrator + session
builder.Services.AddHttpClient<JarvisOrchestrator>();
builder.Services.AddHttpClient<HackerNewsTool>();
builder.Services.AddHttpClient<WeatherTool>();
builder.Services.AddHttpClient<GoWeatherTool>();
builder.Services.AddHttpClient<BinanceMarketTool>();
builder.Services.AddSingleton<IJarvisTool, HackerNewsTool>();
builder.Services.AddSingleton<IJarvisTool, WeatherTool>();
builder.Services.AddSingleton<IJarvisTool, GoWeatherTool>();
builder.Services.AddSingleton<IJarvisTool, BinanceMarketTool>();
builder.Services.AddSingleton<JarvisOrchestrator>();
builder.Services.AddScoped<JarvisSession>();

builder.Services.Configure<HubOptions>(options =>
{
    options.MaximumReceiveMessageSize = 2000 * 1024 * 1024;
});
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 2000 * 1024 * 1024;
});

builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 2000 * 1024 * 1024;
});

builder.Services.AddSingleton(builder.Configuration);
builder.Services.AddSingleton(new FilesClientHandler(builder.Configuration["FileService:Token"], builder.Configuration["Host"]));

builder.Services.AddHttpClient();

var cultureInfo = new CultureInfo("en-GB");
CultureInfo.DefaultThreadCurrentCulture = cultureInfo;
CultureInfo.DefaultThreadCurrentUICulture = cultureInfo;
cultureInfo.NumberFormat.NumberDecimalSeparator = ".";

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseStaticFiles();

app.UseRouting();

app.MapBlazorHub();

app.UseEndpoints(endpoints =>
{
    endpoints.MapHub<FileHub>("/fileHub");
});

app.MapFallbackToPage("/_Host");

app.Run();
