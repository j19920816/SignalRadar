using QuantConnect.Algorithm.Framework.Alphas;
using QuantConnect.Algorithm.Framework.Portfolio;
using QuantConnect.Algorithm.Framework.Risk;
using System.Collections.Generic;

namespace QuantConnect.Algorithm.CSharp.Risk
{
    /// <summary>
    /// 兩階段停損模型（僅回測使用）：
    ///   Phase 1：進場後固定停損，虧損超過 initialStopPercent → 平倉
    ///   Phase 2：獲利達到 activationPercent 後啟動移動停損，
    ///            從最高獲利點回檔超過 retraceFraction → 平倉
    ///            Phase 2 一旦啟動不會退回 Phase 1
    /// </summary>
    public class TrailingStopRiskModel : RiskManagementModel
    {
        private readonly decimal _initialStopPercent;  // Phase 1 固定停損幅度（預設 1%）
        private readonly decimal _activationPercent;   // 啟動 Phase 2 所需獲利幅度（預設 1%）
        private readonly decimal _retraceFraction;     // Phase 2 回檔比例（預設 50%）

        // 各 Symbol 目前追蹤的最佳價格（多倉=最高點，空倉=最低點）
        private readonly Dictionary<Symbol, decimal> _peak = new();

        // 各 Symbol 是否已進入 Phase 2
        private readonly Dictionary<Symbol, bool> _phase2Active = new();

        // 各 Symbol 上次紀錄的倉位方向，用來偵測換倉
        private readonly Dictionary<Symbol, InsightDirection> _direction = new();

        public TrailingStopRiskModel(decimal initialStopPercent = 0.01m, decimal activationPercent  = 0.01m, decimal retraceFraction    = 0.5m)
        {
            _initialStopPercent = initialStopPercent;
            _activationPercent  = activationPercent;
            _retraceFraction    = retraceFraction;
        }

        public override IEnumerable<IPortfolioTarget> ManageRisk(QCAlgorithm algorithm, IPortfolioTarget[] targets)
        {
            foreach (var kvp in algorithm.Securities)
            {
                var symbol  = kvp.Key;
                var holding = algorithm.Portfolio[symbol];
                var price   = kvp.Value.Price;

                // 沒有持倉就清除紀錄，不處理
                if (!holding.Invested)
                {
                    _peak.Remove(symbol);
                    _phase2Active.Remove(symbol);
                    _direction.Remove(symbol);
                    continue;
                }

                var bar  = kvp.Value.Cache.GetData<Data.Market.TradeBar>();
                var high = bar != null ? bar.High : price;
                var low  = bar != null ? bar.Low  : price;

                var entryPrice       = holding.AveragePrice;
                var currentDirection = holding.IsLong ? InsightDirection.Up : InsightDirection.Down;

                // 換倉時重設所有狀態
                if (!_direction.TryGetValue(symbol, out var prevDirection) || prevDirection != currentDirection)
                {
                    _direction[symbol]    = currentDirection;
                    _phase2Active[symbol] = false;
                    _peak[symbol]         = holding.IsLong ? high : low;
                }

                if (holding.IsLong)
                {
                    // 更新最高點
                    if (high > _peak[symbol])
                        _peak[symbol] = high;

                    // 獲利達到 activationPercent → 啟動 Phase 2
                    if (!_phase2Active[symbol] && price >= entryPrice * (1m + _activationPercent))
                        _phase2Active[symbol] = true;

                    bool triggered;
                    if (_phase2Active[symbol])
                    {
                        // Phase 2：停損線 = 進場價 + retraceFraction × (最高點 - 進場價)
                        var stopPrice = entryPrice + _retraceFraction * (_peak[symbol] - entryPrice);
                        triggered = price <= stopPrice;
                    }
                    else
                    {
                        // Phase 1：固定停損線 = 進場價 × (1 - initialStopPercent)
                        var stopPrice = entryPrice * (1m - _initialStopPercent);
                        triggered = price <= stopPrice;
                    }

                    if (triggered)
                    {
                        _peak.Remove(symbol);
                        _phase2Active.Remove(symbol);
                        _direction.Remove(symbol);
                        yield return new PortfolioTarget(symbol, 0);
                    }
                }
                else // IsShort
                {
                    // 更新最低點
                    if (low < _peak[symbol])
                        _peak[symbol] = low;

                    // 獲利達到 activationPercent → 啟動 Phase 2
                    if (!_phase2Active[symbol] && price <= entryPrice * (1m - _activationPercent))
                        _phase2Active[symbol] = true;

                    bool triggered;
                    if (_phase2Active[symbol])
                    {
                        // Phase 2：停損線 = 進場價 - retraceFraction × (進場價 - 最低點)
                        var stopPrice = entryPrice - _retraceFraction * (entryPrice - _peak[symbol]);
                        triggered = price >= stopPrice;
                    }
                    else
                    {
                        // Phase 1：固定停損線 = 進場價 × (1 + initialStopPercent)
                        var stopPrice = entryPrice * (1m + _initialStopPercent);
                        triggered = price >= stopPrice;
                    }

                    if (triggered)
                    {
                        _peak.Remove(symbol);
                        _phase2Active.Remove(symbol);
                        _direction.Remove(symbol);
                        yield return new PortfolioTarget(symbol, 0);
                    }
                }
            }
        }
    }
}
