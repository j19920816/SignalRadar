using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Launcher.Charts
{
    public static class BacktestResultLoader
    {
        private const string BARS_FILE = "backtest-bars.json";
        private const string ORDER_EVENTS_FILE = "SignalRadarAlgorithm-order-events.json";
        private const string SUMMARY_FILE = "SignalRadarAlgorithm-summary.json";

        // 讀 1c 寫出的 backtest-bars.json：{ "BTCUSDT 18R": [ { t,o,h,l,c,v }, ... ] }
        public static Dictionary<string, List<OhlcBar>> LoadBars()
        {
            var result = new Dictionary<string, List<OhlcBar>>();
            var path = Path.Combine(AppContext.BaseDirectory, BARS_FILE);
            if (!File.Exists(path))
                return result;

            var raw = JsonSerializer.Deserialize<Dictionary<string, List<BarData>>>(File.ReadAllText(path));
            if (raw == null)
                return result;

            foreach (var kvp in raw)
            {
                var bars = new List<OhlcBar>(kvp.Value.Count);
                foreach (var dto in kvp.Value)
                {
                    bars.Add(new OhlcBar(
                        dto.Time,
                        (double)dto.Open,
                        (double)dto.High,
                        (double)dto.Low,
                        (double)dto.Close,
                        (double)dto.Volume));
                }
                result[kvp.Key] = bars;
            }
            return result;
        }

        // 讀 Lean 自動產生的 order-events.json，只取 status=="filled"
        public static List<OrderFill> LoadOrderFills()
        {
            var result = new List<OrderFill>();
            var path = Path.Combine(AppContext.BaseDirectory, ORDER_EVENTS_FILE);
            if (!File.Exists(path))
                return result;

            var raw = JsonSerializer.Deserialize<List<OrderEventData>>(File.ReadAllText(path));
            if (raw == null)
                return result;

            foreach (var ev in raw)
            {
                if (!string.Equals(ev.Status, "filled", StringComparison.OrdinalIgnoreCase))
                    continue;
                result.Add(new OrderFill(ev.Symbol, ev.Time, ev.FillPrice, ev.FillQuantity, ev.Direction));
            }
            return result;
        }

        // 讀 Lean 自動產生的 summary.json：
        //   charts."Strategy Equity".series.Equity.values → 權益曲線
        //   statistics → 統計字典（Win Rate / Profit-Loss Ratio / Sharpe Ratio ...）
        public static BacktestSummary LoadSummary()
        {
            var path = Path.Combine(AppContext.BaseDirectory, SUMMARY_FILE);
            if (!File.Exists(path))
                return new BacktestSummary(Array.Empty<double>(), Array.Empty<double>(), new Dictionary<string, string>());

            using var doc = JsonDocument.Parse(File.ReadAllText(path));

            // 權益曲線：x 是 unix 秒，轉成 local time 的 OADate 給 ScottPlot 用
            var equityValues = doc.RootElement
                .GetProperty("charts")
                .GetProperty("Strategy Equity")
                .GetProperty("series")
                .GetProperty("Equity")
                .GetProperty("values");

            var count = equityValues.GetArrayLength();
            var timestamps = new double[count];
            var equity = new double[count];
            var i = 0;
            foreach (var point in equityValues.EnumerateArray())
            {
                var unixSec = point.GetProperty("x").GetDouble();
                var dt = DateTimeOffset.FromUnixTimeSeconds((long)unixSec).LocalDateTime;
                timestamps[i] = dt.ToOADate();
                equity[i] = point.GetProperty("y").GetDouble();
                i++;
            }

            // 統計字典：整包塞進去，window 自己挑要顯示的 key
            var stats = new Dictionary<string, string>();
            if (doc.RootElement.TryGetProperty("statistics", out var statsElem))
            {
                foreach (var prop in statsElem.EnumerateObject())
                {
                    var value = prop.Value.GetString();
                    stats[prop.Name] = value != null ? value : "";
                }
            }

            return new BacktestSummary(timestamps, equity, stats);
        }

        // backtest-bars.json 的單根 K 棒：欄位名是 1c 寫出來的 t/o/h/l/c/v
        private class BarData
        {
            [JsonPropertyName("t")] 
            public DateTime Time { get; set; }

            [JsonPropertyName("o")] 
            public decimal Open { get; set; }

            [JsonPropertyName("h")] 
            public decimal High { get; set; }

            [JsonPropertyName("l")] 
            public decimal Low { get; set; }

            [JsonPropertyName("c")] 
            public decimal Close { get; set; }

            [JsonPropertyName("v")] 
            public decimal Volume { get; set; }
        }

        // Lean order-events.json 的單筆事件：kebab-case 欄位名
        private class OrderEventData
        {
            [JsonPropertyName("symbol-value")] 
            public string Symbol { get; set; } = "";

            [JsonPropertyName("time")] 
            public DateTime Time { get; set; }
            [JsonPropertyName("status")] 
            public string Status { get; set; } = "";

            [JsonPropertyName("fill-price")] 
            public double FillPrice { get; set; }

            [JsonPropertyName("fill-quantity")] 
            public double FillQuantity { get; set; }

            [JsonPropertyName("direction")] 
            public string Direction { get; set; } = "";
        }
    }
}
