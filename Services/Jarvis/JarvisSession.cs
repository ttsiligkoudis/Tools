using System.Collections.Concurrent;
using ToolsServer.Services.Jarvis;

namespace ToolsServer.Services.Jarvis;

public class JarvisSession
{
    private readonly JarvisOrchestrator _orchestrator;
    public List<JarvisMessage> History { get; } = new();

    public JarvisSession(JarvisOrchestrator orchestrator)
    {
        _orchestrator = orchestrator;
        History.Add(new JarvisMessage("assistant", "Hello, I am J.A.R.V.I.S. How can I assist you today?"));
    }

    public Task<JarvisTurnResult> RespondAsync(CancellationToken ct = default) => _orchestrator.ChatAsync(History, ct);
}
