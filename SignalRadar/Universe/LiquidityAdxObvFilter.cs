using QuantConnect;
using QuantConnect.Algorithm;
using QuantConnect.Data.Consolidators;
using QuantConnect.Data.Market;
using QuantConnect.Indicators;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace SignalRadar.Algorithm.Universe
{
    /// <summary>
    /// 三層篩選：流動性（VolumeSma）→ 活躍度（ADX + 當期成交量）→ 量能持續性（OBV）。
    ///   Live：基底 RunAsync 平行拉 K 棒後呼叫 EvaluateBars。
    ///   回測：RegisterSymbol 為每個 symbol 掛 4H Consolidator，靠 Lean 的回測 feed 累積指標。
    /// </summary>
    public class LiquidityAdxObvFilter : SymbolFilterBase
    {
        private class FilterData
        {
            public SimpleMovingAverage VolumeSma { get; } = new SimpleMovingAverage(10);
            public AverageDirectionalIndex Adx { get; } = new AverageDirectionalIndex(14);
            public decimal CurrentUsdtVolume { get; set; }
            public OnBalanceVolume Obv { get; } = new OnBalanceVolume();
            public SimpleMovingAverage ObvSma { get; } = new SimpleMovingAverage(10);
        }

        // 回測用：每個 Symbol 的指標狀態靠 Consolidator 持續累積
        private readonly ConcurrentDictionary<Symbol, FilterData> _filterData = new();

        public LiquidityAdxObvFilter(string sourceId) : base(sourceId)
        {
        }

        protected override bool EvaluateBars(Symbol symbol, IEnumerable<TradeBar> bars)
        {
            var fd = new FilterData();
            foreach (var bar in bars)
            {
                var usdtVolume = bar.Volume * bar.Close;
                fd.CurrentUsdtVolume = usdtVolume;
                fd.VolumeSma.Update(bar.EndTime, usdtVolume);
                fd.Adx.Update(bar);
                fd.Obv.Update(bar);
                if (fd.Obv.IsReady)
                    fd.ObvSma.Update(bar.EndTime, fd.Obv.Current.Value);
            }
            return PassFilter(fd);
        }

        public override void RegisterSymbol(QCAlgorithm algorithm, Symbol symbol)
        {
            if (!_filterData.TryAdd(symbol, new FilterData()))
                return;

            var consolidator = new TradeBarConsolidator(TimeSpan.FromHours(4));
            consolidator.DataConsolidated += OnFourHourBar;
            algorithm.SubscriptionManager.AddConsolidator(symbol, consolidator);
        }

        private void OnFourHourBar(object sender, TradeBar bar)
        {
            var filterData = _filterData[bar.Symbol];
            var usdtVolume = bar.Volume * bar.Close;

            filterData.CurrentUsdtVolume = usdtVolume;
            filterData.VolumeSma.Update(bar.EndTime, usdtVolume);
            filterData.Adx.Update(bar);
            filterData.Obv.Update(bar);
            if (filterData.Obv.IsReady)
                filterData.ObvSma.Update(bar.EndTime, filterData.Obv.Current.Value);

            var active = new HashSet<Symbol>();
            foreach (var kvp in _filterData)
            {
                if (PassFilter(kvp.Value))
                    active.Add(kvp.Key);
            }
            ActiveSymbols = active;
        }

        private bool PassFilter(FilterData filterData)
        {
            if (!filterData.VolumeSma.IsReady || !filterData.Adx.IsReady || !filterData.ObvSma.IsReady)
                return false;

            // 第一層：流動性 — 過去 10 根 4H 成交金額均值 > 100 萬 USDT
            var isOverAmount = filterData.VolumeSma.Current.Value >= 1_000_000m;

            // 第二層：活躍度 — ADX >= 35 且當前 4H 成交金額 > 均值 × 1.5
            var isTrend = filterData.Adx.Current.Value >= 35m;
            var isOverAvgAmount = filterData.CurrentUsdtVolume > filterData.VolumeSma.Current.Value * 1.5m;

            // 第三層：量能持續性 — OBV > SMA10(OBV)
            var isOverObv = filterData.Obv.Current.Value > filterData.ObvSma.Current.Value;

            return isOverAmount && isTrend && isOverAvgAmount && isOverObv;
        }
    }
}
