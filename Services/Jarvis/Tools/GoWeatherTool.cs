using System.Text.Json;
using System.Text.Json.Serialization;
using System.Web;

namespace ToolsServer.Services.Jarvis.Tools;

// Secondary weather API (goweather.xyz) for simple current + short forecast by city.
public class GoWeatherTool : IJarvisTool
{
    private readonly HttpClient _http;
    public string Name => "goWeather";
    public string Description => "Fetch quick weather snapshot (temperature, wind, description, short forecast days 1-3) for a city using goweather.xyz. Use as fallback or when user wants a brief summary.";
    public string JsonSchema => "{\n" +
        "  \"type\": \"object\",\n" +
        "  \"properties\": {\n" +
        "    \"city\": { \"type\": \"string\", \"description\": \"City name (e.g. Berlin).\" }\n" +
        "  },\n" +
        "  \"required\": [\"city\"],\n" +
        "  \"additionalProperties\": false\n" +
        "}";

    public GoWeatherTool(HttpClient http)
    {
        _http = http;
        _http.Timeout = TimeSpan.FromSeconds(10);
        _http.BaseAddress ??= new Uri("https://goweather.xyz");
    }

    public async Task<string> ExecuteAsync(JsonElement arguments, CancellationToken ct = default)
    {
        if (!arguments.TryGetProperty("city", out var cityProp) || cityProp.ValueKind != JsonValueKind.String)
            return JsonSerializer.Serialize(new { error = "city required" });
        var city = cityProp.GetString();
        if (string.IsNullOrWhiteSpace(city)) return JsonSerializer.Serialize(new { error = "city required" });
        try
        {
            var endpoint = "/weather/" + HttpUtility.UrlEncode(city);
            var data = await _http.GetFromJsonAsync<GoWeatherResponse>(endpoint, cancellationToken: ct);
            if (data == null) return JsonSerializer.Serialize(new { error = "no data" });
            return JsonSerializer.Serialize(new
            {
                city,
                temperature = data.Temperature,
                wind = data.Wind,
                description = data.Description,
                forecast = data.Forecast?.Select(f => new { day = f.Day, temperature = f.Temperature, wind = f.Wind })
            }, JarvisJson.Options);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    private sealed class GoWeatherResponse
    {
        [JsonPropertyName("temperature")] public string? Temperature { get; set; }
        [JsonPropertyName("wind")] public string? Wind { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("forecast")] public List<GoWeatherForecast>? Forecast { get; set; }
    }

    private sealed class GoWeatherForecast
    {
        [JsonPropertyName("day")] public object? Day { get; set; } // can be number or string per API changes
        [JsonPropertyName("temperature")] public string? Temperature { get; set; }
        [JsonPropertyName("wind")] public string? Wind { get; set; }
    }
}
