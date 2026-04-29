using QuantConnect;
using System;

namespace SignalRadar.Algorithm.Interfaces
{
    public interface ISignalAlpha
    {
        string StrategyId { get; }
        string TimeFrame { get; }
        TimeSpan TimeSpanBar { get; }
        decimal GetStopPrice(Symbol symbol);
    }
}
