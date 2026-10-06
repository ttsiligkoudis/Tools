using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ToolsServer.Services.Jarvis;

public class JarvisOrchestrator
{
    private readonly HttpClient _http;
    private readonly IEnumerable<IJarvisTool> _tools;
    private readonly ILogger<JarvisOrchestrator> _logger;
    private readonly string _model;
    private readonly string _provider; // openai | gemini
    private const int MaxToolExecutionsPerTurn = 1; // prevent looping

    public JarvisOrchestrator(HttpClient httpClient, IEnumerable<IJarvisTool> tools, ILogger<JarvisOrchestrator> logger, IConfiguration config)
    {
        _http = httpClient;
        _tools = tools;
        _logger = logger;
        _model = config["OpenAI:Model"] ?? "gpt-4o"; // still used if provider=openai
        _provider = config["Jarvis:Provider"] ?? (Environment.GetEnvironmentVariable("GOOGLEAI_API_KEY") != null ? "gemini" : "openai");
    }

    public Task<JarvisTurnResult> ChatAsync(List<JarvisMessage> history, CancellationToken ct = default)
        => _provider.Equals("gemini", StringComparison.OrdinalIgnoreCase)
            ? ChatWithGeminiAsync(history, ct)
            : ChatWithOpenAIAsync(history, ct);

    #region OpenAI Implementation
    private async Task<JarvisTurnResult> ChatWithOpenAIAsync(List<JarvisMessage> history, CancellationToken ct)
    {
        var toolsArray = new JsonArray();
        foreach (var t in _tools)
        {
            toolsArray.Add(new JsonObject
            {
                ["type"] = "function",
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["parameters"] = JsonNode.Parse(t.JsonSchema)!
            });
        }

        // Count tool executions for current turn (after latest user msg)
        int lastUserIndex = history.FindLastIndex(h => h.Role == "user");
        int executedToolCount = lastUserIndex >= 0 ? history.Skip(lastUserIndex + 1).Count(h => h.Role == "tool") : 0;

        var sb = new StringBuilder();
        sb.AppendLine("System: You are J.A.R.V.I.S., a concise helpful AI assistant.");
        if (executedToolCount >= MaxToolExecutionsPerTurn)
            sb.AppendLine("System: A tool has already been executed for the latest request. Do NOT call another tool; summarize existing tool results.");
        else
            sb.AppendLine("""System: Available tools: weather (weather queries), hackerNews (tech/startup/news), binanceMarket (crypto prices & market movers), goWeather (quick city weather). To call a tool output exactly: TOOL_CALL:{"name":"toolName","arguments":{...}} as a single line with NO other text. Otherwise answer normally.""");
        foreach (var m in history)
            sb.AppendLine(m.Role + ": " + m.Content);

        var req = new JsonObject
        {
            ["model"] = _model,
            ["input"] = sb.ToString(),
            ["tools"] = toolsArray,
            ["tool_choice"] = executedToolCount >= MaxToolExecutionsPerTurn ? "none" : "auto",
            ["temperature"] = 0.4
        };

        var requestJson = req.ToJsonString();
        _logger.LogInformation("OpenAI request: {Json}", requestJson);

        using var msg = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses");
        msg.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Environment.GetEnvironmentVariable("OPENAI_API_KEY"));

