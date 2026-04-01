#region imports
using Giraffy.CryptoExchange;
using Giraffy.CryptoExchange.Common;
using QuantConnect.Algorithm.CSharp.Alphas;
using QuantConnect.Algorithm.CSharp.Execution;
using QuantConnect.Algorithm.CSharp.Infrastructure;
using QuantConnect.Algorithm.CSharp.PortfolioConstruction;
using QuantConnect.Algorithm.CSharp.Risk;
#endregion

namespace QuantConnect.Algorithm.CSharp
{
    public class SignalRadarAlgorithm : QCAlgorithm
    {
        public static SymbolsRule SymbolsRule = new SymbolsRule();
        private TcpSignalSender _sender;

        public override void Initialize()
        {
            // 取得個幣種規則
            ExchangeManager.G_ExchangeManager.TryGetOrAdd(Giraffy.CryptoExchange.Exchange.BinanceUsdtFuture, "", "" , out var rest, out _);
            if (rest != null)
            {
                SymbolsRule = rest.GetSymbolsAsync().Result;
            }

            // ── 回測區間與帳戶設定 ─────────────────────────────────────
            SetBenchmark(x => 0);
            SetStartDate(2024, 1, 1);
            SetEndDate(2024, 12, 31);
            SetAccountCurrency("USDT");
            SetCash("USDT", 100000);

            // ── 交易標的（1 分鐘解析度）─────────────────────────────────
            if (!LiveMode)
            {
                AddCrypto("BTCUSDT", Resolution.Minute, Market.Binance);
                AddCrypto("ETHUSDT", Resolution.Minute, Market.Binance);
            }
            else
            {
                AddCryptoFuture("BTCUSDT", Resolution.Minute, Market.Binance);
                AddCryptoFuture("ETHUSDT", Resolution.Minute, Market.Binance);
            }

            // ── Live 模式才建立 TCP 連線，回測不需要 ────────────────────
            if (LiveMode)
            {
                _sender = new TcpSignalSender("127.0.0.1", 2026);
                _sender.ConnectAsync().Wait();
            }

            // ── Framework 三層組裝 ────────────────────────────────────
            var alpha = new EngulfingCandlePatternAlpha();

            // 第一層：AlphaModel — 偵測吞噬形態，產生 Insight（Up / Down）
            AddAlpha(alpha);

            // 第二層：PCM — 把 Insight 轉成目標數量（總資產 × 10% / 現價）
            SetPortfolioConstruction(new FixedPortfolioConstructionModel(0.1m));

            // 第三層：ExecutionModel — 回測下市價單，Live 送 TCP 訊號
            SetExecution(new SignalExecutionModel(_sender, LiveMode, alpha));

            // ── 移動停損只在回測掛上；Live 由下單機自行管理 ─────────────
            if (!LiveMode)
                SetRiskManagement(new TrailingStopRiskModel(0.01m));
        }

        public override void OnEndOfAlgorithm()
        {
            // 演算法結束時關閉 TCP 連線
            _sender?.Dispose();
        }
    }
}
