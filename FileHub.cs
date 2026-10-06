using Microsoft.AspNetCore.SignalR;

namespace ToolsServer
{
    public class FileHub : Hub
    {
        public async Task SendFileChunk(string name, string data)
        {
            await Clients.All.SendAsync("ReceiveFile", name, data);
        }

        public string GetConnectionId()
        {
            return Context.ConnectionId;
        }
    }
}
