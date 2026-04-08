using QuantConnect.Data.Market;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace QuantConnect.Algorithm.CSharp.Interfaces
{
    public interface IWarmUpProvider
    {
        /// <summary>
        /// 取得指定 Symbol 的歷史 K 棒，用於 Live 模式下的指標 Warm-up。
        /// </summary>
        /// <param name="symbol">目標 Symbol</param>
        /// <param name="barSize">K 棒週期（例如 TimeSpan.FromHours(1) 或 TimeSpan.FromHours(4)）</param>
        /// <param name="count">需要的 K 棒數量</param>
        Task<IEnumerable<TradeBar>> GetBarsAsync(Symbol symbol, TimeSpan barSize, int count);
    }
}
