using QuantConnect.Algorithm.CSharp.Interfaces;
using QuantConnect.Algorithm.CSharp.Universe;
using QuantConnect.Algorithm.Framework.Alphas;
using QuantConnect.Data;
using QuantConnect.Data.Consolidators;
using QuantConnect.Data.Market;
using QuantConnect.Data.UniverseSelection;
using QuantConnect.Indicators;
using QuantConnect.Indicators.CandlestickPatterns;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;

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
        private System.TimeSpan _timeSpan = System.TimeSpan.FromHours(1);
        public string StrategyId => "EngulfingCandle_1h";
        public string TimeFrame => "1h";

        // 每個 Symbol 各自維護一組指標與 K 棒視窗
        private readonly ConcurrentDictionary<Symbol, EngulfingData> _data = new();

        private readonly SymbolFilterModel _symbolFilter = new();
        private readonly IWarmUpProvider _warmUpProvider;

        public EngulfingCandlePatternAlpha(IWarmUpProvider warmUpProvider = null)
        {
            _warmUpProvider = warmUpProvider;
        }

        /// <summary>
        /// 初始化, 只跑一次, 當新的 Symbol 被加入時，為它建立吞噬指標並用小時 K 棒更新。
        /// </summary>
        public override async void OnSecuritiesChanged(QCAlgorithm algorithm, SecurityChanges changes)
        {
            try
            {
                foreach (var security in changes.AddedSecurities)
                {
                    var symbol = security.Symbol;

                    if (!_data.ContainsKey(symbol))
                    {
                        var engulfingData = new EngulfingData { Engulfing = new Engulfing() };
                        _data[symbol] = engulfingData;

                        if (algorithm.LiveMode)
                        {
                            // Live（Tick）：Warm-up 餵歷史 1H K 棒讓指標快速 Ready
                            if (_warmUpProvider != null)
                            {
                                var bars = await _warmUpProvider.GetBarsAsync(symbol, TimeSpan.FromHours(1), 3);
                                foreach (var bar in bars)
                                {
                                    engulfingData.Engulfing.Update(bar);
                                    engulfingData.Bars.Add(bar);
                                }
                            }

                            // 手動建立 1H 整合器，同時更新指標與 K 棒視窗
                            var consolidator = new TickConsolidator(TimeSpan.FromHours(1));
                            consolidator.DataConsolidated += OnDataConsolidated;
                            algorithm.SubscriptionManager.AddConsolidator(symbol, consolidator);
                        }
                        else
                        {
                            // 回測（Minute）：Lean 自動用 1h TradeBar 更新指標
                            algorithm.RegisterIndicator(symbol, engulfingData.Engulfing, Resolution.Hour);
                        }
                    }

                    // 建立 4H 整合器，供過濾器計算流動性、ADX、OBV 指標
                    await _symbolFilter.RegisterSymbolAsync(algorithm, symbol, algorithm.LiveMode, _warmUpProvider);
                }
            }
            catch (Exception ex)
            {
                algorithm.Error($"[EngulfingCandlePatternAlpha] OnSecuritiesChanged 失敗: {ex.Message}");
            }
        }

        private void OnDataConsolidated(object sender, TradeBar bar)
        {
            var symbol = bar.Symbol;
            _data[symbol].Engulfing.Update(bar);
            _data[symbol].Bars.Add(bar);
            _data[symbol].HasNewBar = true;
        }

        /// <summary>
        /// 每根 1 分鐘 K 棒都會被呼叫，但只在整點（新的 1 小時 K 棒剛收盤）才處理。
        /// </summary>
        public override IEnumerable<Insight> Update(QCAlgorithm algorithm, Slice data)
        {
            if (algorithm.LiveMode)
            {
                // Live（Tick）：由整合器 callback 更新指標，HasNewBar 旗標表示有新 1H 棒
                foreach (var kvp in _data)
                {
                    var symbol = kvp.Key;
                    var engulfingData = kvp.Value;

                    if (!engulfingData.HasNewBar)
                        continue;

                    engulfingData.HasNewBar = false;

                    if (!_symbolFilter.ActiveSymbols.Contains(symbol) || !engulfingData.Engulfing.IsReady)
                        continue;

                    var value = engulfingData.Engulfing.Current.Value;
                    if (value > 0)
                        yield return Insight.Price(symbol, _timeSpan, InsightDirection.Up);
                    else if (value < 0)
                        yield return Insight.Price(symbol, _timeSpan, InsightDirection.Down);
                }
            }
            else
            {
                // 回測（Minute）：非整點代表小時 K 棒還未收盤，跳過
                if (algorithm.Time.Minute != 0) yield break;

                foreach (var kvp in _data)
                {
                    var symbol = kvp.Key;
                    var engulfing = kvp.Value.Engulfing;

                    if (!_symbolFilter.ActiveSymbols.Contains(symbol) || !engulfing.IsReady || !data.Bars.ContainsKey(symbol))
                        continue;

                    // 把最新 K 棒存入滾動視窗，供 GetStopPrice 計算前一根 Low
                    kvp.Value.Bars.Add(data.Bars[symbol]);

                    var value = engulfing.Current.Value;
                    if (value > 0)
                        yield return Insight.Price(symbol, _timeSpan, InsightDirection.Up);
                    else if (value < 0)
                        yield return Insight.Price(symbol, _timeSpan, InsightDirection.Down);
                }
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
        public bool HasNewBar { get; set; } = false;

        // 保留最近 3 根 K 棒，用來計算止損價
        public RollingWindow<TradeBar> Bars { get; set; } = new RollingWindow<TradeBar>(3);
    }
}
