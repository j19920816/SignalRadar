using QuantConnect.Algorithm.CSharp.Interfaces;
using QuantConnect.Algorithm.Framework.Alphas;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Data.UniverseSelection;
using QuantConnect.Indicators;
using QuantConnect.Indicators.CandlestickPatterns;
using System.Collections.Generic;

namespace QuantConnect.Algorithm.CSharp.Alphas
{
    /// <summary>
    /// 1 小時吞噬形態 Alpha：
    ///   看漲吞噬 → Insight.Up（建議做多）
    ///   看跌吞噬 → Insight.Down（建議做空）
    /// 本層只負責產生訊號，不下單也不送 TCP。
    /// </summary>
    public class EngulfingCandlePatternAlpha : AlphaModel, ISignalAlpha
    {
        public string StrategyId => "EngulfingCandle_1h";
        public string TimeFrame => "1h";

        // 每個 Symbol 各自維護一組指標與 K 棒視窗
        private readonly Dictionary<Symbol, EngulfingData> _data = new();

        /// <summary>
        /// 當新的 Symbol 被加入時，為它建立吞噬指標並用小時 K 棒更新。
        /// </summary>
        public override void OnSecuritiesChanged(QCAlgorithm algorithm, SecurityChanges changes)
        {
            foreach (var security in changes.AddedSecurities)
            {
                if (!_data.ContainsKey(security.Symbol))
                {
                    _data[security.Symbol] = new EngulfingData { Engulfing = new Engulfing() };

                    // 將指標綁定到小時解析度，Lean 會自動用 1h TradeBar 更新
                    algorithm.RegisterIndicator(security.Symbol, _data[security.Symbol].Engulfing, Resolution.Hour);
                }
            }
        }

        /// <summary>
        /// 每根 1 分鐘 K 棒都會被呼叫，但只在整點（新的 1 小時 K 棒剛收盤）才處理。
        /// </summary>
        public override IEnumerable<Insight> Update(QCAlgorithm algorithm, Slice data)
        {
            // 非整點代表小時 K 棒還未收盤，跳過
            if (algorithm.Time.Minute != 0)
                yield break;

            foreach (var kvp in _data)
            {
                var symbol = kvp.Key;
                var engulfing = kvp.Value.Engulfing;

                // 指標需要至少兩根 K 棒才能判斷吞噬
                if (!engulfing.IsReady) continue;

                // 確認此 Symbol 這根 K 棒有資料
                if (!data.Bars.ContainsKey(symbol)) continue;

                // 把最新 K 棒存入滾動視窗，供 GetStopPrice 計算前一根 Low
                kvp.Value.Bars.Add(data.Bars[symbol]);

                var value = engulfing.Current.Value;

                // value > 0：看漲吞噬（陽線完全吞噬前一根陰線）→ 做多訊號
                if (value > 0)
                    yield return Insight.Price(symbol, System.TimeSpan.FromDays(365), InsightDirection.Up);

                // value < 0：看跌吞噬（陰線完全吞噬前一根陽線）→ 做空訊號
                else if (value < 0)
                    yield return Insight.Price(symbol, System.TimeSpan.FromDays(365), InsightDirection.Down);
            }
        }

        /// <summary>
        /// 取得止損價：前一根 K 棒的最低點。
        /// 由 SignalExecutionModel 在建立訊號時呼叫。
        /// </summary>
        public decimal GetStopPrice(Symbol symbol)
        {
            // 視窗尚未填滿時回傳 0（由呼叫端決定如何處理）
            if (!_data.TryGetValue(symbol, out var d) || !d.Bars.IsReady)
                return 0;

            // Bars[0] = 最新，Bars[1] = 前一根
            return d.Bars[1].Low;
        }
    }

    /// <summary>
    /// 每個 Symbol 的指標狀態容器
    /// </summary>
    public class EngulfingData
    {
        public Engulfing Engulfing { get; set; }

        // 保留最近 3 根 K 棒，用來計算止損價
        public RollingWindow<TradeBar> Bars { get; set; } = new RollingWindow<TradeBar>(3);
    }
}
