using System;
using System.Text.Json.Serialization;

namespace QuantConnect.Algorithm.CSharp.Signals
{
    public enum Side
    {
        Long,
        Short
    }

    public class SignalMessage
    {
        [JsonPropertyName("signalId")]
        public string SignalId { get; set; } = Guid.NewGuid().ToString("N");

        [JsonPropertyName("strategyId")]
        public string StrategyId { get; set; }

        [JsonPropertyName("symbol")]
        public string Symbol { get; set; }

        [JsonPropertyName("timestamp")]
        public long Timestamp { get; set; }

        [JsonPropertyName("price")]
        public decimal Price { get; set; }

        [JsonPropertyName("side")]
        public Giraffy.CryptoExchange.Common.Side Side { get; set; }

        [JsonPropertyName("timeFrame")]
        public string TimeFrame { get; set; }

        [JsonPropertyName("stopPrice")]
        public decimal StopPrice { get; set; }
    }
}