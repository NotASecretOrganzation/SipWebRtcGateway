// See https://aka.ms/new-console-template for more information
namespace ConsoleApp1
{
    public class WebSocketMessage
    {
        public string Type { get; set; }
        public object Data { get; set; }
        public string SessionId { get; set; }
    }
}