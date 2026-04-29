using QuantConnect;
using QuantConnect.Algorithm;
using QuantConnect.Algorithm.Framework.Selection;
using QuantConnect.Data.Market;
using QuantConnect.Data.UniverseSelection;
using QuantConnect.Scheduling;
using System;
using System.Collections.Generic;


namespace SignalRadar.Algorithm.Universe
{
    public class WBottomUniverseFilter : SymbolFilterBase
    {
        public WBottomUniverseFilter(string sourceId) : base(sourceId)
        {
        }

        public override void RegisterSymbol(QCAlgorithm algorithm, Symbol symbol)
        {
            throw new NotImplementedException();
        }

        protected override bool EvaluateBars(Symbol symbol, IEnumerable<TradeBar> bars)
        {
            throw new NotImplementedException();
        }
    }
}
