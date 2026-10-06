using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using System.Text.Json;
using System.Text.Json.Serialization;
using ToolsServer.Shared;
using System.Globalization;
using System.Text;

namespace ToolsServer.Pages;

public partial class Weather
{
    [CascadingParameter] public MainLayout Layout { get; set; }
    [Inject] private HttpClient Http { get; set; }
    [Inject] private IJSRuntime JS { get; set; }

    [Parameter, SupplyParameterFromQuery(Name = "city")] public string CityFromQuery { get; set; } // query string binding

    private string cityQuery = ""; // default only
    private bool useFahrenheit = false;
    private bool isLoading = false;

    private List<GeoResult> geocodeResults = new();
    private GeoResult? selectedLocation;

    private List<HourlyPoint> hourlyForecast = new();
    private Dictionary<DateTime, List<HourlyPoint>> groupedDailyForecast => hourlyForecast
        .GroupBy(g => g.Time.Date)
        .OrderBy(o => o.Key)
        .ToDictionary(k => k.Key, v => v.OrderBy(o => o.Time).ToList());

    private Dictionary<DateTime, SunriseSunset> dailySunData = new();
    private bool attemptedAutoLocate = false;

    protected override async Task OnInitializedAsync()
    {
        Layout.Title = "Weather";
        if (!string.IsNullOrWhiteSpace(CityFromQuery))
        {
            cityQuery = CityFromQuery;
            attemptedAutoLocate = true; // skip auto locate if city provided
            _ = SearchCity(); // fire and forget populate
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || attemptedAutoLocate) return;
        attemptedAutoLocate = true;
        try
        {
            isLoading = true; StateHasChanged();
            var pos = await JS.InvokeAsync<BrowserPosition?>("weather.getLocation");
            if (pos is not null)
            {
                var rev = await ReverseGeocodeAsync(pos.latitude, pos.longitude);
                if (rev != null)
                {
                    selectedLocation = rev;
                    cityQuery = rev.DisplayName; // actual resolved (already normalized)
                }
                else
                {
                    selectedLocation = new GeoResult 
                    { 
                        Id = 0, 
                        Name = cityQuery, 
                        Country = string.Empty, 
                        Latitude = pos.latitude, 
                        Longitude = pos.longitude 
                    };
                }
                geocodeResults = new() { selectedLocation };
                await LoadWeather();
                StateHasChanged();
                return;
            }
            isLoading = false; StateHasChanged();
        }
        catch 
        {
            isLoading = false; StateHasChanged();
        }
        await SearchCity();
    }

    private async Task<GeoResult?> ReverseGeocodeAsync(double lat, double lon)
    {
        var latStr = lat.ToString(CultureInfo.InvariantCulture);
        var lonStr = lon.ToString(CultureInfo.InvariantCulture);

        // 1. Try Open-Meteo reverse
        try
        {
            var url = $"https://geocoding-api.open-meteo.com/v1/reverse?latitude={latStr}&longitude={lonStr}&language=en&count=1";
            var reverse = await Http.GetFromJsonAsync<ReverseGeocodeResponse>(url);
            var item = reverse?.Results?.FirstOrDefault();
            if (item != null)
            {
                return new GeoResult
                {
                    Id = item.Id,
                    Name = NormalizeNameToEnglish(item.Name),
                    Country = NormalizeNameToEnglish(item.Country),
                    Latitude = item.Latitude,
                    Longitude = item.Longitude,
                };
            }
        }
        catch { }

        // 2. Fallback: Nominatim (OpenStreetMap)
        try
        {
            var nominatimUrl = $"https://nominatim.openstreetmap.org/reverse?format=jsonv2&lat={latStr}&lon={lonStr}&zoom=10&addressdetails=1&accept-language=en";
            using var req = new HttpRequestMessage(HttpMethod.Get, nominatimUrl);
            req.Headers.UserAgent.ParseAdd("ToolsServerWeather/1.0 (+https://example.invalid)");
            var resp = await Http.SendAsync(req);
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadAsStringAsync();
                var nominatim = JsonSerializer.Deserialize<NominatimReverseResult>(json);
                var name = nominatim?.Address?.City ?? nominatim?.Address?.Town ?? nominatim?.Address?.Village ?? nominatim?.Address?.County;
                var country = nominatim?.Address?.Country;
                if (!string.IsNullOrWhiteSpace(name))
                {
                    return new GeoResult 
                    { 
                        Id = 0, 
                        Name = NormalizeNameToEnglish(name), 
                        Country = NormalizeNameToEnglish(country ?? string.Empty), 
                        Latitude = lat, 
                        Longitude = lon 
                    };
                }
            }
        }
        catch { }

