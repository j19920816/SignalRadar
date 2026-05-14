# 回測 K 線圖視覺化規劃

## Context
回測跑完目前只有命令列 log，使用者希望在 `environment=backtesting` 時自動彈出 WPF 視窗，顯示：
1. **個別 symbol 4H K 棒 + 進出場標記**（主需求）
2. **權益曲線 + 回撤圖**（績效 tab）

Live 模式維持純命令列，不影響現有流程。

---

## 架構總覽

```
Engine.Run() 完成
    └── !LiveMode → BacktestChartWindow.ShowDialog()（阻塞到使用者關閉）
            └── finally: Exit()
```

資料來源：
- `backtest-bars.json`（新增，Algorithm.cs 收集 4H K 棒）
- `SignalRadarAlgorithm-order-events.json`（Lean 自動產生，含進出場）
- `SignalRadarAlgorithm-summary.json`（Lean 自動產生，含權益 OHLC + 統計數字）

---

## 步驟

### Step 1 — `SignalRadar/Algorithm.cs`

新增欄位：
```csharp
private readonly ConcurrentDictionary<Symbol, List<TradeBar>> _backtestBars = new();
```

新增 `OnSecuritiesChanged` override：
```csharp
public override void OnSecuritiesChanged(SecurityChanges changes)
{
    if (LiveMode) return;
    foreach (var security in changes.AddedSecurities)
    {
        var symbol = security.Symbol;
        _backtestBars.TryAdd(symbol, new List<TradeBar>());
        var con = new TradeBarConsolidator(TimeSpan.FromHours(4));
        con.DataConsolidated += (_, bar) => _backtestBars[symbol].Add((TradeBar)bar);
        SubscriptionManager.AddConsolidator(symbol, con);
    }
}
```

修改 `OnEndOfAlgorithm`，加在 `_sender?.Dispose()` 後：
```csharp
if (!LiveMode)
{
    var dict = _backtestBars.ToDictionary(
        kvp => kvp.Key.Value,
        kvp => kvp.Value.Select(b => new {
            t = b.Time, o = b.Open, h = b.High, l = b.Low, c = b.Close, v = b.Volume
        }));
    File.WriteAllText("backtest-bars.json", JsonSerializer.Serialize(dict));
}
```

> 需 `using System.Text.Json;`、`using QuantConnect.Data.Consolidators;`，
> Algorithm.cs 已有 `using System.Collections.Concurrent;`。

---

### Step 2 — `Launcher/Launcher.csproj`

加入（OutputType 維持 `Exe`，保留命令列視窗）：
```xml
<UseWPF>true</UseWPF>
<PackageReference Include="ScottPlot.WPF" Version="5.*" />
```

---

### Step 3 — `Launcher/Program.cs`

Main 方法加 `[STAThread]`：
```csharp
[STAThread]
static void Main(string[] args) { ... }
```

在 `engine.Run(...)` 下方、try 區塊結束前插入：
```csharp
if (!QuantConnect.Globals.LiveMode &&
    algorithmManager.State == AlgorithmStatus.Completed)
{
    var window = new SignalRadar.Charts.BacktestChartWindow();
    window.ShowDialog();
}
```

---

### Step 4 — 新建 `Launcher/Charts/` 目錄，三個新檔案

#### `BacktestData.cs`（資料模型）
```csharp
namespace SignalRadar.Charts;

record OhlcBar(DateTime Time, double Open, double High, double Low, double Close, double Volume);
record OrderFill(string Symbol, DateTime Time, double Price, double Quantity, string Direction);
record BacktestSummary(double[] EquityTimestamps, double[] EquityValues, Dictionary<string, string> Stats);
```

#### `BacktestResultLoader.cs`（讀取 JSON）
- `LoadBars()` → 讀 `backtest-bars.json`，回傳 `Dictionary<string, List<OhlcBar>>`
- `LoadFills()` → 讀 `SignalRadarAlgorithm-order-events.json`，只取 `status=filled`
- `LoadSummary()` → 讀 `SignalRadarAlgorithm-summary.json`，取出 `charts.Strategy Equity.series.Equity.values` 和 `statistics`

