using QuantConnect;
using QuantConnect.Algorithm;
using QuantConnect.Algorithm.Framework.Alphas;
using QuantConnect.Data.Consolidators;
using QuantConnect.Data.Market;
using SignalRadar.Algorithm.Interfaces;
using SignalRadar.Algorithm.Universe;
using System;
using System.Threading.Tasks;

namespace SignalRadar.Algorithm.Alphas
{
    /// <summary>
    /// 訊號型 Alpha 共用基底：統一處理 UniverseSelectionModel 來源綁定，並強制實作 ISignalAlpha 三個成員。
    /// 子類別 Update 內用 BelongsToSource 過濾標的，未來改 params string[] 只需動本類別。
    /// </summary>
    public abstract class SignalAlphaBase : AlphaModel, ISignalAlpha
    {
        // sourceId = null 代表不限制來源（回測用：標的由 AddCryptoFuture 直接訂閱，沒走 UniverseSelectionModel）。
        private readonly string _sourceId;

        protected SignalAlphaBase(string sourceId = null)
        {
            _sourceId = sourceId;
        }

        public abstract string StrategyId { get; }
        public abstract string TimeFrame { get; }
        public abstract TimeSpan TimeSpanBar { get; }
        public abstract decimal GetStopPrice(Symbol symbol);

        protected bool BelongsToSource(Symbol symbol) => _sourceId == null || UniverseSource.Belongs(symbol, _sourceId);

        /// <summary>
        /// 統一 warm-up → consolidator 註冊順序,子類別不要自己排這兩步。
        /// 順序不能反:若先掛 consolidator 再 await 拉歷史 K 棒,await 期間真實 minute bar 會先觸發 consolidator handler 把 live bar 餵進指標,
        /// 接著 warm-up 又把更舊的歷史 bar 補進去 → 指標時序錯亂。
        /// Live: 先用 REST 拉 warmUpBarCount 根歷史 K 棒呼叫 feedWarmUpBar,完成後才掛 consolidator。
        /// 回測: 略過 warm-up,直接掛 consolidator。
        /// </summary>
        protected async Task WarmUpAndAttachConsolidatorAsync(
            QCAlgorithm algorithm,
            Symbol symbol,
            IWarmUpProvider warmUpProvider,
            int warmUpBarCount,
            Action<Symbol, TradeBar> feedWarmUpBar,
            EventHandler<TradeBar> onConsolidatedBar)
        {
            if (algorithm.LiveMode && warmUpProvider != null && warmUpBarCount > 0)
            {
                var bars = await warmUpProvider.GetBarsAsync(symbol, TimeSpanBar, warmUpBarCount);
                foreach (var bar in bars)
                    feedWarmUpBar(symbol, bar);
            }

            var consolidator = new TradeBarConsolidator(TimeSpanBar);
            consolidator.DataConsolidated += onConsolidatedBar;
            algorithm.SubscriptionManager.AddConsolidator(symbol, consolidator);
        }
    }
}
