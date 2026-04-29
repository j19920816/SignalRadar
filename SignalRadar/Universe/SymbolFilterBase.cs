using SignalRadar.Algorithm.Interfaces;
using QuantConnect;
using QuantConnect.Algorithm;
using QuantConnect.Data.Market;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SignalRadar.Algorithm.Universe
{
    /// <summary>
    /// 標的篩選器抽象基底。每個 filter 對應一個 UniverseSource 來源 id —
    /// FilteredUniverseSelectionModel 把篩選結果寫進 UniverseSource[SourceId]，
    /// Alpha 透過 BelongsToSource(symbol) 判斷是否屬於自己訂閱的來源。
    ///
    /// Live 路徑：基底提供節流 + 平行拉 K 棒模板（RunAsync），子類只實作 EvaluateBars 判斷單一 symbol 是否通過。
    /// 回測路徑：RegisterSymbol 完全交給子類別實作（指標狀態跟 Consolidator 處理跟子類自己的 FilterData 強綁，不適合抽到基底）。
    /// </summary>
    public abstract class SymbolFilterBase
    {
        public string SourceId { get; }
        public HashSet<Symbol> ActiveSymbols { get; protected set; } = new();

        protected SymbolFilterBase(string sourceId)
        {
            SourceId = sourceId;
        }

        /// <summary>
        /// Live 模式 — 每 4H 由 UniverseSelection 呼叫。
        /// 針對所有 candidate symbol 平行拉 100 根 4H K 棒，呼叫子類別 EvaluateBars 判斷後回傳通過清單。
        /// 每次重新 warm-up，不保留指標狀態，避免 Binance websocket 超量訂閱後指標斷鏈。
        /// </summary>
        public async Task<HashSet<Symbol>> RunAsync(IEnumerable<Symbol> candidates, IWarmUpProvider warmUpProvider, int concurrency = 10)
        {
            var timespan = TimeSpan.FromHours(4);
            var passed = new ConcurrentBag<Symbol>();

            using var throttle = new SemaphoreSlim(concurrency);
            var tasks = candidates.Select(async symbol =>
            {
                await throttle.WaitAsync();
                try
                {
                    var bars = await warmUpProvider.GetBarsAsync(symbol, timespan, 100);
                    if (EvaluateBars(symbol, bars))
                        passed.Add(symbol);
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

            ActiveSymbols = new HashSet<Symbol>(passed);
            return ActiveSymbols;
        }

        /// <summary>
        /// Live 路徑 — 子類根據 100 根 4H 棒判斷該 symbol 是否通過。
        /// 每次呼叫獨立計算指標，不保留狀態。
        /// </summary>
        protected abstract bool EvaluateBars(Symbol symbol, IEnumerable<TradeBar> bars);

        /// <summary>
        /// 回測路徑 — Alpha 在 OnSecuritiesChanged 為每個新 symbol 呼叫一次。
        /// 子類自行掛 Consolidator、累積指標、更新 ActiveSymbols。
        /// </summary>
        public abstract void RegisterSymbol(QCAlgorithm algorithm, Symbol symbol);
    }
}
