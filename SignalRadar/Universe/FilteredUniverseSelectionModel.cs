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
    ///   1. 透過 BinanceCryptoUniverse.GetTradableCryptos 撈 USDT 永續合約 candidates
    ///   2. 透過注入的 SymbolFilterBase 跑篩選，並把結果寫入 UniverseSource[filter.SourceId]
    ///   3. 回傳通過的 Symbol 清單給 Lean，Lean 自動 AddSecurity / RemoveSecurity
    /// 多策略時每個策略各自開一個 instance（搭配各自的 filter + sourceId）。
    /// </summary>
    public class FilteredUniverseSelectionModel : ScheduledUniverseSelectionModel
    {
        public FilteredUniverseSelectionModel(
            IDateRule dateRule, ITimeRule timeRule,
            SymbolsRule symbolsRule, SecurityType securityType, SymbolFilterBase filter,
            IWarmUpProvider warmUpProvider, UniverseSettings universeSettings)
            : base(dateRule, timeRule, _ => SelectSymbols(symbolsRule, securityType, filter, warmUpProvider), universeSettings)
        {
        }

        private static IEnumerable<Symbol> SelectSymbols(SymbolsRule symbolsRule, SecurityType securityType, SymbolFilterBase filter, IWarmUpProvider warmUpProvider)
        {
            var candidates = BinanceCryptoUniverse.GetTradableCryptos(symbolsRule, securityType);
            var selected = filter.RunAsync(candidates, warmUpProvider).GetAwaiter().GetResult();
            UniverseSource.Set(filter.SourceId, selected);
            return selected;
        }
    }
}
