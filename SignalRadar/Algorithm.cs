#region imports
using Giraffy.CryptoExchange;
using Giraffy.CryptoExchange.Common;
using Giraffy.CryptoExchange.RestCaller;
using QuantConnect;
using QuantConnect.Algorithm;
using QuantConnect.Brokerages;
using QuantConnect.Configuration;
using QuantConnect.Data;
using QuantConnect.Data.UniverseSelection;
using SignalRadar.Algorithm.Alphas;
using SignalRadar.Algorithm.Backtest;
using SignalRadar.Algorithm.Execution;
using SignalRadar.Algorithm.Interfaces;
using SignalRadar.Algorithm.Network;
using SignalRadar.Algorithm.Providers;
using SignalRadar.Algorithm.Universe;
using SignalRadar.BacktestModels;
using SignalRadar.PortfolioConstruction;
using System;
#endregion

namespace SignalRadar.Algorithm
{
    public class SignalRadarAlgorithm : QCAlgorithm
    {
        public static RestApiCaller ApiCaller;
        public static SymbolsRule SymbolsRule = new SymbolsRule();
        private TcpSignalSender _sender;
        private SymbolFilterBase _symbolFilter;
        private IWarmUpProvider _warmUpProvider;

        public override void Initialize()
        {
            // 時區：台北 (UTC+8)
            SetTimeZone("Asia/Taipei");

            // 回測區間與帳戶設定
            SetStartDate(2024, 1, 1);
            SetEndDate(2024, 12, 31);
            SetAccountCurrency("USDT");
            SetCash("USDT", 100000);

            // 交易標的
            // Live：用 ScheduledUniverseSelectionModel 每 4H 動態篩選 Binance USDT 計價標的（spot 或永續，由 environment 決定）
            // 回測：只訂閱 BTC / ETH，避免資料量過大
            _warmUpProvider = new BinanceWarmUpProvider();
            _symbolFilter = new LiquidityAdxObvFilter(nameof(LiquidityAdxObvFilter));

            if (LiveMode)
            {
                // 由 Lean environment 判斷市場類型：含 "future" → USDT 永續，否則 spot
                // 對應 environment：spot → live-binance、futures → live-futures-binance
                var environment = Config.Get("environment");
                var isSpot = !environment.Contains("future", StringComparison.OrdinalIgnoreCase);
                var exchange = isSpot ? Giraffy.CryptoExchange.Exchange.Binance : Giraffy.CryptoExchange.Exchange.BinanceUsdtFuture;
                var securityType = isSpot ? SecurityType.Crypto : SecurityType.CryptoFuture;

                // 取得個幣種規則
                ExchangeManager.G_ExchangeManager.TryGetOrAdd(exchange, "", "", out ApiCaller, out _);
                if (ApiCaller != null)
                    SymbolsRule = ApiCaller.GetSymbolsAsync().Result;

                // 訂閱解析度： K 棒由 Consolidator 合成
                // （Tick 也可用，但上次把 300+ 支 Tick 一次灌進 websocket 會被 cancel；
                //   現在走 UniverseSelection 數量可控，若要換回 Tick，Alpha 的 Consolidator 也要同步改 TickConsolidator）
                UniverseSettings.Resolution = Resolution.Second;

                // 啟動立刻篩一次（拉上一個 4H boundary 的收盤資料）+ 之後每日 00/04/08/12/16/20 UTC
                AddUniverseSelection(new FilteredUniverseSelectionModel(DateRules.EveryDay(), new StartupTimeRule(), SymbolsRule, securityType, _symbolFilter, _warmUpProvider, UniverseSettings));
            }
            else
            {
                AddCryptoFuture("BTCUSDT", Resolution.Minute, QuantConnect.Market.Binance);
                AddCryptoFuture("ETHUSDT", Resolution.Minute, QuantConnect.Market.Binance);
            }

            // Live 模式才建立 TCP 連線，回測不需要
            if (LiveMode)
            {
                _sender = new TcpSignalSender("127.0.0.1", DateTime.Today.Year);
                _sender.ConnectAsync().Wait();
            }
            else  
            {
                // Phase 1：虧損 1% 停損, 獲利 1% 啟動移動停損, Phase 2：從最高點回檔 50% 出場
                SetRiskManagement(new TrailingStopRiskModel(initialStopPercent: 0.01m, activationPercent: 0.01m, retraceFraction: 0.5m));

                // 手續費：每筆成交金額的 0.2%（嚴格回測用，實際為 0.1%）
                foreach (var security in Securities.Values)
                    security.SetFeeModel(new PercentageFeeModel(0.002m));

                // 載入歷史資料
                HistoryDataLoader.Load(BrokerageName.Binance, StartDate, EndDate, SecurityType.CryptoFuture, Securities, Resolution.Minute);
            }

            // Framework 三層組裝
            // Live 綁定篩選器的來源 id；回測無 UniverseSelectionModel，傳 null 不過濾來源
            var alphaSourceId = LiveMode ? _symbolFilter.SourceId : null;
            var engulfingCandleAlpha = new EngulfingCandlePatternAlpha(_warmUpProvider, _symbolFilter, alphaSourceId);

            // 第一層：AlphaModel — 偵測吞噬形態，產生 Insight（Up / Down）
            AddAlpha(engulfingCandleAlpha);

            // 第二層：PCM — 把 Insight 轉成目標數量（總資產 × 10% / 現價）
            SetPortfolioConstruction(new FixedPortfolioConstructionModel());

            // 第三層：ExecutionModel — 回測下市價單，Live 送 TCP 訊號
            SetExecution(new SignalExecutionModel(_sender, LiveMode, engulfingCandleAlpha));         
        }

        /*
        private DateTime _lastDataLog = DateTime.MinValue;
        public override void OnData(Slice slice)
        {
            // 每 10 秒印一次資料流狀態（避免 Tick 級別 log 爆量）
            if ((DateTime.UtcNow - _lastDataLog).TotalSeconds < 10) 
                return;
            _lastDataLog = DateTime.UtcNow;
            Log($"[OnData] Time={slice.Time:HH:mm:ss} Ticks={slice.Ticks.Count} Bars={slice.Bars.Count} QuoteBars={slice.QuoteBars.Count}");
        }
        */

        public override void OnEndOfAlgorithm()
        {
            // 演算法結束時關閉 TCP 連線
            _sender?.Dispose();
        }
    }
}
