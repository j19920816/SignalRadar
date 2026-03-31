namespace QuantConnect.Algorithm.CSharp.Interfaces
{
    public interface ISignalAlpha
    {
        string StrategyId { get; }
        string TimeFrame { get; }
        decimal GetStopPrice(Symbol symbol);
    }
}
