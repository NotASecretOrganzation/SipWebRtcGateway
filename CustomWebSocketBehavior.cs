using System.Text.Json;
using WebSocketSharp;
using WebSocketSharp.Server;

namespace ConsoleApp1
{
    public class CustomWebSocketBehavior : WebSocketBehavior
    {
        public Action<string, string>? HandleWebSocketMessage { get; set; }
        public Action<string, CustomWebSocketBehavior>? OnClientConnected { get; set; }
        public Action<string>? OnClientDisconnected { get; set; }
        public string SessionId { get; protected set; } = string.Empty;

        protected override void OnOpen()
        {
            SessionId = Context.QueryString["sessionId"] ?? Guid.NewGuid().ToString();
            OnClientConnected?.Invoke(SessionId, this);
            Console.WriteLine($"WebSocket client connected: {SessionId}");
        }

        protected override void OnClose(CloseEventArgs e)
        {
            OnClientDisconnected?.Invoke(SessionId);
            Console.WriteLine($"WebSocket client disconnected: {SessionId}");
        }

        protected override void OnMessage(MessageEventArgs e)
        {
            HandleWebSocketMessage?.Invoke(SessionId, e.Data);
        }

        public void SendMessage(object message)
        {
            if (Context.WebSocket.ReadyState == WebSocketState.Open)
            {
                Send(JsonSerializer.Serialize(message));
            }
        }
    }
}