using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ToolsServer.Services.Jarvis;

public interface IJarvisTool
{
    string Name { get; }
    string Description { get; } // High-level description of what the tool does and when to use it.
    string JsonSchema { get; } // A JSON schema (string) describing the arguments required.
    Task<string> ExecuteAsync(JsonElement arguments, CancellationToken ct = default);
}
