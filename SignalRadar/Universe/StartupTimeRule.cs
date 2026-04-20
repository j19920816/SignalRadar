using QuantConnect.Scheduling;
using System;
using System.Collections.Generic;

namespace SignalRadar.Algorithm.Universe
{
    /// <summary>
    /// 啟動後數秒立刻觸發一次，之後每日 00/04/08/12/16/20 UTC 整點觸發。
    /// 與 Binance 的 4H K 棒收盤時間對齊，與 Algorithm 時區無關。
    /// 避免不定時啟動時要等最多 4H 才進行第一次 Universe 篩選；
    /// REST 拉到的是最近收盤的 4H 棒（即上一個 4H boundary 的市場狀態）。
    /// </summary>
    public class StartupTimeRule : ITimeRule
    {
        private readonly DateTime _startupTime;

        public string Name => "StartupAndEveryFourHours";

        public StartupTimeRule(TimeSpan? startupDelay = null)
        {
            // 延後 5 秒避免與 Lean 內部初始化爭搶
            _startupTime = DateTime.SpecifyKind(
                DateTime.UtcNow + (startupDelay.HasValue ? startupDelay.Value : TimeSpan.FromSeconds(5)),
                DateTimeKind.Utc);
        }

        public IEnumerable<DateTime> CreateUtcEventTimes(IEnumerable<DateTime> dates)
        {
            // 不直接用 dates（那是算法時區下的日期），改用 UTC 日曆推進，
            // 以確保觸發點固定在 UTC 00/04/08/12/16/20。
            var startupYielded = false;
            var utcDay = DateTime.UtcNow.Date;

            foreach (var _ in dates)
            {
                for (var h = 0; h < 24; h += 4)
                {
                    var t = DateTime.SpecifyKind(utcDay.AddHours(h), DateTimeKind.Utc);
                    if (!startupYielded && _startupTime < t)
                    {
                        yield return _startupTime;
                        startupYielded = true;
                    }
                    yield return t;
                }
                utcDay = utcDay.AddDays(1);
            }

            if (!startupYielded)
                yield return _startupTime;
        }
    }
}
