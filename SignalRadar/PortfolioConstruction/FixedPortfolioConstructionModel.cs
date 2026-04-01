using QuantConnect.Algorithm.Framework.Alphas;
using QuantConnect.Algorithm.Framework.Portfolio;
using System.Collections.Generic;

namespace QuantConnect.Algorithm.CSharp.PortfolioConstruction
{
    /// <summary>
    /// 固定比例倉位模型：每次訊號固定用總資產的指定比例計算目標數量。
    /// 例如 fraction = 0.1 → 每次用總資產 10% 的價值換算成股數。
    /// </summary>
    public class FixedPortfolioConstructionModel : PortfolioConstructionModel
    {
        private readonly decimal _fraction;

        public FixedPortfolioConstructionModel(decimal fraction = 0.1m)
        {
            _fraction = fraction;
        }

        /// <summary>
        /// 將 AlphaModel 產生的 Insight 轉換成 PortfolioTarget（目標持倉數量）。
        /// ExecutionModel 收到後會計算與目前倉位的差值，決定下什麼單。
        /// </summary>
        public override IEnumerable<IPortfolioTarget> CreateTargets(QCAlgorithm algorithm, Insight[] insights)
        {
            foreach (var insight in insights)
            {
                var price = algorithm.Securities[insight.Symbol].Price;

                // 無法取得價格時跳過，避免除以零
                if (price == 0)
                    continue;

                // Up = 做多（+1）、Down = 做空（-1）、Flat = 平倉（0）
                decimal sign = insight.Direction == InsightDirection.Up ? 1m :
                               insight.Direction == InsightDirection.Down ? -1m : 0m;

                // 目標數量 = ±（總資產 × 比例）/ 現價
                var quantity = sign * (algorithm.Portfolio.TotalPortfolioValue * _fraction) / price;
                yield return new PortfolioTarget(insight.Symbol, quantity);
            }
        }
    }
}
