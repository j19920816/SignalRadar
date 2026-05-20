using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using ScottPlot;
using ScottPlot.WPF;

namespace Launcher.Charts
{
    public partial class BacktestChartWindow : Window
    {
        public BacktestChartWindow()
        {
            InitializeComponent();

            var summary = BacktestResultLoader.LoadSummary();
            var bars = BacktestResultLoader.LoadBars();
            var fills = BacktestResultLoader.LoadOrderFills();

            FillStatsBar(summary.Stats);
            BuildEquityTab(summary);
            BuildSymbolTabs(bars, fills);
        }

        private void FillStatsBar(Dictionary<string, string> stats)
        {
            StatNetProfit.Text = $"淨利: {GetStat(stats, "Net Profit")}";
            StatSharpe.Text = $"Sharpe: {GetStat(stats, "Sharpe Ratio")}";
            StatMaxDrawdown.Text = $"最大回撤: {GetStat(stats, "Drawdown")}";
            StatTotalTrades.Text = $"總交易: {GetStat(stats, "Total Orders")} 筆";
            StatWinRate.Text = $"勝率: {GetStat(stats, "Win Rate")}";
            StatProfitLossRatio.Text = $"盈虧比: {GetStat(stats, "Profit-Loss Ratio")}";
        }

        private static string GetStat(Dictionary<string, string> stats, string key)
        {
            return stats.TryGetValue(key, out var v) ? v : "-";
        }

        // 績效 tab：上 70% 權益曲線、下 30% 回撤（兩個獨立 WpfPlot 疊一個 Grid）
        private void BuildEquityTab(BacktestSummary summary)
        {
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(7, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star) });

            var equityPlot = new WpfPlot();
            Grid.SetRow(equityPlot, 0);
            grid.Children.Add(equityPlot);

            var drawdownPlot = new WpfPlot();
            Grid.SetRow(drawdownPlot, 1);
            grid.Children.Add(drawdownPlot);

            if (summary.EquityValues.Length > 0)
            {
                equityPlot.Plot.Add.ScatterLine(summary.EquityTimestamps, summary.EquityValues);
                equityPlot.Plot.Axes.DateTimeTicksBottom();
                equityPlot.Plot.Title("權益曲線");

                // 回撤 = (equity - runningMax) / runningMax
                var dd = new double[summary.EquityValues.Length];
                var runningMax = summary.EquityValues[0];
                for (var i = 0; i < summary.EquityValues.Length; i++)
                {
                    if (summary.EquityValues[i] > runningMax)
                        runningMax = summary.EquityValues[i];
                    dd[i] = (summary.EquityValues[i] - runningMax) / runningMax;
                }
                drawdownPlot.Plot.Add.ScatterLine(summary.EquityTimestamps, dd);
                drawdownPlot.Plot.Axes.DateTimeTicksBottom();
                drawdownPlot.Plot.Title("回撤");
            }

            equityPlot.Refresh();
            drawdownPlot.Refresh();

            Tabs.Items.Add(new TabItem { Header = "績效", Content = grid });
        }

        // 各 symbol 一個 tab：4H K 棒 + 進出場 ▲▼
        private void BuildSymbolTabs(Dictionary<string, List<OhlcBar>> barsBySymbol, List<OrderFill> fills)
        {
            foreach (var kvp in barsBySymbol)
            {
                var rawSymbol = kvp.Key;
                // Lean 加密幣 symbol 帶後綴 (如 "BTCUSDT 18R")，tab header 去掉
                var display = rawSymbol.Split(' ')[0];
                var symbolBars = kvp.Value;

                var plot = new WpfPlot();

                // K 棒寬度：從資料推 — 取相鄰兩根時間差，避免硬編寫死 4H
                var span = symbolBars.Count >= 2? symbolBars[1].Time - symbolBars[0].Time: TimeSpan.FromHours(4);

                var ohlcList = new List<OHLC>(symbolBars.Count);
                foreach (var bar in symbolBars)
                    ohlcList.Add(new OHLC(bar.Open, bar.High, bar.Low, bar.Close, bar.Time, span));
                plot.Plot.Add.Candlestick(ohlcList);

                // 進出場標記：buy → 綠 ▲、sell → 紅 ▼
                foreach (var fill in fills)
                {
                    if (!string.Equals(fill.Symbol, display, StringComparison.OrdinalIgnoreCase))
                        continue;
                    var isBuy = string.Equals(fill.Direction, "buy", StringComparison.OrdinalIgnoreCase);
                    var marker = plot.Plot.Add.Marker(
                        fill.Time.ToOADate(),
                        fill.Price,
                        isBuy ? MarkerShape.FilledTriangleUp : MarkerShape.FilledTriangleDown,
                        size: 12,
                        color: isBuy ? ScottPlot.Colors.Lime : ScottPlot.Colors.Red);
                }

                plot.Plot.Axes.DateTimeTicksBottom();
                plot.Plot.Title(display);
                plot.Refresh();

                Tabs.Items.Add(new TabItem { Header = display, Content = plot });
            }
        }
    }
}
