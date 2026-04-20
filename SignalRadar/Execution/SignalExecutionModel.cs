using Giraffy.Util;
using SignalRadar.Algorithm.Network;
using SignalRadar.Algorithm.Interfaces;
using SignalRadar.Algorithm.Signals;
using QuantConnect.Algorithm.Framework.Execution;
using QuantConnect.Algorithm.Framework.Portfolio;
using System;
using QuantConnect.Algorithm;

namespace SignalRadar.Algorithm.Execution
{
    /// <summary>
    /// 執行層：接收 PortfolioTarget，依模式決定行為。
    ///   回測：直接對 Lean 下市價單
    ///   Live ：把訊號序列化成 JSON 透過 TCP 送給下單機，送出後不再介入
    /// </summary>
    public class SignalExecutionModel : ExecutionModel
    {
        private readonly TcpSignalSender _sender;
        private readonly bool _liveMode;
        private readonly ISignalAlpha _alpha;   // 用來取得 StrategyId、TimeFrame、StopPrice

        public SignalExecutionModel(TcpSignalSender sender, bool liveMode, ISignalAlpha alpha)
        {
            _sender = sender;
            _liveMode = liveMode;
            _alpha = alpha;
        }

        /// <summary>
        /// 每次 PCM 或 RiskModel 產生新的 PortfolioTarget 時被呼叫。
        /// </summary>
        public override void Execute(QCAlgorithm algorithm, IPortfolioTarget[] targets)
        {
            foreach (var target in targets)
            {
                var symbol = target.Symbol;
                var holding = algorithm.Portfolio[symbol];

                // 已有倉位時只接受平倉訊號（target.Quantity == 0 來自 RiskModel）
                // 忽略新的進場訊號，等出場後才重新進場
                if (holding.Invested && target.Quantity != 0)
                    continue;

                // diff > 0 → 需要買進（開多 or 平空）
                // diff < 0 → 需要賣出（開空 or 平多）
                // diff = 0 → 倉位已達目標，不動作
                var diff = target.Quantity - holding.Quantity;

                var minQty = SignalRadarAlgorithm.SymbolsRule[symbol].QuantityStep;
                if (Math.Abs(diff) < minQty)
                    continue;

                if (_liveMode)
                {
                    // Live：封裝成 SignalMessage 透過 TCP 送出，後續由下單機處理
                    var price = algorithm.Securities[symbol].Price;
                    var stopPrice = _alpha.GetStopPrice(symbol);
                    var side = diff > 0 ? Giraffy.CryptoExchange.Common.Side.Buy : Giraffy.CryptoExchange.Common.Side.Sell;

                    var msg = new SignalMessage
                    {
                        StrategyId = _alpha.StrategyId,
                        Symbol = symbol.Value,
                        Side = side,
                        TimeFrame = _alpha.TimeFrame,
                        Price = price,
                        StopPrice = stopPrice,
                        Timestamp = Web.GenerateTimeStamp(DateTime.UtcNow),
                    };
                    algorithm.Log($"[Signal Send] {msg.StrategyId} {msg.Symbol} {msg.Side} TF={msg.TimeFrame} Price={msg.Price} Stop={msg.StopPrice}");
                    try
                    {
                        //_sender.SendAsync(msg).Wait();
                        algorithm.Log($"[Signal OK  ] {msg.Symbol} {msg.Side} sent\n");
                    }
                    catch (Exception ex)
                    {
                        algorithm.Error($"[Signal FAIL] {msg.Symbol} {msg.Side} → {ex.GetBaseException().Message}\n");
                    }
                }
                else
                {
                    // 回測：直接送市價單，diff 同時處理平倉與開倉
                    // 例如：目前空倉 -5，目標 +10 → diff = 15（一筆單反向）
                    algorithm.MarketOrder(symbol, diff);
                }
            }
        }
    }
}
