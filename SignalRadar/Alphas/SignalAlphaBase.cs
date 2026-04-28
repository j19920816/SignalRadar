using QuantConnect;
using QuantConnect.Algorithm.Framework.Alphas;
using SignalRadar.Algorithm.Interfaces;
using SignalRadar.Algorithm.Universe;

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
        public abstract decimal GetStopPrice(Symbol symbol);

        protected bool BelongsToSource(Symbol symbol)
            => _sourceId == null || UniverseSource.Belongs(symbol, _sourceId);
    }
}