檔案路徑：`AppContext.BaseDirectory` + 檔名（即 bin 輸出目錄）。

#### `BacktestChartWindow.xaml / .xaml.cs`（WPF 主視窗）

視窗結構：
```
┌──────────────────────────────────────────────────────────┐
│ 淨利: +X%   Sharpe: X   最大回撤: X%   總交易: X 筆       │
├──────────────────────────────────────────────────────────┤
│ [績效]  [BTCUSDT]  [ETHUSDT]                              │
├──────────────────────────────────────────────────────────┤
│  WpfPlot（ScottPlot）                                    │
└──────────────────────────────────────────────────────────┘
```

**績效 Tab**（單一 WpfPlot，上下分割）：
- 上 70%：`plot.Add.Signal()` 畫權益曲線（折線）
- 下 30%：`plot.Add.FillY()` 畫回撤（負值區域，紅色半透明）
  - 回撤 = `(equity - runningMax) / runningMax`，即時計算

**Symbol Tab**（各 symbol 一個 tab，各一個 WpfPlot）：
```csharp
// K 棒
var candles = bars.Select(b =>
    new OHLC(b.Open, b.High, b.Low, b.Close,
             new DateTime(b.Time.Ticks), TimeSpan.FromHours(4)));
plot.Add.Candlestick(candles);

// 進場 ▲（買入）
plot.Add.Marker(fill.Time, fill.Price,
    MarkerShape.TriangleUp, size: 12, color: Colors.LimeGreen);
// 出場 ▼（賣出）
plot.Add.Marker(fill.Time, fill.Price,
    MarkerShape.TriangleDown, size: 12, color: Colors.Red);
```

X 軸格式：`plot.Axes.DateTimeTicksBottom()`

---

## 關鍵細節

| 事項 | 說明 |
|------|------|
| OutputType | 維持 `Exe`（保留命令列），加 `UseWPF=true` 即可同時用 WPF |
| STAThread | WPF 強制要求，Lean 的 Engine 執行緒不受影響（各自獨立） |
| ShowDialog 時機 | `engine.Run()` 結束後、`finally` 前，視窗關閉後才進入 `Exit()` |
| 多 symbol | 每個 symbol 一個 Tab，tab header 顯示代號（去掉 ` 18R` 等 Lean 後綴） |
| 訂單方向判斷 | `fillQuantity > 0` → 買入標記，`fillQuantity < 0` → 賣出標記 |
| 僅已成交單 | `order-events.json` 只取 `status == "filled"` |
| 4H bar 收集 | 僅在 `!LiveMode` 才掛 Consolidator，不影響 Live |

---

## 修改的檔案清單

| 檔案 | 動作 |
|------|------|
| `SignalRadar/Algorithm.cs` | 新增 `_backtestBars`、`OnSecuritiesChanged`、修改 `OnEndOfAlgorithm` |
| `Launcher/Launcher.csproj` | 加 `UseWPF`、ScottPlot.WPF |
| `Launcher/Program.cs` | 加 `[STAThread]`、`engine.Run` 後插入視窗啟動 |
| `Launcher/Charts/BacktestData.cs` | 新建 |
| `Launcher/Charts/BacktestResultLoader.cs` | 新建 |
| `Launcher/Charts/BacktestChartWindow.xaml` | 新建 |
| `Launcher/Charts/BacktestChartWindow.xaml.cs` | 新建 |

---

## 驗證方式

1. `dotnet build SignalRadar.slnx` — 確認編譯無誤
2. `config.json` 設 `environment=backtesting`，`dotnet run --project Launcher/Launcher.csproj`
3. 回測跑完後應彈出 WPF 視窗
4. 確認「績效」tab 顯示權益曲線與回撤
5. 確認「BTCUSDT」tab 顯示 4H K 棒與進出場 ▲▼ 標記
6. 關閉視窗後程式正常結束（exit code 0）
7. 切換 `environment=live-futures-binance`，確認不彈出視窗
