using QuantConnect.Algorithm.CSharp.Interfaces;
using QuantConnect.Data.Market;
using System;
using System.Collections.Generic;

namespace QuantConnect.Algorithm.CSharp.Providers
{
    public class BinanceWarmUpProvider : IWarmUpProvider
    {
        public IEnumerable<TradeBar> GetBars(Symbol symbol, TimeSpan barSize, int count)
        {
            // TODO: 呼叫你的 library 取得歷史 K 棒
            throw new NotImplementedException();
        }
    }
}
