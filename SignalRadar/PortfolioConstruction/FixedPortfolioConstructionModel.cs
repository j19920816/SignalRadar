using QuantConnect.Algorithm.Framework.Alphas;
using QuantConnect.Algorithm.Framework.Portfolio;
using System.Collections.Generic;

namespace QuantConnect.Algorithm.CSharp.PortfolioConstruction
{
    /// <summary>
    /// 固定比例倉位模型：每個 Symbol 平均分配總資產，比例 = 1 / 註冊 Symbol 數量。
    /// 例如 2 個 Symbol → 各 50%；5 個 Symbol → 各 20%。
    /// Symbol 數量變動時自動重算，不需手動調整。
    /// </summary>
    public class FixedPortfolioConstructionModel : PortfolioConstructionModel
    {
        /// <summary>
        /// 將 AlphaModel 產生的 Insight 轉換成 PortfolioTarget（目標持倉數量）。
        /// ExecutionModel 收到後會計算與目前倉位的差值，決定下什麼單。
        /// </summary>
        public override IEnumerable<IPortfolioTarget> CreateTargets(QCAlgorithm algorithm, Insight[] insights)
        {
            // 每個 Symbol 分配到的比例 = 1 / 總 Symbol 數
            var symbolCount = algorithm.Securities.Count;  
            if (symbolCount == 0)
                yield break;

            var fraction = 1m / symbolCount;

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
                var quantity = sign * (algorithm.Portfolio.TotalPortfolioValue * fraction) / price;
                yield return new PortfolioTarget(insight.Symbol, quantity);
            }
        }
    }
}
