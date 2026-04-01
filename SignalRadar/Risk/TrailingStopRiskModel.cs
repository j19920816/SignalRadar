using QuantConnect.Algorithm.Framework.Alphas;
using QuantConnect.Algorithm.Framework.Portfolio;
using QuantConnect.Algorithm.Framework.Risk;
using System.Collections.Generic;

namespace QuantConnect.Algorithm.CSharp.Risk
{
    /// <summary>
    /// 固定百分比移動停損（僅回測使用）：
    ///   多倉：價格從最高點回落超過 trailingPercent → 平倉
    ///   空倉：價格從最低點反彈超過 trailingPercent → 平倉
    /// 停損線只往有利方向移動，不會往不利方向退縮。
    /// </summary>
    public class TrailingStopRiskModel : RiskManagementModel
    {
        private readonly decimal _trailingPercent;

        // 各 Symbol 目前的停損線價格
        private readonly Dictionary<Symbol, decimal> _trailingStop = new();

        // 各 Symbol 上次紀錄的倉位方向，用來偵測換倉
        private readonly Dictionary<Symbol, InsightDirection> _direction = new();

        public TrailingStopRiskModel(decimal trailingPercent = 0.01m)
        {
            _trailingPercent = trailingPercent;
        }

        /// <summary>
        /// 每根 K 棒都會被呼叫，逐一檢查所有已持倉的 Symbol 是否觸發停損。
        /// 若觸發，回傳 PortfolioTarget(quantity=0) 通知 ExecutionModel 平倉。
        /// </summary>
        public override IEnumerable<IPortfolioTarget> ManageRisk(QCAlgorithm algorithm, IPortfolioTarget[] targets)
        {
            foreach (var kvp in algorithm.Securities)
            {
                var symbol = kvp.Key;
                var holding = algorithm.Portfolio[symbol];
                var price = kvp.Value.Price;

                // 沒有持倉就清除紀錄，不處理
                if (!holding.Invested)
                {
                    _trailingStop.Remove(symbol);
                    _direction.Remove(symbol);
                    continue;
                }

                var currentDirection = holding.IsLong ? InsightDirection.Up : InsightDirection.Down;

                // 第一次持倉，或倉位方向剛改變（換倉）→ 重設停損線至進場價附近
                if (!_direction.TryGetValue(symbol, out var prevDirection) || prevDirection != currentDirection)
                {
                    _direction[symbol] = currentDirection;
                    _trailingStop[symbol] = holding.IsLong
                        ? price * (1m - _trailingPercent)   // 多倉初始停損：進場價往下 1%
                        : price * (1m + _trailingPercent);  // 空倉初始停損：進場價往上 1%
                }

                if (holding.IsLong)
                {
                    // 多倉：停損線 = 當前最高價 × (1 - 1%)，只往上移不往下移
                    var newStop = price * (1m - _trailingPercent);
                    if (newStop > _trailingStop[symbol])
                        _trailingStop[symbol] = newStop;

                    // 現價跌破停損線 → 觸發平倉
                    if (price <= _trailingStop[symbol])
                    {
                        _trailingStop.Remove(symbol);
                        _direction.Remove(symbol);
                        yield return new PortfolioTarget(symbol, 0);
                    }
                }
                else // IsShort
                {
                    // 空倉：停損線 = 當前最低價 × (1 + 1%)，只往下移不往上移
                    var newStop = price * (1m + _trailingPercent);
                    if (newStop < _trailingStop[symbol])
                        _trailingStop[symbol] = newStop;

                    // 現價漲破停損線 → 觸發平倉
                    if (price >= _trailingStop[symbol])
                    {
                        _trailingStop.Remove(symbol);
                        _direction.Remove(symbol);
                        yield return new PortfolioTarget(symbol, 0);
                    }
                }
            }
        }
    }
}
