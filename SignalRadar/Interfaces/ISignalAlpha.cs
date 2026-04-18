using QuantConnect;

namespace SignalRadar.Algorithm.Interfaces
{
    public interface ISignalAlpha
    {
        string StrategyId { get; }
        string TimeFrame { get; }
        decimal GetStopPrice(Symbol symbol);
    }
}
