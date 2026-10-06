using System.Text.Json;
using System.Text.Json.Serialization;
using System.Web;

namespace ToolsServer.Services.Jarvis.Tools;

public sealed class WeatherTool : IJarvisTool
{
    private readonly HttpClient _http;
    public string Name => "weather";
    public string Description => "Get current weather and short forecast for a city or coordinates. Use when the user asks about weather, temperature, forecast, wind, conditions, or climate for a place.";
    public string JsonSchema => "{\n" +
        "  \"type\": \"object\",\n" +
        "  \"properties\": {\n" +
        "    \"city\": { \"type\": \"string\", \"description\": \"City name with optional country (e.g. 'Kavala, Greece').\" },\n" +
        "    \"latitude\": { \"type\": \"number\", \"description\": \"Latitude if coordinates provided.\" },\n" +
        "    \"longitude\": { \"type\": \"number\", \"description\": \"Longitude if coordinates provided.\" }\n" +
        "  },\n" +
        "  \"required\": [],\n" +
        "  \"additionalProperties\": false\n" +
        "}";

    public WeatherTool(HttpClient http)
    {
        _http = http;
        _http.Timeout = TimeSpan.FromSeconds(15);
    }

    public async Task<string> ExecuteAsync(JsonElement arguments, CancellationToken ct = default)
    {
        try
        {
            double? lat = null, lon = null;
            string? city = null;
            if (arguments.TryGetProperty("city", out var cityProp) && cityProp.ValueKind == JsonValueKind.String)
                city = cityProp.GetString();
            if (arguments.TryGetProperty("latitude", out var latProp) && latProp.ValueKind == JsonValueKind.Number)
                lat = latProp.GetDouble();
            if (arguments.TryGetProperty("longitude", out var lonProp) && lonProp.ValueKind == JsonValueKind.Number)
                lon = lonProp.GetDouble();

            if ((!lat.HasValue || !lon.HasValue) && !string.IsNullOrWhiteSpace(city))
            {
                var geoUrl = "https://geocoding-api.open-meteo.com/v1/search?count=1&language=en&name=" + HttpUtility.UrlEncode(city);
                var geo = await _http.GetFromJsonAsync<GeoResponse>(geoUrl, cancellationToken: ct);
                var g = geo?.Results?.FirstOrDefault();
                if (g != null)
                {
                    lat = g.Latitude; lon = g.Longitude; city = g.Name + (string.IsNullOrWhiteSpace(g.Country)?"":"/"+g.Country);
                }
            }

            if (!lat.HasValue || !lon.HasValue)
            {
                return JsonSerializer.Serialize(new { error = "Could not determine location" });
            }

            var url = $"https://api.open-meteo.com/v1/forecast?latitude={lat.Value}&longitude={lon.Value}&current=temperature_2m,apparent_temperature,relative_humidity_2m,wind_speed_10m,wind_direction_10m,precipitation,weather_code&hourly=temperature_2m,precipitation_probability&forecast_days=1";
            var forecast = await _http.GetFromJsonAsync<ForecastResponse>(url, cancellationToken: ct);
            if (forecast == null)
                return JsonSerializer.Serialize(new { error = "No weather data returned" });

            var current = forecast.Current;
            string dir = DegToCardinal(current.WindDirection10M);
            var summary = new
            {
                location = city ?? $"{lat.Value},{lon.Value}",
                temperature_c = current.Temperature2M,
                apparent_temperature_c = current.ApparentTemperature,
                humidity_percent = current.RelativeHumidity2M,
                wind_speed_kmh = current.WindSpeed10M,
                wind_direction_deg = current.WindDirection10M,
                wind_direction_cardinal = dir,
                precipitation_mm = current.Precipitation,
                condition_code = current.WeatherCode,
                note = "Temperatures in Celsius."
            };
            return JsonSerializer.Serialize(summary);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    private static string DegToCardinal(double deg)
    {
        string[] dirs = {"N","NE","E","SE","S","SW","W","NW"};
        return dirs[(int)Math.Round((deg % 360) / 45) % 8];
    }

    private sealed class GeoResponse { [JsonPropertyName("results")] public List<GeoResult>? Results { get; set; } }
    private sealed class GeoResult { [JsonPropertyName("name")] public string Name { get; set; } = string.Empty; [JsonPropertyName("country")] public string? Country { get; set; } [JsonPropertyName("latitude")] public double Latitude { get; set; } [JsonPropertyName("longitude")] public double Longitude { get; set; } }
    private sealed class ForecastResponse { [JsonPropertyName("current")] public CurrentWeather Current { get; set; } = new(); }
    private sealed class CurrentWeather { [JsonPropertyName("temperature_2m")] public double Temperature2M { get; set; } [JsonPropertyName("apparent_temperature")] public double ApparentTemperature { get; set; } [JsonPropertyName("relative_humidity_2m")] public double RelativeHumidity2M { get; set; } [JsonPropertyName("wind_speed_10m")] public double WindSpeed10M { get; set; } [JsonPropertyName("wind_direction_10m")] public double WindDirection10M { get; set; } [JsonPropertyName("precipitation")] public double Precipitation { get; set; } [JsonPropertyName("weather_code")] public int WeatherCode { get; set; } }
}
