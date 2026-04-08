#region imports
using Giraffy.CryptoExchange;
using Giraffy.CryptoExchange.Common;
using Giraffy.CryptoExchange.RestCaller;
using QuantConnect.Algorithm.CSharp.Alphas;
using QuantConnect.Algorithm.CSharp.Backtest;
using QuantConnect.Algorithm.CSharp.Execution;
using QuantConnect.Algorithm.CSharp.Network;
using QuantConnect.Algorithm.CSharp.Providers;
using SignalRadar.BacktestModels;
using SignalRadar.PortfolioConstruction;
#endregion

namespace QuantConnect.Algorithm.CSharp
{
    public class SignalRadarAlgorithm : QCAlgorithm
    {
        public static RestApiCaller ApiCaller;
        public static SymbolsRule SymbolsRule = new SymbolsRule();
        private TcpSignalSender _sender;

        public override void Initialize()
        {
            // 取得個幣種規則
            Giraffy.CryptoExchange.Exchange exchange = Giraffy.CryptoExchange.Exchange.BinanceUsdtFuture;
            ExchangeManager.G_ExchangeManager.TryGetOrAdd(exchange, "", "", out ApiCaller, out _);
            if (ApiCaller != null)
                SymbolsRule = ApiCaller.GetSymbolsAsync().Result;

            // 回測區間與帳戶設定
            SetBenchmark(x => 0);
            SetStartDate(2024, 1, 1);
            SetEndDate(2024, 12, 31);
            SetAccountCurrency("USDT");
            SetCash("USDT", 100000);

            // 交易標的（1 分鐘解析度）
            // Live：動態訂閱 Binance 合約全部幣種
            // 回測：只訂閱 BTC / ETH，避免資料量過大
            if (LiveMode)
            {
                foreach (var ticker in SymbolsRule.Keys)
                    AddCryptoFuture(ticker, Resolution.Tick, Market.Binance);
            }
            else
            {
                AddCryptoFuture("BTCUSDT", Resolution.Minute, Market.Binance);
                AddCryptoFuture("ETHUSDT", Resolution.Minute, Market.Binance);
            }

            // Live 模式才建立 TCP 連線，回測不需要
            if (LiveMode)
            {
                _sender = new TcpSignalSender("127.0.0.1", 2026);
                _sender.ConnectAsync().Wait();
            }
            else  
            {
                // Phase 1：虧損 1% 停損, 獲利 1% 啟動移動停損, Phase 2：從最高點回檔 50% 出場
                SetRiskManagement(new TrailingStopRiskModel(initialStopPercent: 0.01m, activationPercent: 0.01m, retraceFraction: 0.5m));

                // 手續費：每筆成交金額的 0.2%（嚴格回測用，實際為 0.1%）
                foreach (var security in Securities.Values)
                    security.SetFeeModel(new PercentageFeeModel(0.002m));
            }

            // Framework 三層組裝
            // IWarmUpProvider 實作完成後替換 null
            var engulfingCandleAlpha = new EngulfingCandlePatternAlpha(new BinanceWarmUpProvider());

            // 第一層：AlphaModel — 偵測吞噬形態，產生 Insight（Up / Down）
            AddAlpha(engulfingCandleAlpha);

            // 第二層：PCM — 把 Insight 轉成目標數量（總資產 × 10% / 現價）
            SetPortfolioConstruction(new FixedPortfolioConstructionModel());

            // 第三層：ExecutionModel — 回測下市價單，Live 送 TCP 訊號
            SetExecution(new SignalExecutionModel(_sender, LiveMode, engulfingCandleAlpha));         
        }

        public override void OnEndOfAlgorithm()
        {
            // 演算法結束時關閉 TCP 連線
            _sender?.Dispose();
        }
    }
}
