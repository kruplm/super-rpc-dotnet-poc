using System.Threading.Tasks;

namespace Super.RPC.Example;

public class Program
{
    public static async Task Main(string[] args)
    {
        var webSocketService = new WebSocketService();
        await webSocketService.Start();
    }
}
