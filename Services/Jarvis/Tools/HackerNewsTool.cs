using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ToolsServer.Services.Jarvis.Tools;

public class HackerNewsTool : IJarvisTool
{
    private readonly HttpClient _http;
    public string Name => "hackerNews";
    public string Description => "Use this tool to fetch current top Hacker News stories or details about a specific story id. User asks for tech news, startup news, programming news, HN, hackernews -> use this.";

    // Schema kept very small; model can omit optional fields.
    public string JsonSchema =>
        "{\n" +
        "  \"type\": \"object\",\n" +
        "  \"properties\": {\n" +
        "    \"limit\": { \"type\": \"integer\", \"description\": \"Maximum number of top stories to return (default 5, max 20)\" },\n" +
        "    \"storyId\": { \"type\": \"integer\", \"description\": \"If provided, fetch only the details for this story id\" }\n" +
        "  },\n" +
        "  \"additionalProperties\": false\n" +
        "}";

    public HackerNewsTool(HttpClient httpClient)
    {
        _http = httpClient;
        // Base Firebase API
        _http.BaseAddress ??= new Uri("https://hacker-news.firebaseio.com/v0/");
        _http.Timeout = TimeSpan.FromSeconds(15);
    }

    public async Task<string> ExecuteAsync(JsonElement arguments, CancellationToken ct = default)
    {
        int? storyId = null;
        int limit = 5;
        if (arguments.TryGetProperty("storyId", out var storyIdProp) && storyIdProp.ValueKind == JsonValueKind.Number)
        {
            storyId = storyIdProp.GetInt32();
        }
        if (arguments.TryGetProperty("limit", out var limitProp) && limitProp.ValueKind == JsonValueKind.Number)
        {
            limit = Math.Clamp(limitProp.GetInt32(), 1, 20);
        }

        if (storyId.HasValue)
        {
            var detail = await GetStoryAsync(storyId.Value, ct);
            return JsonSerializer.Serialize(detail, JarvisJson.Options);
        }

        var stories = await GetTopStoriesAsync(limit, ct);
        return JsonSerializer.Serialize(stories, JarvisJson.Options);
    }

    private async Task<List<HackerNewsStory>> GetTopStoriesAsync(int limit, CancellationToken ct)
    {
        var list = new List<HackerNewsStory>();
        try
        {
            int[] ids = Array.Empty<int>();
            try
            {
                // Add cache-buster query to reduce potential CDN stale edge returning empty
                var req = new HttpRequestMessage(HttpMethod.Get, $"topstories.json?ts={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}");
                req.Headers.UserAgent.ParseAdd("ToolsServerHackerNews/1.0 (+https://example.invalid)");
                var resp = await _http.SendAsync(req, ct);
                if (resp.IsSuccessStatusCode)
                {
                    var json = await resp.Content.ReadAsStringAsync(ct);
                    ids = JsonSerializer.Deserialize<int[]>(json) ?? Array.Empty<int>();
                }
            }
            catch { }

            if (ids.Length == 0)
            {
                // Fallback: Algolia front page API
                try
                {
                    var algolia = await _http.GetFromJsonAsync<AlgoliaResponse>("https://hn.algolia.com/api/v1/search?tags=front_page", ct);
                    if (algolia?.Hits != null)
                    {
                        foreach (var h in algolia.Hits.Take(limit))
                        {
                            list.Add(new HackerNewsStory
                            {
                                Id = h.ObjectID,
                                Title = h.Title ?? h.StoryTitle,
                                Url = h.Url ?? h.StoryUrl,
                                Score = h.Points,
                                By = h.Author,
                                Time = DateTimeOffset.FromUnixTimeSeconds(h.CreatedAtI).UtcDateTime,
                                CommentCount = h.NumComments
                            });
                        }
                        return list;
                    }
                }
                catch { }
            }

            foreach (var id in ids.Take(limit))
            {
                var story = await GetStoryAsync(id, ct);
                if (story != null)
                    list.Add(story);
            }
        }
        catch { }
        return list;
    }

    private async Task<HackerNewsStory?> GetStoryAsync(int id, CancellationToken ct)
    {
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get, "item/" + id + ".json");
            req.Headers.UserAgent.ParseAdd("ToolsServerHackerNews/1.0 (+https://example.invalid)");
            var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync(ct);
            var story = JsonSerializer.Deserialize<HackerNewsItem>(json, JarvisJson.Options);
            if (story == null || story.Type != "story") return null;
            return new HackerNewsStory
            {
                Id = story.Id,
                Title = story.Title,
                Url = story.Url,
                Score = story.Score,
                By = story.By,
                Time = DateTimeOffset.FromUnixTimeSeconds(story.Time).UtcDateTime,
                CommentCount = story.Descendants
            };
        }
        catch
        {
            return null;
        }
    }

    private sealed class HackerNewsItem
    {
        [JsonPropertyName("id")] public int Id { get; set; }
        [JsonPropertyName("type")] public string Type { get; set; } = string.Empty;
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("url")] public string? Url { get; set; }
        [JsonPropertyName("score")] public int Score { get; set; }
        [JsonPropertyName("by")] public string By { get; set; } = string.Empty;
        [JsonPropertyName("time")] public long Time { get; set; }
        [JsonPropertyName("descendants")] public int Descendants { get; set; }
    }

    private sealed class HackerNewsStory
    {
        public int Id { get; set; }
        public string? Title { get; set; }
        public string? Url { get; set; }
        public int Score { get; set; }
        public string By { get; set; } = string.Empty;
        public DateTime Time { get; set; }
        public int CommentCount { get; set; }
    }

    // Algolia fallback DTOs
    private sealed class AlgoliaResponse
    {
        [JsonPropertyName("hits")] public List<AlgoliaHit>? Hits { get; set; }
    }
    private sealed class AlgoliaHit
    {
        [JsonPropertyName("objectID")] public int ObjectID { get; set; }
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("story_title")] public string? StoryTitle { get; set; }
        [JsonPropertyName("url")] public string? Url { get; set; }
        [JsonPropertyName("story_url")] public string? StoryUrl { get; set; }
        [JsonPropertyName("points")] public int Points { get; set; }
        [JsonPropertyName("author")] public string Author { get; set; } = string.Empty;
        [JsonPropertyName("created_at_i")] public long CreatedAtI { get; set; }
        [JsonPropertyName("num_comments")] public int NumComments { get; set; }
    }
}
