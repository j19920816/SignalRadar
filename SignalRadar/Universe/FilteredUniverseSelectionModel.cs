using Giraffy.CryptoExchange.Common;
using SignalRadar.Algorithm.Interfaces;
using QuantConnect;
using QuantConnect.Algorithm.Framework.Selection;
using QuantConnect.Data.UniverseSelection;
using QuantConnect.Scheduling;
using System.Collections.Generic;

namespace SignalRadar.Algorithm.Universe
{
    /// <summary>
    /// Live 用 — 每 4H 重新篩選 Binance USDT 永續合約。
    ///   1. 透過 BinanceCryptoUniverse.GetTradableUsdtPerpetuals 撈 USDT 永續合約 candidates
    ///   2. 透過 SymbolFilterModel 跑三層篩選（流動性 / ADX / OBV）
    ///   3. 回傳通過的 Symbol 清單給 Lean，Lean 自動 AddSecurity / RemoveSecurity
    /// </summary>
    public class FilteredUniverseSelectionModel : ScheduledUniverseSelectionModel
    {
        // 給 Alpha 訂閱用的來源識別字串。改名時 IDE 跳轉直接重構，避免散落各處的字面字串打錯。
        public const string SourceId = nameof(FilteredUniverseSelectionModel);

        public FilteredUniverseSelectionModel(
            IDateRule dateRule, ITimeRule timeRule,
            SymbolsRule symbolsRule, SymbolFilterModel filter,
            IWarmUpProvider warmUpProvider, UniverseSettings universeSettings)
            : base(dateRule, timeRule, _ => SelectSymbols(symbolsRule, filter, warmUpProvider), universeSettings)
        {
        }

        private static IEnumerable<Symbol> SelectSymbols(SymbolsRule symbolsRule, SymbolFilterModel filter, IWarmUpProvider warmUpProvider)
        {
            var candidates = BinanceCryptoUniverse.GetTradableUsdtPerpetuals(symbolsRule);
            var selected = filter.RunAsync(candidates, warmUpProvider).GetAwaiter().GetResult();
            UniverseSource.Set(SourceId, selected);
            return selected;
        }
    }
}
