namespace TcpClientTest;

using NyxNetwork;
using NyxNetwork.Tcp;
using System.Net;
using System.Text;
using System.Text.Unicode;

public class Program
{
    static void Main(string[] args)
    {
        TcpConnection connection = new(IPAddress.Parse("127.0.0.1"), 6000);
        if (connection.StartAsync().Wait(5000))
        {
            Console.WriteLine("Connected to server.");
            var command = Encoding.ASCII.GetBytes("CM01");
            List<byte> packet = [ DevicePacket.StartMarker,DevicePacket.EndMarker ];
            packet.InsertRange(1, command);          

            connection.SendAndReceiveAsync(packet.ToArray(), (response) => {
                Console.WriteLine($"Received response:\r\n  " +
                    $"Command: {Encoding.ASCII.GetString(response.Command)} \r\n " +
                    $"Payload: {Encoding.ASCII.GetString(response.Payload)}");
            }).Wait();
        }
        else
        {
            Console.WriteLine("Failed to connect to server.");
        }
    }
}