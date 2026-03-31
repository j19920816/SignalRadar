#region imports
using QuantConnect.Algorithm.Framework.Portfolio;
using QuantConnect.Algorithm.Framework.Execution;
using QuantConnect.Algorithm.CSharp.Alphas;
using QuantConnect.Algorithm.CSharp.Infrastructure;
#endregion

namespace QuantConnect.Algorithm.CSharp
{
    public class SignalRadarAlgorithm : QCAlgorithm
    {
        private TcpSignalSender _sender;

        public override void Initialize()
        {
            SetBenchmark(x => 0);
            SetStartDate(2024, 1, 1);
            SetEndDate(2024, 12, 31);
            SetAccountCurrency("USDT");
            SetCash("USDT", 100000);

            AddCrypto("BTCUSDT", Resolution.Minute, Market.Binance);
            AddCrypto("ETHUSDT", Resolution.Minute, Market.Binance);

            SetPortfolioConstruction(new NullPortfolioConstructionModel());
            SetExecution(new NullExecutionModel());

            if (LiveMode)
            {
                _sender = new TcpSignalSender("127.0.0.1", 2026);
                _sender.ConnectAsync().Wait();
            }
            

            AddAlpha(new CandlePatternAlpha(this, _sender, LiveMode));
        }

        public override void OnEndOfAlgorithm()
        {
            _sender?.Dispose();
        }
    }
}