using Giraffy.CryptoExchange;
using Giraffy.CryptoExchange.Common;
using Giraffy.CryptoExchange.RestCaller;
using QuantConnect;
using QuantConnect.Securities;
using System;
using System.Collections.Generic;

// 別名：避免跟 Giraffy.CryptoExchange.Common.Market 撞名
using LeanMarket = QuantConnect.Market;

namespace SignalRadar.Algorithm.Universe
{
    /// <summary>
    /// 紀錄 symbol 由哪個 UniverseSelectionModel 篩出。
    /// UniverseSelectionModel 篩完用 Set 覆寫該來源的最新名單；Alpha 用 Belongs 判斷是否屬於自己訂閱的來源。
    /// </summary>
    public static class UniverseSource
    {
        private static readonly Dictionary<string, HashSet<Symbol>> _map = new();
        private static readonly object _lock = new();

        public static void Set(string sourceId, IEnumerable<Symbol> symbols)
        {
            lock (_lock) _map[sourceId] = new HashSet<Symbol>(symbols);
        }

        public static bool Belongs(Symbol symbol, string sourceId)
        {
            lock (_lock) return _map.TryGetValue(sourceId, out var set) && set.Contains(symbol);
        }
    }

    /// <summary>
    /// Binance crypto universe 共用工具，給各個 UniverseSelectionModel reuse：
    ///   - NonCryptoBases：Lean SPDB 非加密市場推出的商品/法幣/CFD base asset 清單，用來擋下 NATGASUSDT、XPTUSDT 這類會走商品換匯路徑害 EnsureCurrencyDataFeed 崩潰的 ticker
    ///   - GetTradableUsdtPerpetuals：從 Giraffy SymbolsRule 撈所有可交易的 Binance USDT 永續合約 Symbol，過程中為 Lean SPDB 沒登記的新上幣即時補註
    /// </summary>
    public static class BinanceCryptoUniverse
    {
        // Lean 內部會把商品/法幣/CFD 走非 crypto 的 currency conversion 路徑，導致 EnsureCurrencyDataFeed 崩潰
        // （例如 NATGASUSDT、XPTUSDT、XAUUSDT）。從 SPDB 非加密市場動態推出這類 base asset 清單，
        // 之後 Binance 再上什麼 SILVERUSDT / PLATINUMUSDT 只要 Lean SPDB 有對應登記就會自動擋下，不用改程式碼。
        public static HashSet<string> NonCryptoBases => _nonCryptoBases.Value;

        private static readonly Lazy<HashSet<string>> _nonCryptoBases = new Lazy<HashSet<string>>(BuildNonCryptoBases);

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

        /// <summary>
        /// 從 Giraffy SymbolsRule 撈所有可交易的 Binance USDT 永續合約 Symbol。
        ///   - 跳過非 USDT 計價合約
        ///   - 跳過交割合約（ticker 含底線，例如 BTCUSDT_260626）
        ///   - 跳過商品/法幣/CFD base asset（NonCryptoBases）
        ///   - Lean 沒登記過的新上幣，依 Giraffy 的 TickPriceStep / QuantityStep 即時補進 SPDB，避免 SecurityService 建 Security 時 "Failed to resolve base currency" 崩潰
        /// </summary>
        public static List<Symbol> GetTradableUsdtPerpetuals(SymbolsRule symbolsRule)
        {
            // 先撈 Lean 符號資料庫 (SPDB) 已知的 binance CryptoFuture ticker
            var spdb = SymbolPropertiesDatabase.FromDataFolder();
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in spdb.GetSymbolPropertiesList(LeanMarket.Binance, SecurityType.CryptoFuture))
                known.Add(kvp.Key.Symbol);

            var nonCryptoBases = NonCryptoBases;

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

            return candidates;
        }
    }
}
