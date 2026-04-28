using SignalRadar.Algorithm.Interfaces;
using QuantConnect;
using QuantConnect.Algorithm;
using QuantConnect.Data.Consolidators;
using QuantConnect.Data.Market;
using QuantConnect.Indicators;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SignalRadar.Algorithm.Universe
{
    /// <summary>
    /// 三層篩選模型：流動性（VolumeSma）→ 活躍度（ADX + 當期成交量）→ 量能持續性（OBV）。
    ///   Live：由 FilteredUniverseSelectionModel 每 4H 呼叫 RunAsync，透過 REST 拉歷史 K 棒計算。
    ///   回測：由 AlphaModel.OnSecuritiesChanged 呼叫 RegisterSymbol 掛 4H Consolidator，靠 Lean 的回測 feed 累積指標。
    /// </summary>
    public class SymbolFilterModel
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

        public HashSet<Symbol> ActiveSymbols { get; private set; } = new();

        /// <summary>
        /// Live 模式 — 每 4H 由 UniverseSelection 呼叫。
        /// 針對所有 candidate symbol 平行拉 100 根 4H K 棒，計算三層篩選後回傳入選清單。
        /// 每次重新 warm-up，不保留指標狀態，避免 Binance websocket 超量訂閱後指標斷鏈。
        /// </summary>
        public async Task<HashSet<Symbol>> RunAsync(IEnumerable<Symbol> candidates, IWarmUpProvider warmUpProvider, int currencyCount = 10)
        {
            var timespan = TimeSpan.FromHours(4);
            var results = new ConcurrentDictionary<Symbol, FilterData>();

            using var throttle = new SemaphoreSlim(currencyCount);
            var tasks = candidates.Select(async symbol =>
            {
                await throttle.WaitAsync();
                try
                {
                    var fd = new FilterData();
                    var bars = await warmUpProvider.GetBarsAsync(symbol, timespan, 100);
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
                    results[symbol] = fd;
                }
                catch
                {
                    // 單一 symbol REST 失敗不影響其他 symbol
                }
                finally
                {
                    throttle.Release();
                }
            }).ToList();

            await Task.WhenAll(tasks);

            var active = new HashSet<Symbol>();
            foreach (var kvp in results)
            {
                if (PassFilter(kvp.Value))
                    active.Add(kvp.Key);
            }

            ActiveSymbols = active;
            return active;
        }

        /// <summary>
        /// 回測用 — AlphaModel 在 OnSecuritiesChanged 時呼叫，為 symbol 掛 4H Consolidator。
        /// 每根 4H 棒收盤更新指標並重跑一次篩選。
        /// </summary>
        public void RegisterSymbol(QCAlgorithm algorithm, Symbol symbol)
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

        private static bool PassFilter(FilterData filterData)
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
