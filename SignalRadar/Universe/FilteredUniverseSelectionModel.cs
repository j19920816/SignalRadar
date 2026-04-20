using Giraffy.CryptoExchange;
using Giraffy.CryptoExchange.Common;
using Giraffy.CryptoExchange.RestCaller;
using SignalRadar.Algorithm.Interfaces;
using QuantConnect;
using QuantConnect.Algorithm.Framework.Selection;
using QuantConnect.Data.UniverseSelection;
using QuantConnect.Scheduling;
using QuantConnect.Securities;
using System;
using System.Collections.Generic;

// 別名：避免跟 Giraffy.CryptoExchange.Common.Market 撞名
using LeanMarket = QuantConnect.Market;

namespace SignalRadar.Algorithm.Universe
{
    /// <summary>
    /// Live 用 — 每 4H 重新篩選 Binance USDT 永續合約。
    ///   1. 從 SymbolsRule 取出所有 QuoteAsset == USDT 的合約
    ///   2. 透過 REST 拉 4H K 棒，跑 SymbolFilterModel 三層篩選
    ///   3. 回傳通過的 Symbol 清單給 Lean，Lean 自動 AddSecurity / RemoveSecurity
    /// </summary>
    public class FilteredUniverseSelectionModel : ScheduledUniverseSelectionModel
    {
        public FilteredUniverseSelectionModel(
            IDateRule dateRule,
            ITimeRule timeRule,
            SymbolsRule symbolsRule,
            SymbolFilterModel filter,
            IWarmUpProvider warmUpProvider,
            UniverseSettings universeSettings)
            : base(dateRule, timeRule, _ => SelectSymbols(symbolsRule, filter, warmUpProvider), universeSettings)
        {
        }

        private static IEnumerable<Symbol> SelectSymbols(SymbolsRule symbolsRule, SymbolFilterModel filter, IWarmUpProvider warmUpProvider)
        {
            // 1. 先撈 Lean 符號資料庫 (SPDB) 已知的 binance CryptoFuture ticker
            //    未知的 ticker (例如 EDGEUSDT 這種新上幣) 若直接丟進 Universe，
            //    SecurityService.CreateSecurity 會丟 "Failed to resolve base currency" 整個 algorithm 掛掉
            var spdb = SymbolPropertiesDatabase.FromDataFolder();
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in spdb.GetSymbolPropertiesList(LeanMarket.Binance, SecurityType.CryptoFuture))
            {
                known.Add(kvp.Key.Symbol);
            }

            // 2. 掃 Giraffy SymbolsRule，挑出所有 USDT 計價合約
            //    Lean 不認得的，用 Giraffy 的欄位 (QuoteAsset / QuantityStep / TickPriceStep) 即時補進 SPDB
            //    這樣 SecurityService 建 Security 時就能正確解析 base/quote currency 與 lot size
            var candidates = new List<Symbol>();
            foreach (var kvp in symbolsRule)
            {
                var ticker = kvp.Key;
                var rule = kvp.Value;

                if (!rule.QuoteAsset.Equals("USDT", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!known.Contains(ticker))
                {
                    // Lean 沒登記過 → 依 Giraffy 的規則替它補一筆到 SPDB
                    // TickPriceStep / QuantityStep 是 nullable，取不到就用保守的預設值
                    var tick = rule.TickPriceStep.HasValue ? rule.TickPriceStep.Value : 0.0001m;
                    var lot = rule.QuantityStep.HasValue ? rule.QuantityStep.Value : 1m;
                    var props = new SymbolProperties(
                        description: ticker,
                        quoteCurrency: rule.QuoteAsset,
                        contractMultiplier: 1m,
                        minimumPriceVariation: tick,
                        lotSize: lot,
                        marketTicker: ticker,
                        minimumOrderSize: null,
                        priceMagnifier: 1m,
                        strikeMultiplier: 1m);
                    spdb.SetEntry(LeanMarket.Binance, ticker, SecurityType.CryptoFuture, props);
                }

                var symbol = Symbol.Create(ticker, SecurityType.CryptoFuture, LeanMarket.Binance);
                candidates.Add(symbol);
            }

            // 3. 跑三層篩選（REST 拉 4H K 棒 → VolumeSMA / ADX / OBV），同步等結果回來
            return filter.RunAsync(candidates, warmUpProvider).GetAwaiter().GetResult();
        }
    }
}