        return null;
    }

    private async Task SearchCity()
    {
        if (string.IsNullOrWhiteSpace(cityQuery)) return;
        isLoading = true; StateHasChanged();
        var geo = await Http.GetFromJsonAsync<GeocodingResponse>($"https://geocoding-api.open-meteo.com/v1/search?count=5&language=en&name={Uri.EscapeDataString(cityQuery)}");
        geocodeResults = geo?.Results?.Select(r => new GeoResult
        {
            Id = r.Id,
            Name = NormalizeNameToEnglish(r.Name),
            Country = NormalizeNameToEnglish(r.Country),
            Latitude = r.Latitude,
            Longitude = r.Longitude
        }).ToList() ?? new();

        selectedLocation = geocodeResults.FirstOrDefault();
        if (selectedLocation is not null)
        {
            cityQuery = selectedLocation.DisplayName;
            await LoadWeather();
        }
        isLoading = false; StateHasChanged();
    }

    private async Task OnLocationChange(ChangeEventArgs e)
    {
        var idStr = e.Value?.ToString();
        if (int.TryParse(idStr, out var id))
        {
            selectedLocation = geocodeResults.FirstOrDefault(x => x.Id == id);
            if (selectedLocation is not null)
            {
                cityQuery = selectedLocation.DisplayName;
                await LoadWeather();
            }
        }
    }

    private async Task LoadWeather()
    {
        if (selectedLocation is null) return;
        isLoading = true; StateHasChanged();

        var tempUnit = useFahrenheit ? "fahrenheit" : "celsius";
        var hourlyParams = "temperature_2m,apparent_temperature,dew_point_2m,relative_humidity_2m,wind_speed_10m,wind_direction_10m,precipitation_probability";
        var url = $"https://api.open-meteo.com/v1/forecast?latitude={selectedLocation.Latitude.ToString(CultureInfo.InvariantCulture)}&longitude={selectedLocation.Longitude.ToString(CultureInfo.InvariantCulture)}&hourly={hourlyParams}&daily=sunrise,sunset&temperature_unit={tempUnit}&forecast_days=5&timezone=auto";
        WeatherResponse? data = null;
        try { data = await Http.GetFromJsonAsync<WeatherResponse>(url); } catch { }
        if (data?.Hourly?.Time == null) { isLoading = false; StateHasChanged(); return; }

        var now = DateTime.Now;
        hourlyForecast.Clear();
        for (int i = 0; i < data.Hourly.Time.Count; i++)
        {
            if (!DateTime.TryParse(data.Hourly.Time[i], out var parsed)) continue;
            if (parsed < now) continue;

            var point = new HourlyPoint
            {
                Time = parsed,
                Temperature = data.Hourly.Temperature2M?.ElementAtOrDefault(i) ?? 0,
                FeelsLike = data.Hourly.ApparentTemperature?.ElementAtOrDefault(i) ?? 0,
                DewPoint = data.Hourly.DewPoint2M?.ElementAtOrDefault(i) ?? 0,
                Humidity = (int)(data.Hourly.RelativeHumidity2M?.ElementAtOrDefault(i) ?? 0),
                WindSpeed = data.Hourly.WindSpeed10M?.ElementAtOrDefault(i) ?? 0,
                WindDirection = data.Hourly.WindDirection10M?.ElementAtOrDefault(i) ?? 0,
                PrecipProbability = (int)(data.Hourly.PrecipitationProbability?.ElementAtOrDefault(i) ?? 0)
            };
            point.WindDirectionText = DegToCardinal(point.WindDirection);
            point.WindDirectiondeg = $"{point.WindDirection}deg";
            point.Description = GetDescription(point.Temperature, useFahrenheit);
            point.IconClass = GetIconClass(point.Time, point.Description);
            point.HumidityBucket = (int)Math.Clamp(Math.Round(point.Humidity / 20.0, MidpointRounding.AwayFromZero), 0, 5);
            if (point.Time.Minute == 0 && (point.Time.Hour % 3) == 0)
                hourlyForecast.Add(point);
        }

        dailySunData = new();
        if (data.Daily?.Sunrise is not null)
        {
            for (int i = 0; i < data.Daily.Sunrise.Count; i++)
            {
                if (DateTime.TryParse(data.Daily.Sunrise[i], out var sunrise) && DateTime.TryParse(data.Daily.Sunset.ElementAtOrDefault(i), out var sunset))
                    dailySunData[sunrise.Date] = new SunriseSunset { Sunrise = sunrise, Sunset = sunset };
            }
        }

        isLoading = false; StateHasChanged();
    }

    private string DegToCardinal(double deg)
    {
        string[] dirs = {"N","NE","E","SE","S","SW","W","NW"};
        return dirs[(int)Math.Round((deg % 360) / 45) % 8];
    }
    private string GetIconClass(DateTime time, string desc)
    {
        bool night = time.Hour < 6 || time.Hour >= 20;
        if (desc.Contains("Freezing") || desc.Contains("Cold")) return night?"fa-solid fa-snowflake":"fa-regular fa-snowflake";
        if (desc.Contains("Hot")) return night?"fa-solid fa-moon":"fa-solid fa-sun";
        if (desc.Contains("Warm")) return night?"fa-solid fa-cloud-moon":"fa-solid fa-cloud-sun";
        if (desc.Contains("Cool")) return night?"fa-solid fa-cloud-moon-rain":"fa-solid fa-cloud";
        return night?"fa-solid fa-moon":"fa-solid fa-sun";
    }
    private string GetDescription(double temp, bool fahrenheit)
    {
        double celsius = fahrenheit ? (temp - 32) * 5 / 9 : temp;
        if (celsius < 0) return "Freezing"; if (celsius < 10) return "Cold"; if (celsius < 20) return "Cool"; if (celsius < 28) return "Warm"; return "Hot";
    }
    private async Task ChangeUnits(bool f) { if (useFahrenheit == f) return; useFahrenheit = f; await LoadWeather(); }

    // Typeahead support
    private List<GeoResult> suggestionResults = new();
    private bool showSuggestions = false;
    private bool canSearchExplicit => cityQuery?.Length >= 3;
    private System.Timers.Timer _debounceTimer;

    private void OnCityInput(ChangeEventArgs e)
    {
        cityQuery = e.Value?.ToString() ?? string.Empty;
        if (_debounceTimer == null)
        {
            _debounceTimer = new System.Timers.Timer(500); // 500ms debounce
            _debounceTimer.Elapsed += async (s, args) =>
            {
                _debounceTimer.Stop();
                await InvokeAsync(async () => await PerformSuggestionSearch());
            };
            _debounceTimer.AutoReset = false;
        }
        _debounceTimer.Stop();
        if (cityQuery.Length >= 3)
        {
            _debounceTimer.Start();
        }
        else
        {
            suggestionResults.Clear();
            showSuggestions = false;
        }
    }

    private async Task PerformSuggestionSearch()
    {
        try
        {
            var geo = await Http.GetFromJsonAsync<GeocodingResponse>($"https://geocoding-api.open-meteo.com/v1/search?count=7&language=en&name={Uri.EscapeDataString(cityQuery)}");
            suggestionResults = geo?.Results?.Select(r => new GeoResult
            {
                Id = r.Id,
                Name = NormalizeNameToEnglish(r.Name),
                Country = NormalizeNameToEnglish(r.Country),
                Latitude = r.Latitude,
                Longitude = r.Longitude
            }).ToList() ?? new();
            showSuggestions = suggestionResults.Any();
        }
        catch
        {
            suggestionResults = new();
            showSuggestions = false;
        }
        StateHasChanged();
    }

    private async Task SelectSuggestion(GeoResult g)
    {
        cityQuery = g.DisplayName;
        selectedLocation = g;
        geocodeResults = new() { g };
        showSuggestions = false;
        await LoadWeather();
    }

    private async Task ExplicitSearch()
    {
        await SearchCity();
    }

    // --- Normalization / Transliteration ---
    private string NormalizeNameToEnglish(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        if (!ContainsGreek(name)) return name; // already Latin
        // Basic Greek -> Latin mapping
        var map = greekMap;
        var sb = new StringBuilder();
        foreach (var ch in name)
        {
            if (map.TryGetValue(ch, out var repl)) sb.Append(repl); else sb.Append(ch);
        }
        // Title case simple
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(sb.ToString().ToLowerInvariant());
    }

    private bool ContainsGreek(string s) => s.Any(c => (c >= '\u0370' && c <= '\u03FF') || (c >= '\u1F00' && c <= '\u1FFF'));

    private static readonly Dictionary<char, string> greekMap = new()
    {
        ['Á'] = "A", ['á'] = "a", ['Â'] = "V", ['â'] = "v", ['Ã'] = "G", ['ã'] = "g", ['Ä'] = "D", ['ä'] = "d",
        ['Å'] = "E", ['å'] = "e", ['Æ'] = "Z", ['æ'] = "z", ['Ç'] = "I", ['ç'] = "i", ['È'] = "Th", ['è'] = "th",
        ['É'] = "I", ['é'] = "i", ['Ú'] = "I", ['ú'] = "i", ['Ê'] = "K", ['ê'] = "k", ['Ë'] = "L", ['ë'] = "l",
        ['Ì'] = "M", ['ì'] = "m", ['Í'] = "N", ['í'] = "n", ['Î'] = "X", ['î'] = "x", ['Ï'] = "O", ['ï'] = "o",
        ['Ð'] = "P", ['ð'] = "p", ['Ñ'] = "R", ['ñ'] = "r", ['Ó'] = "S", ['ó'] = "s", ['ò'] = "s", ['Ô'] = "T", ['ô'] = "t",
        ['Õ'] = "Y", ['õ'] = "y", ['Û'] = "Y", ['û'] = "y", ['Ö'] = "F", ['ö'] = "f", ['×'] = "Ch", ['÷'] = "ch",
        ['Ø'] = "Ps", ['ø'] = "ps", ['Ù'] = "O", ['ù'] = "o"
    };

    // DTOs
    private class GeocodingResponse { [JsonPropertyName("results")] public List<GeoApiResult> Results { get; set; } }
    private class ReverseGeocodeResponse { [JsonPropertyName("results")] public List<GeoApiResult> Results { get; set; } }
    private class GeoApiResult { [JsonPropertyName("id")] public int Id { get; set; } [JsonPropertyName("name")] public string Name { get; set; } [JsonPropertyName("country")] public string Country { get; set; } [JsonPropertyName("latitude")] public double Latitude { get; set; } [JsonPropertyName("longitude")] public double Longitude { get; set; } }
    private class WeatherResponse { [JsonPropertyName("hourly")] public HourlyResponse Hourly { get; set; } [JsonPropertyName("daily")] public DailyResponse Daily { get; set; } }
    private class HourlyResponse { [JsonPropertyName("time")] public List<string> Time { get; set; } [JsonPropertyName("temperature_2m")] public List<double>? Temperature2M { get; set; } [JsonPropertyName("apparent_temperature")] public List<double>? ApparentTemperature { get; set; } [JsonPropertyName("dew_point_2m")] public List<double>? DewPoint2M { get; set; } [JsonPropertyName("relative_humidity_2m")] public List<double>? RelativeHumidity2M { get; set; } [JsonPropertyName("wind_speed_10m")] public List<double>? WindSpeed10M { get; set; } [JsonPropertyName("wind_direction_10m")] public List<double>? WindDirection10M { get; set; } [JsonPropertyName("precipitation_probability")] public List<double>? PrecipitationProbability { get; set; } }
    private class DailyResponse { [JsonPropertyName("time")] public List<string> Time { get; set; } [JsonPropertyName("sunrise")] public List<string> Sunrise { get; set; } [JsonPropertyName("sunset")] public List<string> Sunset { get; set; } }
    private class GeoResult { public int Id { get; set; } public string Name { get; set; } public string Country { get; set; } public double Latitude { get; set; } public double Longitude { get; set; } public string DisplayName => string.IsNullOrWhiteSpace(Country) ? Name : $"{Name}, {Country}"; }
    private class HourlyPoint { public DateTime Time { get; set; } public double Temperature { get; set; } public double FeelsLike { get; set; } public double DewPoint { get; set; } public int Humidity { get; set; } public double WindSpeed { get; set; } public double WindDirection { get; set; } public string WindDirectionText { get; set; } public string WindDirectiondeg { get; set; } public string Description { get; set; } public string IconClass { get; set; } public int HumidityBucket { get; set; } public int PrecipProbability { get; set; } }
    private class SunriseSunset { public DateTime Sunrise { get; set; } public DateTime Sunset { get; set; } }
    private class BrowserPosition { public double latitude { get; set; } public double longitude { get; set; } }
    private class NominatimReverseResult { [JsonPropertyName("address")] public NominatimAddress Address { get; set; } }
    private class NominatimAddress { [JsonPropertyName("city")] public string City { get; set; } [JsonPropertyName("town")] public string Town { get; set; } [JsonPropertyName("village")] public string Village { get; set; } [JsonPropertyName("county")] public string County { get; set; } [JsonPropertyName("country")] public string Country { get; set; } }
}