        var response = await _http.SendAsync(msg, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("OpenAI error: {Status} {Body}", response.StatusCode, body);
            return new JarvisTurnResult($"API error {(int)response.StatusCode}: {response.ReasonPhrase}");
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        var text = ExtractAssistantText_OpenAI(root);

        if (TryParseToolDirective(text, out var toolName, out var argumentsJson))
        {
            if (executedToolCount >= MaxToolExecutionsPerTurn || AlreadyExecutedTool(history, toolName))
            {
                var existing = ExtractPriorToolResult(history, toolName);
                var summaryExisting = SummarizeToolResult(toolName, existing) ?? existing ?? "(No data returned)";
                return new JarvisTurnResult(summaryExisting);
            }

            var tool = _tools.FirstOrDefault(t => string.Equals(t.Name, toolName, StringComparison.OrdinalIgnoreCase));
            if (tool != null)
            {
                JsonElement argsElement = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson)?"{}":argumentsJson).RootElement;
                var toolResult = await tool.ExecuteAsync(argsElement, ct);
                history.Add(new JarvisMessage("tool", $"Tool {toolName} result: {toolResult}"));
                // Immediately summarize ourselves (avoid raw JSON echo) instead of relying on second model pass
                var summary = SummarizeToolResult(toolName, toolResult) ?? toolResult;
                return new JarvisTurnResult(summary);
            }
        }

        return await MaybeFallbackAsync(history, text, ct);
    }
    #endregion

    #region Gemini Implementation
    private async Task<JarvisTurnResult> ChatWithGeminiAsync(List<JarvisMessage> history, CancellationToken ct)
    {
        var functionDeclarations = new JsonArray();
        foreach (var t in _tools)
        {
            var schemaNode = JsonNode.Parse(t.JsonSchema)!; // user schema
            SanitizeSchemaForGemini(schemaNode);
            functionDeclarations.Add(new JsonObject
            {
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["parameters"] = schemaNode
            });
        }
        var toolsNode = new JsonArray();
        if (functionDeclarations.Count > 0)
            toolsNode.Add(new JsonObject { ["functionDeclarations"] = functionDeclarations });

        int lastUserIndex = history.FindLastIndex(h => h.Role == "user");
        int executedToolCount = lastUserIndex >= 0 ? history.Skip(lastUserIndex + 1).Count(h => h.Role == "tool") : 0;

        var systemText = executedToolCount >= MaxToolExecutionsPerTurn
            ? "You are J.A.R.V.I.S. A tool has already been executed for the latest user request; do NOT call another function. Use existing tool result(s) to answer."
            : "You are J.A.R.V.I.S., a concise helpful AI assistant. Use a functionCall only if needed for weather, news, or crypto market data.";

        var systemInstruction = new JsonObject
        {
            ["parts"] = new JsonArray { new JsonObject { ["text"] = systemText } }
        };

        var contents = new JsonArray();
        foreach (var m in history)
        {
            var role = m.Role switch
            {
                "assistant" => "model",
                "tool" => "user", // feed tool output
                _ => "user"
            };
            contents.Add(new JsonObject
            {
                ["role"] = role,
                ["parts"] = new JsonArray { new JsonObject { ["text"] = m.Content } }
            });
        }

        var req = new JsonObject
        {
            ["systemInstruction"] = systemInstruction,
            ["contents"] = contents,
            ["generationConfig"] = new JsonObject { ["temperature"] = 0.4 },
            ["tools"] = executedToolCount >= MaxToolExecutionsPerTurn ? new JsonArray() : toolsNode
        };

        var requestJson = req.ToJsonString();
        _logger.LogInformation("Gemini request: {Json}", requestJson);

        using var msg = new HttpRequestMessage(HttpMethod.Post, "https://generativelanguage.googleapis.com/v1beta/models/gemini-2.0-flash:generateContent");
        msg.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");
        msg.Headers.Add("X-goog-api-key", Environment.GetEnvironmentVariable("GOOGLEAI_API_KEY") ?? string.Empty);

        var response = await _http.SendAsync(msg, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Gemini error: {Status} {Body}", response.StatusCode, body);
            return new JarvisTurnResult($"API error {(int)response.StatusCode}: {response.ReasonPhrase}");
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var (text, functionCall) = ExtractGeminiOutput(root);
        if (functionCall != null)
        {
            if (executedToolCount >= MaxToolExecutionsPerTurn || AlreadyExecutedTool(history, functionCall.Value.Name))
            {
                var existing = ExtractPriorToolResult(history, functionCall.Value.Name);
                var summaryExisting = SummarizeToolResult(functionCall.Value.Name, existing) ?? existing ?? "(No data returned)";
                return new JarvisTurnResult(summaryExisting);
            }

            var tool = _tools.FirstOrDefault(t => string.Equals(t.Name, functionCall.Value.Name, StringComparison.OrdinalIgnoreCase));
            if (tool != null)
            {
                var toolResult = await tool.ExecuteAsync(functionCall.Value.Arguments, ct);
                history.Add(new JarvisMessage("tool", $"Tool {functionCall.Value.Name} result: {toolResult}"));
                var summary = SummarizeToolResult(functionCall.Value.Name, toolResult) ?? toolResult;
                return new JarvisTurnResult(summary);
            }
        }

        return await MaybeFallbackAsync(history, text, ct);
    }

    private static bool AlreadyExecutedTool(List<JarvisMessage> history, string toolName)
        => history.Any(m => m.Role == "tool" && m.Content.StartsWith($"Tool {toolName} result:"));

    private static string? ExtractPriorToolResult(List<JarvisMessage> history, string toolName)
    {
        var msg = history.LastOrDefault(m => m.Role == "tool" && m.Content.StartsWith($"Tool {toolName} result:"));
        if (msg == null) return null;
        var idx = msg.Content.IndexOf("result:", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        return msg.Content[(idx + 7)..].Trim();
    }

    private static string? SummarizeToolResult(string toolName, string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson)) return null;
        try
        {
            return toolName switch
            {
                "hackerNews" => TryFormatHackerNews(rawJson) ?? rawJson,
                "weather" => TryFormatWeather(rawJson) ?? rawJson,
                "binanceMarket" => TryFormatBinance(rawJson) ?? rawJson,
                _ => rawJson
            };
        }
        catch { return rawJson; }
    }
    #endregion

    #region Gemini helpers
    private static void SanitizeSchemaForGemini(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            obj.Remove("additionalProperties");
            foreach (var kv in obj.ToList())
                if (kv.Value != null) SanitizeSchemaForGemini(kv.Value);
        }
        else if (node is JsonArray arr)
        {
            foreach (var item in arr) if (item != null) SanitizeSchemaForGemini(item);
        }
    }

    private static (string Text, (string Name, JsonElement Arguments)? FunctionCall) ExtractGeminiOutput(JsonElement root)
    {
        try
        {
            var candidates = root.GetProperty("candidates");
            var first = candidates.EnumerateArray().FirstOrDefault();
            if (first.ValueKind == JsonValueKind.Undefined) return (string.Empty, null);
            var parts = first.GetProperty("content").GetProperty("parts");
            foreach (var part in parts.EnumerateArray())
            {
                if (part.TryGetProperty("functionCall", out var fc))
                {
                    var name = fc.GetProperty("name").GetString() ?? string.Empty;
                    var args = fc.GetProperty("args");
                    return (string.Empty, (name, args));
                }
                if (part.TryGetProperty("text", out var txtEl))
                {
                    return (txtEl.GetString() ?? string.Empty, null);
                }
            }
        }
        catch { }
        return (string.Empty, null);
    }
    #endregion

    // Shared fallback + helpers
    private async Task<JarvisTurnResult> MaybeFallbackAsync(List<JarvisMessage> history, string assistantText, CancellationToken ct)
    {
        var lastUser = history.LastOrDefault(h => h.Role == "user")?.Content ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(lastUser))
        {
            var lower = lastUser.ToLowerInvariant();
            // Crypto price first to avoid being captured by gainers logic
            var symbol = DetectCryptoSymbol(lower);
            if (symbol != null && !AlreadyExecutedTool(history, "binanceMarket") && (lower.Contains("price") || lower.Contains("value") || lower.Contains("worth")))
            {
                var toolPrice = _tools.FirstOrDefault(t => t.Name == "binanceMarket");
                if (toolPrice != null)
                {
                    var argsObj = new JsonObject { ["symbol"] = symbol, ["interval"] = "1h", ["limit"] = 30 };
                    var toolResult = await toolPrice.ExecuteAsync(JsonDocument.Parse(argsObj.ToJsonString()).RootElement, ct);
                    var friendly = TryFormatBinance(toolResult) ?? toolResult;
                    return new JarvisTurnResult(friendly);
                }
            }
            if (NeedsWeather(lower))
            {
                var tool = _tools.FirstOrDefault(t => t.Name == "weather");
                if (tool != null && !AlreadyExecutedTool(history, "weather"))
                {
                    var city = ExtractCityFromWeatherQuery(lastUser);
                    var argsObj = new JsonObject();
                    if (!string.IsNullOrWhiteSpace(city)) argsObj["city"] = city;
                    var toolResult = await tool.ExecuteAsync(JsonDocument.Parse(argsObj.ToJsonString()).RootElement, ct);
                    var friendly = TryFormatWeather(toolResult) ?? toolResult;
                    return new JarvisTurnResult(friendly);
                }
            }
            if (NeedsNews(lower))
            {
                var tool = _tools.FirstOrDefault(t => t.Name == "hackerNews");
                if (tool != null && !AlreadyExecutedTool(history, "hackerNews"))
                {
                    var argsObj = new JsonObject { ["limit"] = 5 };
                    var toolResult = await tool.ExecuteAsync(JsonDocument.Parse(argsObj.ToJsonString()).RootElement, ct);
                    var friendly = TryFormatHackerNews(toolResult) ?? toolResult;
                    return new JarvisTurnResult(friendly);
                }
            }
            if (NeedsCryptoGainers(lower))
            {
                var tool = _tools.FirstOrDefault(t => t.Name == "binanceMarket");
                if (tool != null && !AlreadyExecutedTool(history, "binanceMarket"))
                {
                    int top = ExtractTopNumber(lower, 5);
                    var argsObj = new JsonObject { ["topGainers"] = top };
                    var toolResult = await tool.ExecuteAsync(JsonDocument.Parse(argsObj.ToJsonString()).RootElement, ct);
                    var friendly = TryFormatBinance(toolResult) ?? toolResult;
                    return new JarvisTurnResult(friendly);
                }
            }
        }
        return new JarvisTurnResult(assistantText);
    }

    private static string ExtractAssistantText_OpenAI(JsonElement root)
    {
        try
        {
            var output = root.GetProperty("output");
            foreach (var item in output.EnumerateArray())
            {
                if (item.TryGetProperty("content", out var contentArr))
                {
                    foreach (var c in contentArr.EnumerateArray())
                    {
                        if (c.TryGetProperty("type", out var typeProp) && typeProp.GetString() == "output_text")
                            return c.GetProperty("text").GetString() ?? string.Empty;
                    }
                }
            }
        }
        catch { }
        return string.Empty;
    }

    private static bool TryParseToolDirective(string text, out string toolName, out string argumentsJson)
    {
        toolName = string.Empty;
        argumentsJson = string.Empty;
        if (string.IsNullOrWhiteSpace(text)) return false;
        const string marker = "TOOL_CALL:";
        var idx = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return false;
        var jsonPart = text[(idx + marker.Length)..].Trim();
        try
        {
            using var doc = JsonDocument.Parse(jsonPart);
            var root = doc.RootElement;
            toolName = root.GetProperty("name").GetString() ?? string.Empty;
            argumentsJson = root.GetProperty("arguments").GetRawText();
            return true;
        }
        catch { return false; }
    }

    private static bool NeedsWeather(string text) => text.Contains("weather") || text.Contains("forecast") || text.Contains("temperature") || text.Contains("wind");
    private static bool NeedsNews(string text) => text.Contains("hacker news") || text.Contains("hn ") || text.EndsWith(" hn") || text.Contains("tech news") || text.Contains("startup news") || text.Contains("latest news");
    private static bool NeedsCryptoGainers(string text) => (text.Contains("top") || text.Contains("best") || text.Contains("gainer")) && (text.Contains("crypto") || text.Contains("coin") || text.Contains("token") || text.Contains("gainer"));
    private static int ExtractTopNumber(string text, int def = 5)
    {
        foreach (var part in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(new string(part.Where(char.IsDigit).ToArray()), out var n) && n > 0) return Math.Clamp(n,1,20);
        }
        return def;
    }

    private static string ExtractCityFromWeatherQuery(string query)
    {
        var markers = new[] { " in ", " for " };
        foreach (var m in markers)
        {
            var idx = query.IndexOf(m, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                var city = query[(idx + m.Length)..].Trim().TrimEnd('?', '.', '!', ',');
                if (city.Length > 0) return city;
            }
        }
        return string.Empty;
    }

    private static string? TryFormatWeather(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out _)) return null;
            var loc = root.GetProperty("location").GetString();
            var t = root.GetProperty("temperature_c").GetDouble();
            var feels = root.GetProperty("apparent_temperature_c").GetDouble();
            var hum = root.GetProperty("humidity_percent").GetDouble();
            var wind = root.GetProperty("wind_speed_kmh").GetDouble();
            var dir = root.GetProperty("wind_direction_cardinal").GetString();
            return $"Weather for {loc}: {t:F1}\u00B0C (feels {feels:F1}\u00B0C), humidity {hum}%, wind {wind:F1} km/h {dir}.";
        }
        catch { return null; }
    }

    private static string? TryFormatHackerNews(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
            if (!doc.RootElement.EnumerateArray().Any()) return "No Hacker News stories retrieved just now.";
            var sb = new StringBuilder();
            sb.AppendLine("Top Hacker News stories:");
            int i = 1;
            foreach (var story in doc.RootElement.EnumerateArray())
            {
                var title = story.TryGetProperty("title", out var t) ? t.GetString() : null;
                var score = story.TryGetProperty("score", out var s) ? s.GetInt32() : 0;
                var url = story.TryGetProperty("url", out var u) ? u.GetString() : null;
                if (string.IsNullOrWhiteSpace(title)) continue;
                sb.AppendLine($"{i}. {title} (score {score}){(string.IsNullOrWhiteSpace(url)?"":" - "+url)}");
                if (++i > 5) break;
            }
            return sb.ToString().Trim();
        }
        catch { return null; }
    }

    private static string? TryFormatBinance(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            var sb = new StringBuilder();
            if (doc.RootElement.TryGetProperty("symbol", out var sym))
            {
                var interval = doc.RootElement.TryGetProperty("interval", out var intEl) ? intEl.GetString() : null;
                if (doc.RootElement.TryGetProperty("latest", out var latest) && latest.ValueKind == JsonValueKind.Object)
                {
                    double close = latest.GetProperty("close").GetDouble();
                    double changePct = latest.GetProperty("changePct").GetDouble();
                    sb.AppendLine($"{sym.GetString()} price: {close:0.########} USDT ({changePct:+0.##;-0.##;0}% last {interval}).");
                }
                if (doc.RootElement.TryGetProperty("sma_5", out var sma5) && doc.RootElement.TryGetProperty("sma_20", out var sma20))
                {
                    var s5 = sma5.GetDouble(); var s20 = sma20.GetDouble();
                    var relation = s5 > s20 ? "above" : s5 < s20 ? "below" : "near";
                    sb.AppendLine($"SMA5 {relation} SMA20 ({s5:0.########} vs {s20:0.########}).");
                }
            }
            if (doc.RootElement.TryGetProperty("topGainers", out var gainers) && gainers.ValueKind == JsonValueKind.Array && gainers.GetArrayLength() > 0)
            {
                if (sb.Length > 0) sb.AppendLine();
                sb.Append("Top gainers (24h %): ");
                int i = 1; var parts = new List<string>();
                foreach (var g in gainers.EnumerateArray())
                {
                    var symbol = g.GetProperty("symbol").GetString();
                    var pct = g.GetProperty("priceChangePercent").GetDouble();
                    var price = g.GetProperty("lastPrice").GetDouble();
                    parts.Add($"{i}. {symbol} {pct:+0.##;-0.##;0}% @ {price:0.########}");
                    if (++i > 10) break;
                }
                sb.Append(string.Join(" ", parts));
            }
            return sb.ToString().Trim();
        }
        catch { return null; }
    }

    private static string? DetectCryptoSymbol(string lower)
    {
        if (lower.Contains("bitcoin") || Regex.IsMatch(lower, @"\bbtc\b")) return "BTCUSDT";
        if (lower.Contains("ethereum") || Regex.IsMatch(lower, @"\beth\b")) return "ETHUSDT";
        if (lower.Contains("solana") || Regex.IsMatch(lower, @"\bsol\b")) return "SOLUSDT";
        if (lower.Contains("dogecoin") || Regex.IsMatch(lower, @"\bdoge\b")) return "DOGEUSDT";
        if (lower.Contains("cardano") || Regex.IsMatch(lower, @"\bada\b")) return "ADAUSDT";
        if (lower.Contains("ripple") || Regex.IsMatch(lower, @"\bxrp\b")) return "XRPUSDT";
        if (lower.Contains("polygon") || Regex.IsMatch(lower, @"\bmatic\b")) return "MATICUSDT";
        if (lower.Contains("chainlink") || Regex.IsMatch(lower, @"\blink\b")) return "LINKUSDT";
        if (lower.Contains("polkadot") || Regex.IsMatch(lower, @"\bdot\b")) return "DOTUSDT";
        if (lower.Contains("litecoin") || Regex.IsMatch(lower, @"\bltc\b")) return "LTCUSDT";
        if (lower.Contains("bitcoin cash") || Regex.IsMatch(lower, @"\bbch\b")) return "BCHUSDT";
        return null;
    }
}

public record JarvisMessage(string Role, string Content);

public record JarvisTurnResult(string AssistantReply);
