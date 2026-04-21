using SignalRadar.Algorithm.Interfaces;
using SignalRadar.Algorithm.Universe;
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
using QuantConnect;
using QuantConnect.Algorithm;

namespace SignalRadar.Algorithm.Alphas
{
    /// <summary>
    /// 15 分鐘吞噬形態 Alpha：
    ///   看漲吞噬 → Insight.Up（建議做多）
    ///   看跌吞噬 → Insight.Down（建議做空）
    /// 本層只負責產生訊號，不下單也不送 TCP。
    /// </summary>
    public class EngulfingCandlePatternAlpha : AlphaModel, ISignalAlpha
    {
        private readonly TimeSpan _timeSpan = TimeSpan.FromMinutes(5);
        public string StrategyId => "EngulfingCandle";
        public string TimeFrame => _timeSpan.TotalMinutes.ToString();

        // 每個 Symbol 各自維護一組指標與 K 棒視窗
        private readonly ConcurrentDictionary<Symbol, EngulfingData> _engulfingData = new();

        private readonly SymbolFilterModel _symbolFilter;
        private readonly IWarmUpProvider _warmUpProvider;

        public EngulfingCandlePatternAlpha(IWarmUpProvider warmUpProvider, SymbolFilterModel symbolFilter)
        {
            _warmUpProvider = warmUpProvider;
            _symbolFilter = symbolFilter;
        }

        /// <summary>
        /// 新符號被加入時為它建立吞噬指標 + Consolidator。
        /// Live：先用 REST 歷史 K 棒做 warm-up；回測另外掛 4H Consolidator 給篩選器。
        /// </summary>
        public override async void OnSecuritiesChanged(QCAlgorithm algorithm, SecurityChanges changes)
        {
            try
            {
                foreach (var security in changes.AddedSecurities)
                {
                    var symbol = security.Symbol;
                    if (_engulfingData.ContainsKey(symbol))
                        continue;

                    var engulfingData = new EngulfingData { Engulfing = new Engulfing() };
                    _engulfingData[symbol] = engulfingData;

                    // Live：REST 拉 3 根歷史 K 棒餵指標（Minute 訂閱等於即時 warm-up）
                    if (algorithm.LiveMode && _warmUpProvider != null)
                    {
                        var bars = await _warmUpProvider.GetBarsAsync(symbol, _timeSpan, 3);
                        foreach (var bar in bars)
                        {
                            engulfingData.Engulfing.Update(bar);
                            engulfingData.Bars.Add(bar);
                        }
                    }

                    // Minute 訂閱 → Consolidator 合成，兩邊模式共用
                    var consolidator = new TradeBarConsolidator(_timeSpan);
                    consolidator.DataConsolidated += OnDataConsolidated;
                    algorithm.SubscriptionManager.AddConsolidator(symbol, consolidator);

                    // 回測：Alpha 替每個 symbol 掛 4H Consolidator 給篩選器
                    // Live 篩選由 FilteredUniverseSelectionModel 透過 REST 跑，不需掛 Consolidator
                    if (!algorithm.LiveMode)
                        _symbolFilter.RegisterSymbol(algorithm, symbol);
                }
            }
            catch (Exception ex)
            {
                algorithm.Error($"[EngulfingCandlePatternAlpha] OnSecuritiesChanged 失敗: {ex.Message}");
            }
        }

        private void OnDataConsolidated(object sender, TradeBar bar)
        {
            var data = _engulfingData[bar.Symbol];
            data.Engulfing.Update(bar);
            data.Bars.Add(bar);
            data.HasNewBar = true;
        }

        /// <summary>
        /// 每次 Slice 進來都會呼叫，由 HasNewBar 旗標判斷是否有新 K 棒可處理。
        /// </summary>
        public override IEnumerable<Insight> Update(QCAlgorithm algorithm, Slice data)
        {
            foreach (var kvp in _engulfingData)
            {
                var symbol = kvp.Key;
                var engulfingData = kvp.Value;

                if (!engulfingData.HasNewBar)
                    continue;
                engulfingData.HasNewBar = false;

                if (!_symbolFilter.ActiveSymbols.Contains(symbol) /*|| !engulfingData.Engulfing.IsReady*/)
                    continue;

                var value = engulfingData.Engulfing.Current.Value;
                if (value > 0)
                    yield return Insight.Price(symbol, _timeSpan, InsightDirection.Up);
                else if (value < 0)
                    yield return Insight.Price(symbol, _timeSpan, InsightDirection.Down);
            }

            //algorithm.Log($"[Alpha] Symbols={_data.Count} ActiveSet={_symbolFilter.ActiveSymbols.Count}");
        }

        /// <summary>
        /// 取得止損價：前一根 K 棒的最低點。
        /// 由 SignalExecutionModel 在建立訊號時呼叫。
        /// </summary>
        public decimal GetStopPrice(Symbol symbol)
        {
            if (!_engulfingData.TryGetValue(symbol, out var d) || !d.Bars.IsReady)
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
