using QuantConnect;
using QuantConnect.Algorithm;
using QuantConnect.Data.Consolidators;
using QuantConnect.Data.Market;
using QuantConnect.Indicators;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace SignalRadar.Algorithm.Universe
{
    /// <summary>
    /// W 底（higher low 反轉型）篩選：最近一個 pivot low (low2) 比往前的 pivot low (low1) 高，
    /// 中間 peak 突破兩低點且距離合理，最新收盤離 peak 不超過 1.5 倍 ATR(14)。
    ///   Live：基底 RunAsync 平行拉 500 根 4H K 棒後呼叫 EvaluateBars。
    ///   回測：RegisterSymbol 為每個 symbol 掛 4H Consolidator，靠 Lean 的回測 feed 累積 ATR / RollingWindow。
    /// </summary>
    public class WBottomUniverseFilter : SymbolFilterBase
    {
        private const int FRACTAL_RADIUS = 2;                   // pivot low（K 線局部最低點）偵測半徑：一根 K 棒的 Low 同時低於前 2 根與後 2 根的 Low → 標記為 pivot low
        private const int MAX_LOW2_DISTANCE = 5;                // 最近 pivot low（low2）距最新 K 棒的最大距離（根），超過視為訊號過期
        private const int MIN_PIVOT_GAP = 10;                   // low2 到更早 low1 的最小 K 棒間距，避免兩低點太擠不成 W 形
        private const int MAX_PIVOT_GAP = 40;                   // low2 到更早 low1 的最大 K 棒間距，超過視為形態過寬
        private const int MIN_PEAK_SIDE = 3;                    // W 底兩低點之間的反彈高點（peak）距「較近那個低點」的最小 K 棒距離，避免 peak 緊貼低點
        private const int MAX_PEAK_SIDE = 25;                   // W 底兩低點之間的反彈高點（peak）距「較近那個低點」的最大 K 棒距離
        private const decimal MAX_ATR_MULTIPLE_TO_PEAK = 1.5m;  // 最新收盤距 peak.High 的最大容忍距離，以 ATR(14) 倍數表示；離頸線太遠則 false，已突破（差值為負）自動通過
        private const int ROLLING_WINDOW_SIZE = 60;             // 回測 K 棒滾動視窗：MAX_PIVOT_GAP(40) + MAX_LOW2_DISTANCE(5) + fractal 兩側各 2 根 + buffer ≈ 60

        private class FilterData
        {
            public AverageTrueRange Atr { get; } = new AverageTrueRange(14);
            public RollingWindow<TradeBar> Bars { get; } = new RollingWindow<TradeBar>(ROLLING_WINDOW_SIZE);
        }

        private readonly ConcurrentDictionary<Symbol, FilterData> _filterData = new();

        public WBottomUniverseFilter(string sourceId) : base(sourceId)
        {
        }

        protected override bool EvaluateBars(Symbol symbol, IEnumerable<TradeBar> bars)
        {
            var fd = new FilterData();
            foreach (var bar in bars)
            {
                fd.Atr.Update(bar);
                fd.Bars.Add(bar);
            }
            return PassFilter(fd);
        }

        public override void RegisterSymbol(QCAlgorithm algorithm, Symbol symbol)
        {
            if (!_filterData.TryAdd(symbol, new FilterData()))
                return;

            var consolidator = new TradeBarConsolidator(TimeSpan.FromHours(4));
            consolidator.DataConsolidated += OnFourHourBar;
            algorithm.SubscriptionManager.AddConsolidator(symbol, consolidator);
        }

        private void OnFourHourBar(object sender, TradeBar bar)
        {
            var fd = _filterData[bar.Symbol];
            fd.Atr.Update(bar);
            fd.Bars.Add(bar);

            var active = new HashSet<Symbol>();
            foreach (var kvp in _filterData)
            {
                if (PassFilter(kvp.Value))
                    active.Add(kvp.Key);
            }
            ActiveSymbols = active;
        }

        private bool PassFilter(FilterData fd)
        {
            if (!fd.Atr.IsReady)
                return false;

            if (fd.Bars.Count < FRACTAL_RADIUS * 2 + MIN_PIVOT_GAP + 1)
                return false;

            var low2 = FindRecentPivotLow(fd.Bars, MAX_LOW2_DISTANCE, FRACTAL_RADIUS);
            if (low2 == null)
                return false;

            var low1 = FindEarlierPivotLow(fd.Bars, low2.Value.Index, MIN_PIVOT_GAP, MAX_PIVOT_GAP, low2.Value.Low, FRACTAL_RADIUS);
            if (low1 == null)
                return false;

            var peak = FindPeakBetween(fd.Bars, low2.Value.Index, low1.Value.Index);
            if (peak == null)
                return false;

            var twoLowsMax = low1.Value.Low > low2.Value.Low ? low1.Value.Low : low2.Value.Low;
            if (peak.Value.High <= twoLowsMax)
                return false;

            var distToLow2 = peak.Value.Index - low2.Value.Index;
            var distToLow1 = low1.Value.Index - peak.Value.Index;
            var nearestSide = distToLow2 < distToLow1 ? distToLow2 : distToLow1;
            if (nearestSide < MIN_PEAK_SIDE || nearestSide > MAX_PEAK_SIDE)
                return false;

            var lastClose = fd.Bars[0].Close;
            if (peak.Value.High - lastClose > MAX_ATR_MULTIPLE_TO_PEAK * fd.Atr.Current.Value)
                return false;

            return true;
        }

        // RollingWindow 索引：[0] 最新、index 越大時間越早。
        // 「最近」= 索引較小、「往前」= 索引較大。

        private static (int Index, decimal Low)? FindRecentPivotLow(RollingWindow<TradeBar> bars, int maxDistance, int radius)
        {
            var maxIndex = radius + maxDistance - 1;
            if (maxIndex > bars.Count - 1 - radius)
                maxIndex = bars.Count - 1 - radius;

            for (int i = radius; i <= maxIndex; i++)
            {
                if (IsPivotLow(bars, i, radius))
                    return (i, bars[i].Low);
            }
            return null;
        }

        private static (int Index, decimal Low)? FindEarlierPivotLow(RollingWindow<TradeBar> bars, int afterIndex, int minGap, int maxGap, decimal mustBeLowerThan, int radius)
        {
            var start = afterIndex + minGap;
            var end = afterIndex + maxGap;
            var upper = bars.Count - 1 - radius;
            if (end > upper)
                end = upper;

            for (int i = start; i <= end; i++)
            {
                if (!IsPivotLow(bars, i, radius))
                    continue;
                if (bars[i].Low < mustBeLowerThan)
                    return (i, bars[i].Low);
            }
            return null;
        }

        private static (int Index, decimal High)? FindPeakBetween(RollingWindow<TradeBar> bars, int low2Index, int low1Index)
        {
            int peakIdx = -1;
            decimal peakHigh = decimal.MinValue;
            for (int i = low2Index + 1; i < low1Index; i++)
            {
                if (bars[i].High > peakHigh)
                {
                    peakHigh = bars[i].High;
                    peakIdx = i;
                }
            }
            if (peakIdx < 0)
                return null;
            return (peakIdx, peakHigh);
        }

        private static bool IsPivotLow(RollingWindow<TradeBar> bars, int index, int radius)
        {
            var center = bars[index].Low;
            for (int k = 1; k <= radius; k++)
            {
                if (center >= bars[index - k].Low)
                    return false;
                if (center >= bars[index + k].Low)
                    return false;
            }
            return true;
        }
    }
}
