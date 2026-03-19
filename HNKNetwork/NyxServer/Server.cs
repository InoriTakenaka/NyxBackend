using NyxNetwork;
using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;

namespace NyxServer
{
    public class TcpServer
    {
        private int _port { get; set; }
        private TcpListener _listener;
        public TcpServer(int port)
        {
            _port = port;
        }
        public void Start()
        {
            _listener = new TcpListener(System.Net.IPAddress.Any, _port);
            _listener.Start();
            Console.WriteLine($"Starting TCP server on port {_port}...");
            while (true)
            {
                TcpClient client = _listener.AcceptTcpClient();
                Console.WriteLine($"client connected.");
                HandleConnect(client);
            }
        }

        private void HandleConnect(TcpClient client)
        {
            using NetworkStream stream = client.GetStream();
            byte[] buffer = new byte[1024];
            try
            {
                while (true)
                {
                    int byteRead = stream.Read(buffer, 0, buffer.Length);
                    if (byteRead == 0)
                    {
                        Console.WriteLine("Client disconnected.");
                        break;
                    }

                    ReadOnlySpan<byte> receivedData = buffer.AsSpan(0, byteRead);
                    if (receivedData[0] == DevicePacket.StartMarker &&
                        receivedData[^1] == DevicePacket.EndMarker)
                    {
                        var command = receivedData.Slice(1, 4);
                        var payload = receivedData.Slice(5, receivedData.Length - 6);

                        DevicePacket requestPacket = new(command, payload);
                        Console.WriteLine($"received: {BitConverter.ToString(requestPacket.Command.ToArray())}");

                        byte[] responsePayload = Encoding.ASCII.GetBytes("ACK");
                        DevicePacket responsePacket = new(command, responsePayload);

                        stream.Write(responsePacket.ToBytes(), 0, responsePacket.ToBytes().Length);
                    }
                }

            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error handling client: {ex.Message}");
            }
        }
        public class Program
        {
            public static int Main(string[] args)
            {
                int port = 6000;
                TcpServer server = new(port);
                server.Start();
                return 0;
            }
        }
    }
}
