using System.Text.Json;
using System.Text.Json.Serialization;
using System.Web;

namespace ToolsServer.Services.Jarvis.Tools;

// Binance market data + lightweight analytics for LLM reasoning
public class BinanceMarketTool : IJarvisTool
{
    private readonly HttpClient _http;
    public string Name => "binanceMarket";
    public string Description => "Fetch crypto prices, recent OHLCV candles, and identify leading gainers. Use for crypto / bitcoin / ethereum / coin / token / market / price / chart / prediction questions.";
    public string JsonSchema => "{\n" +
        "  \"type\": \"object\",\n" +
        "  \"properties\": {\n" +
        "    \"symbol\": { \"type\": \"string\", \"description\": \"Trading pair symbol like BTCUSDT, ETHUSDT.\" },\n" +
        "    \"interval\": { \"type\": \"string\", \"description\": \"Kline interval (1m,5m,15m,1h,4h,1d) default 1h\" },\n" +
        "    \"limit\": { \"type\": \"integer\", \"description\": \"Number of candles (max 500, default 100)\" },\n" +
        "    \"topGainers\": { \"type\": \"integer\", \"description\": \"If set >0, return that many top 24h percentage gainers (USDT pairs).\" }\n" +
        "  },\n" +
        "  \"additionalProperties\": false\n" +
        "}";

    public BinanceMarketTool(HttpClient http)
    {
        _http = http;
        _http.Timeout = TimeSpan.FromSeconds(15);
        _http.BaseAddress ??= new Uri("https://api.binance.com");
    }

    public async Task<string> ExecuteAsync(JsonElement arguments, CancellationToken ct = default)
    {
        string? symbol = null; string interval = "1h"; int limit = 100; int top = 0;
        if (arguments.TryGetProperty("symbol", out var sym) && sym.ValueKind == JsonValueKind.String) symbol = sym.GetString();
        if (arguments.TryGetProperty("interval", out var ints) && ints.ValueKind == JsonValueKind.String) interval = ints.GetString() ?? interval;
        if (arguments.TryGetProperty("limit", out var lim) && lim.ValueKind == JsonValueKind.Number) limit = Math.Clamp(lim.GetInt32(), 1, 500);
        if (arguments.TryGetProperty("topGainers", out var tg) && tg.ValueKind == JsonValueKind.Number) top = Math.Clamp(tg.GetInt32(), 0, 50);

        var result = new Dictionary<string, object?>();

        if (top > 0)
        {
            try
            {
                var tickers = await _http.GetFromJsonAsync<List<Binance24HTicker>>("/api/v3/ticker/24hr", cancellationToken: ct) ?? new();
                var gainers = tickers.Where(t => t.Symbol.EndsWith("USDT", StringComparison.OrdinalIgnoreCase) && double.TryParse(t.PriceChangePercent, out _))
                                      .OrderByDescending(t => double.Parse(t.PriceChangePercent))
                                      .Take(top)
                                      .Select(t => new {
                                          t.Symbol,
                                          priceChangePercent = double.Parse(t.PriceChangePercent),
                                          lastPrice = double.Parse(t.LastPrice),
                                          volume = double.Parse(t.Volume),
                                          quoteVolume = double.Parse(t.QuoteVolume)
                                      }).ToList();
                result["topGainers"] = gainers;
            }
            catch (Exception ex)
            {
                result["topGainersError"] = ex.Message;
            }
        }

        if (!string.IsNullOrWhiteSpace(symbol))
        {
            try
            {
                var klineUrl = $"/api/v3/klines?symbol={HttpUtility.UrlEncode(symbol)}&interval={interval}&limit={limit}";
                var raw = await _http.GetStringAsync(klineUrl, ct);
                using var doc = JsonDocument.Parse(raw);
                var candles = new List<object>();
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    // Extract basic fields for reasoning + derived metrics
                    var openTime = el[0].GetInt64();
                    double open = double.Parse(el[1].GetString()!);
                    double high = double.Parse(el[2].GetString()!);
                    double low = double.Parse(el[3].GetString()!);
                    double close = double.Parse(el[4].GetString()!);
                    double volume = double.Parse(el[5].GetString()!);
                    double quote = double.Parse(el[7].GetString()!);
                    var change = close - open;
                    var changePct = open == 0 ? 0 : change / open * 100.0;
                    candles.Add(new { openTime, open, high, low, close, volume, quoteVolume = quote, changePct });
                }
                var last = candles.LastOrDefault();
                result["symbol"] = symbol;
                result["interval"] = interval;
                result["candles"] = candles;
                result["latest"] = last;
                if (candles.Count > 1)
                {
                    var closes = candles.Select(c => (double)c.GetType().GetProperty("close")!.GetValue(c)!).ToList();
                    // simple moving averages
                    double SMA(int n) => closes.Count >= n ? closes.Skip(closes.Count - n).Average() : closes.Average();
                    result["sma_5"] = SMA(5);
                    result["sma_20"] = SMA(20);
                }
            }
            catch (Exception ex)
            {
                result["klineError"] = ex.Message;
            }
        }

        return JsonSerializer.Serialize(result, JarvisJson.Options);
    }

    private sealed class Binance24HTicker
    {
        [JsonPropertyName("symbol")] public string Symbol { get; set; } = string.Empty;
        [JsonPropertyName("priceChangePercent")] public string PriceChangePercent { get; set; } = "0";
        [JsonPropertyName("lastPrice")] public string LastPrice { get; set; } = "0";
        [JsonPropertyName("volume")] public string Volume { get; set; } = "0";
        [JsonPropertyName("quoteVolume")] public string QuoteVolume { get; set; } = "0";
    }
}
