using QuantConnect.Algorithm.CSharp.Interfaces;
using QuantConnect.Data.Consolidators;
using QuantConnect.Data.Market;
using QuantConnect.Indicators;
using System;
using System.Collections.Generic;

namespace QuantConnect.Algorithm.CSharp.Universe
{
    public class SymbolFilterModel
    {
        private class FilterData
        {
            // 第一層：流動性
            public SimpleMovingAverage VolumeSma { get; } = new SimpleMovingAverage(10);

            // 第二層：活躍度
            public AverageDirectionalIndex Adx { get; } = new AverageDirectionalIndex(14);
            public decimal CurrentUsdtVolume { get; set; }

            // 第三層：量能持續性
            public OnBalanceVolume Obv { get; } = new OnBalanceVolume();
            public SimpleMovingAverage ObvSma { get; } = new SimpleMovingAverage(10);
        }

        private readonly Dictionary<Symbol, FilterData> _filterData = new();
        private DateTime _lastFilterTime = DateTime.MinValue;

        public HashSet<Symbol> ActiveSymbols { get; } = new();

        public void RunFilter()
        {
            ActiveSymbols.Clear();

            foreach (var kvp in _filterData)
            {
                var symbol = kvp.Key;
                var filterData = kvp.Value;

                if (!filterData.VolumeSma.IsReady || !filterData.Adx.IsReady || !filterData.ObvSma.IsReady)
                    continue;

                // 第一層：流動性 — 過去 10 根 4H 成交金額均值 > 100 萬 USDT
                var isOverAmount = filterData.VolumeSma.Current.Value >= 1_000_000m;

                // 第二層：活躍度 — ADX >= 35 且當前 4H 成交金額 > 均值 × 1.5
                var isTrend = filterData.Adx.Current.Value >= 35m;
                var isOverAvgAmount = filterData.CurrentUsdtVolume > filterData.VolumeSma.Current.Value * 1.5m;

                // 第三層：量能持續性 — OBV > SMA10(OBV)
                var isOverObv = filterData.Obv.Current.Value > filterData.ObvSma.Current.Value;

                if (isOverAmount && isTrend && isOverAvgAmount && isOverObv)
                    ActiveSymbols.Add(symbol);
            }
        }

        public void RegisterSymbol(QCAlgorithm algorithm, Symbol symbol, bool isLive, IWarmUpProvider warmUpProvider = null)
        {
            if (_filterData.ContainsKey(symbol))
                return;

            var filterData = new FilterData();
            _filterData[symbol] = filterData;

            var timespan = TimeSpan.FromHours(4);
            if (isLive)
            {
                // Warm-up：餵歷史 K 棒讓指標快速 Ready
                if (warmUpProvider != null)
                {
                    foreach (var bar in warmUpProvider.GetBars(symbol, timespan, 100))
                    {
                        var usdtVolume = bar.Volume * bar.Close;
                        filterData.CurrentUsdtVolume = usdtVolume;
                        filterData.VolumeSma.Update(bar.EndTime, usdtVolume);
                        filterData.Adx.Update(bar);
                        filterData.Obv.Update(bar);
                        if (filterData.Obv.IsReady)
                            filterData.ObvSma.Update(bar.EndTime, filterData.Obv.Current.Value);
                    }
                }

                var consolidator = new TickConsolidator(timespan);
                consolidator.DataConsolidated += OnFourHourBar;
                algorithm.SubscriptionManager.AddConsolidator(symbol, consolidator);
            }
            else
            {
                var consolidator = new TradeBarConsolidator(timespan);
                consolidator.DataConsolidated += OnFourHourBar;
                algorithm.SubscriptionManager.AddConsolidator(symbol, consolidator);
            }
        }

        private void OnFourHourBar(object sender, TradeBar bar)
        {
            var symbol = bar.Symbol;
            var filterDate = _filterData[symbol];
            var usdtVolume = bar.Volume * bar.Close;

            filterDate.CurrentUsdtVolume = usdtVolume;
            filterDate.VolumeSma.Update(bar.EndTime, usdtVolume);
            filterDate.Adx.Update(bar);
            filterDate.Obv.Update(bar);
            if (filterDate.Obv.IsReady)
                filterDate.ObvSma.Update(bar.EndTime, filterDate.Obv.Current.Value);

            if (bar.EndTime > _lastFilterTime)
            {
                _lastFilterTime = bar.EndTime;
                RunFilter();
            }
        }
    }
}
