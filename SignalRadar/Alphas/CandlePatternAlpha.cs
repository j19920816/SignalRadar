using Giraffy.Util;
using QuantConnect.Algorithm.CSharp.Infrastructure;
using QuantConnect.Algorithm.CSharp.Interfaces;
using QuantConnect.Algorithm.CSharp.Signals;
using QuantConnect.Algorithm.Framework.Alphas;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Data.UniverseSelection;
using QuantConnect.Indicators;
using QuantConnect.Indicators.CandlestickPatterns;
using System;
using System.Collections.Generic;

namespace QuantConnect.Algorithm.CSharp.Alphas
{
    public class CandlePatternAlpha : AlphaModel, ISignalAlpha
    {
        public string StrategyId => "CandlePattern_1h";
        public string TimeFrame => "1h";

        private readonly QCAlgorithm _algorithm;
        private readonly TcpSignalSender _sender;
        private readonly bool _liveMode;

        // 每個 Symbol 各自的指標
        private readonly Dictionary<Symbol, EngulfingData> _data = new();

        public CandlePatternAlpha(QCAlgorithm algorithm, TcpSignalSender sender, bool liveMode)
        {
            _algorithm = algorithm;
            _sender = sender;
            _liveMode = liveMode;
        }

        public override void OnSecuritiesChanged(QCAlgorithm algorithm, SecurityChanges changes)
        {
            foreach (var security in changes.AddedSecurities)
            {
                if (!_data.ContainsKey(security.Symbol))
                {
                    _data[security.Symbol] = new EngulfingData { Engulfing = new Engulfing() };
                    
                    // 訂閱 TradeBar 更新指標
                    algorithm.RegisterIndicator(security.Symbol, _data[security.Symbol].Engulfing, Resolution.Hour);
                }
            }
        }

        public override IEnumerable<Insight> Update(QCAlgorithm algorithm, Slice data)
        {
            // 只在整點才觸發
            if (algorithm.Time.Minute != 0)
                return new List<Insight>();

            foreach (var kvp in _data)
            {
                var symbol = kvp.Key;
                var engulfing = kvp.Value.Engulfing;

                if (!engulfing.IsReady)
                    continue;

                if (!data.Bars.ContainsKey(symbol))
                    continue;

                var bar = data.Bars[symbol];
                kvp.Value.Bars.Add(bar);

                var value = engulfing.Current.Value;

                if (value > 0)  // 看漲吞噬
                {
                    var stopPrice = GetStopPrice(symbol);
                    SendOrOrder(symbol, InsightDirection.Up, stopPrice, "看漲吞噬");
                }
                else if (value < 0)  // 看跌吞噬
                {
                    var stopPrice = GetStopPrice(symbol);
                    SendOrOrder(symbol, InsightDirection.Down, stopPrice, "看跌吞噬");
                }
            }

            return new List<Insight>();
        }

        public decimal GetStopPrice(Symbol symbol)
        {
            if (!_data.TryGetValue(symbol, out var d) || !d.Bars.IsReady)
                return 0;

            // 前一根 bar 的 Low 當停損
            return d.Bars[1].Low;
        }

        private void SendOrOrder(Symbol symbol, InsightDirection direction, decimal stopPrice, string signal)
        {
            var price = _algorithm.Securities[symbol].Price;
            if (_liveMode)
            {
                var msg = new SignalMessage
                {
                    StrategyId = StrategyId,
                    Symbol = symbol.Value,
                    Side = direction == InsightDirection.Up ? Giraffy.CryptoExchange.Common.Side.Buy : Giraffy.CryptoExchange.Common.Side.Sell,
                    TimeFrame = TimeFrame,
                    Price = price,
                    StopPrice = stopPrice,
                    Timestamp = Web.GenerateTimeStamp(DateTime.UtcNow),
                };
                _sender.SendAsync(msg).Wait();
            }
            else
            {
                var holding = _algorithm.Portfolio[symbol];

                if (direction == InsightDirection.Up)
                {
                    // 有空倉就平倉
                    if (holding.IsShort)
                        _algorithm.MarketOrder(symbol, -holding.Quantity);
                    // 沒有多倉才開新多倉
                    else if (!holding.IsLong)
                    {
                        var quantity = (_algorithm.Portfolio.TotalPortfolioValue * 0.1m) / price;
                        _algorithm.MarketOrder(symbol, quantity);
                    }
                }
                else
                {
                    // 有多倉就平倉
                    if (holding.IsLong)
                        _algorithm.MarketOrder(symbol, -holding.Quantity);
                    // 沒有空倉才開新空倉
                    else if (!holding.IsShort)
                    {
                        var quantity = (_algorithm.Portfolio.TotalPortfolioValue * 0.1m) / price;
                        _algorithm.MarketOrder(symbol, -quantity);
                    }
                }
            }
        }
    }

    public class EngulfingData
    {
        public Engulfing Engulfing { get; set; }
        public RollingWindow<TradeBar> Bars { get; set; } = new RollingWindow<TradeBar>(3);
    }
}