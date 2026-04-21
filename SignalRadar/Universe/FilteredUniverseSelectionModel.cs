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
        // Lean 內部會把商品/法幣/CFD 走非 crypto 的 currency conversion 路徑，導致 EnsureCurrencyDataFeed 崩潰
        // （例如 NATGASUSDT、XPTUSDT、XAUUSDT）。從 SPDB 非加密市場動態推出這類 base asset 清單，
        // 之後 Binance 再上什麼 SILVERUSDT / PLATINUMUSDT 只要 Lean SPDB 有對應登記就會自動擋下，不用改程式碼。
        private static readonly Lazy<HashSet<string>> NonCryptoBases = new Lazy<HashSet<string>>(BuildNonCryptoBases);

        public FilteredUniverseSelectionModel(
            IDateRule dateRule,ITimeRule timeRule,
            SymbolsRule symbolsRule,SymbolFilterModel filter,
            IWarmUpProvider warmUpProvider, UniverseSettings universeSettings)
            : base(dateRule, timeRule, _ => SelectSymbols(symbolsRule, filter, warmUpProvider), universeSettings)
        {
        }

        private static HashSet<string> BuildNonCryptoBases()
        {
            var spdb = SymbolPropertiesDatabase.FromDataFolder();
            var bases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 所有 Lean 登記過非加密資產的 market / SecurityType 組合
            // (從 Data/symbol-properties/symbol-properties-database.csv 實際存在的組合歸納出來)
            var markets = new[] { LeanMarket.FXCM, LeanMarket.Oanda, LeanMarket.InteractiveBrokers,
                                  LeanMarket.NYMEX, LeanMarket.CME, LeanMarket.CBOT, LeanMarket.ICE, LeanMarket.COMEX };
            var types = new[] { SecurityType.Cfd, SecurityType.Forex, SecurityType.Future, SecurityType.FutureOption };

            foreach (var market in markets)
            {
                foreach (var type in types)
                {
                    foreach (var kvp in spdb.GetSymbolPropertiesList(market, type))
                    {
                        var ticker = kvp.Key.Symbol;
                        var quote = kvp.Value.QuoteCurrency;
                        // 多數 cfd/forex 的 ticker 是 base+quote 直接串接（NATGASUSD / XPTUSD / EURUSD），拆出前綴
                        if (!string.IsNullOrEmpty(quote) && ticker.Length > quote.Length
                            && ticker.EndsWith(quote, StringComparison.OrdinalIgnoreCase))
                            bases.Add(ticker.Substring(0, ticker.Length - quote.Length));
                        else
                            bases.Add(ticker);
                    }
                }
            }
            return bases;
        }

        private static IEnumerable<Symbol> SelectSymbols(SymbolsRule symbolsRule, SymbolFilterModel filter, IWarmUpProvider warmUpProvider)
        {
            // 1. 先撈 Lean 符號資料庫 (SPDB) 已知的 binance CryptoFuture ticker
            //    未知的 ticker (例如 EDGEUSDT 這種新上幣) 若直接丟進 Universe，
            //    SecurityService.CreateSecurity 會丟 "Failed to resolve base currency" 整個 algorithm 掛掉
            var spdb = SymbolPropertiesDatabase.FromDataFolder();
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in spdb.GetSymbolPropertiesList(LeanMarket.Binance, SecurityType.CryptoFuture))
                known.Add(kvp.Key.Symbol);

            var nonCryptoBases = NonCryptoBases.Value;

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

                // 排除交割合約（ticker 含底線，例如 BTCUSDT_260626）
                if (ticker.Contains('_'))
                    continue;

                if (!known.Contains(ticker))
                {
                    // base asset 是 ISO 4217 貴金屬 / 商品簡稱 / 法幣 → EnsureCurrencyDataFeed 會走商品或換匯路徑崩潰，跳過
                    var baseAsset = ticker.EndsWith("USDT", StringComparison.OrdinalIgnoreCase) ? ticker.Substring(0, ticker.Length - 4) : ticker;
                    if (nonCryptoBases.Contains(baseAsset))
                        continue;

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
