using Giraffy.Net;
using QuantConnect.Algorithm.CSharp.Signals;
using System;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace QuantConnect.Algorithm.CSharp.Infrastructure
{
    public class TcpSignalSender : IDisposable
    {
        private NetClient _client;
        private readonly string _host;
        private readonly int _port;

        private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };

        public TcpSignalSender(string host, int port)
        {
            _host = host;
            _port = port;
        }

        public async Task ConnectAsync()
        {
            _client = new NetClient
            {
                Username = "CryptoTrader",
                Password = DateTime.Today.ToString("yyyyMMdd"),
                HeartbeatInterval = TimeSpan.FromSeconds(10),
                HeartbeatTimeout = TimeSpan.FromSeconds(30),
                MaxPacketSize = 4 * 1024 * 1024,
                AutoReconnect = true
            };

            await _client.ConnectAsync(_host, _port);
        }

        public async Task SendAsync(SignalMessage signal)
        {
            try
            {
                var json = JsonSerializer.Serialize(signal, _jsonOptions);
                var bytes = Encoding.UTF8.GetBytes(json);
                await _client.SendAsync(bytes);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TcpSignalSender] 發送失敗: {ex.Message}");
            }
        }

        public void Dispose()
        {
            _client?.DisconnectAsync().Wait();
        }
    }
}