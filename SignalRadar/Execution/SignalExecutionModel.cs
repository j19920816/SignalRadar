using Giraffy.Util;
using SignalRadar.Algorithm.Network;
using SignalRadar.Algorithm.Interfaces;
using SignalRadar.Algorithm.Signals;
using QuantConnect.Algorithm.Framework.Execution;
using QuantConnect.Algorithm.Framework.Portfolio;
using System;
using System.Collections.Generic;
using System.Linq;
using QuantConnect.Algorithm;

namespace SignalRadar.Algorithm.Execution
{
    /// <summary>
    /// 執行層：接收 PortfolioTarget，依模式決定行為。
    ///   回測：直接對 Lean 下市價單
    ///   Live ：把訊號序列化成 JSON 透過 TCP 送給下單機，送出後不再介入
    /// 多 Alpha 並行時靠 target.Tag(由 PCM 從 Insight.SourceModel 帶過來)反查對應 alpha 取得 StrategyId / TimeFrame / StopPrice。
    /// </summary>
    public class SignalExecutionModel : ExecutionModel
    {
        private readonly TcpSignalSender _sender;
        private readonly bool _liveMode;
        // key = StrategyId(= Alpha.Name = 類別名稱),value = 發訊號的 Alpha
        private readonly IReadOnlyDictionary<string, ISignalAlpha> _alphas;

        public SignalExecutionModel(TcpSignalSender sender, bool liveMode, IEnumerable<ISignalAlpha> alphas)
        {
            _sender = sender;
            _liveMode = liveMode;
            _alphas = alphas.ToDictionary(a => a.StrategyId);
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
                if (!SignalRadarAlgorithm.SymbolsRule.TryGetValue(symbol.Value, out var rule))
                    continue;

                // 已有倉位時只接受平倉訊號（target.Quantity == 0 來自 RiskModel）
                // 忽略新的進場訊號，等出場後才重新進場
                if (holding.Invested && target.Quantity != 0)
                    continue;

                // diff > 0 → 需要買進（開多 or 平空）
                // diff < 0 → 需要賣出（開空 or 平多）
                // diff = 0 → 倉位已達目標，不動作
                var diff = target.Quantity - holding.Quantity;

                var minQty = rule.MinTradeQuantity;
                if (Math.Abs(diff) < minQty)
                    continue;

                if (_liveMode)
                {
                    // target.Tag = StrategyId(由 PCM 從 Insight.SourceModel 帶過來),反查對應 alpha
                    // (Live 沒掛 RiskModel,所有 target 都應從我們自家 PCM 出來;tag 為空視為無 alpha 上下文跳過)
                    if (string.IsNullOrEmpty(target.Tag) || !_alphas.TryGetValue(target.Tag, out var alpha))
                        continue;

                    // Live：封裝成 SignalMessage 透過 TCP 送出，後續由下單機處理
                    var price = algorithm.Securities[symbol].Price;
                    var stopPrice = alpha.GetStopPrice(symbol);
                    var side = diff > 0 ? Giraffy.CryptoExchange.Common.Side.Buy : Giraffy.CryptoExchange.Common.Side.Sell;

                    var msg = new SignalMessage
                    {
                        StrategyId = alpha.StrategyId,
                        Symbol = symbol.Value,
                        Side = side,
                        TimeFrame = alpha.TimeFrame,
                        Price = price,
                        StopPrice = stopPrice,
                        Timestamp = Web.GenerateTimeStamp(DateTime.UtcNow),
                    };

                    // Fire-and-forget：避免 TCP 卡住阻塞 Lean 主迴圈
                    // 完成 / 失敗各自寫 log,不等送出結果
                    _ = _sender.SendAsync(msg).ContinueWith(t =>
                    {
                        if (t.IsFaulted)
                            algorithm.Error($"[Signal FAIL] {msg.Symbol} {msg.Side} → {t.Exception?.GetBaseException().Message}");
                        else
                            algorithm.Log($"[Signal OK  ] {msg.StrategyId} {msg.Symbol} {msg.Side} TimeFrame={msg.TimeFrame} Price={msg.Price} Stop={msg.StopPrice} sent");
                    });
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
