using System.Text.Json;
using System.Text.Json.Serialization;

namespace ToolsServer.Services.Jarvis;

public static class JarvisJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
