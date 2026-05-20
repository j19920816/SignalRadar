using System;
using System.Collections.Generic;

namespace Launcher.Charts
{
    // 寫出的 backtest-bars.json 每一筆 K 棒
    public record OhlcBar(DateTime Time, double Open, double High, double Low, double Close, double Volume);

    // Lean order-events.json 取出的單筆成交（status == "filled"）
    public record OrderFill(string Symbol, DateTime Time, double Price, double Quantity, string Direction);

    // Lean summary.json 萃取出的權益曲線 + 統計數字
    // EquityTimestamps / EquityValues 對應 charts.Strategy Equity.series.Equity.values
    // Stats 是 statistics 區段整包字典，BacktestChartWindow 自己挑 key 顯示
    public record BacktestSummary(double[] EquityTimestamps, double[] EquityValues, Dictionary<string, string> Stats);
}
