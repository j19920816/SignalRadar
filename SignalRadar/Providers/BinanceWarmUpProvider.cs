using QuantConnect.Algorithm.CSharp.Interfaces;
using QuantConnect.Data.Market;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace QuantConnect.Algorithm.CSharp.Providers
{
    public class BinanceWarmUpProvider : IWarmUpProvider
    {
        public async Task<IEnumerable<TradeBar>> GetBarsAsync(Symbol symbol, TimeSpan barInterval, int count)
        {
            var result = new List<TradeBar>();
            if (SignalRadarAlgorithm.ApiCaller != null)
            {
                var candleSticks = await SignalRadarAlgorithm.ApiCaller.GetKlinesAsync(symbol.Value, (int)barInterval.TotalSeconds, null, null, count);
                if (candleSticks != null)
                {
                    int i;
                    for (i = 0; i < candleSticks.Count; ++i)
                    {
                        // UTC time
                        var openDateTime = candleSticks[i].OpenTime;
                        var open = candleSticks[i].OpenPrice.Value;
                        var high = candleSticks[i].HighPrice.Value;
                        var low = candleSticks[i].LowPrice.Value;
                        var close = candleSticks[i].ClosePrice.Value;
                        var volume = candleSticks[i].Volume.Value;  
                        var bar = new TradeBar(openDateTime, symbol, open, high, low, close, volume);
                        result.Add(bar);
                    }
                }

            }
            return result;
        }
    }
}
