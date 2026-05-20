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

### 基本流程
1. `dotnet build SignalRadar.slnx` — 確認編譯無誤 ✅(已驗證)
2. `config.json` 設 `environment=backtesting`，`dotnet run --project Launcher/Launcher.csproj`
3. 回測跑完後應彈出 WPF 視窗
4. 確認「績效」tab 顯示權益曲線與回撤
5. 確認「BTCUSDT」/「ETHUSDT」tab 顯示 K 棒與進出場 ▲▼ 標記
6. 關閉視窗後程式正常結束（exit code 0）
7. 切換 `environment=live-futures-binance`，確認不彈出視窗

### 實作後未驗證的假設（跑回測後逐項對照）

**1. Lean 統計 key 名是否如預期**

`BacktestChartWindow.xaml.cs::FillStatsBar` 假設以下 key 存在於 `SignalRadarAlgorithm-summary.json` 的 `statistics` 區段：

| 顯示欄位 | 假設 key |
|---|---|
| 淨利 | `Net Profit` |
| Sharpe | `Sharpe Ratio` |
| 最大回撤 | `Drawdown` |
| 總交易 | `Total Orders` |
| 勝率 | `Win Rate` |
| 盈虧比 | `Profit-Loss Ratio` |

**驗證方法**：跑完回測後打開 `bin/Debug/net10.0-windows/SignalRadarAlgorithm-summary.json`，搜尋 `"statistics"` 區塊，逐一比對 key 名。任何顯示為 `-` 的欄位代表 key 對不上，記下實際 key 名後改 `FillStatsBar` 對應字串。

可能差異案例：`Total Orders` 在某些 Lean 版本叫 `Total Trades`；`Drawdown` 可能是 `Max Drawdown` 或 `Maximum Drawdown`。

**2. order events 的 symbol 是否帶後綴**

`BacktestChartWindow.xaml.cs::BuildSymbolTabs` 比對進出場標記用：
```csharp
string.Equals(f.Symbol, display, StringComparison.OrdinalIgnoreCase)
```
其中 `display` 已 `rawSymbol.Split(' ')[0]` 去掉 Lean 加密幣後綴（如 `"BTCUSDT 18R"` → `"BTCUSDT"`），而 `f.Symbol` 從 `OrderEventData.symbol-value` 取。

**驗證方法**：跑完回測後打開 `SignalRadarAlgorithm-order-events.json`，看 `symbol-value` 是 `"BTCUSDT"` 還是 `"BTCUSDT 18R"`。若帶後綴，K 線圖上會完全看不到 ▲▼ 標記 — 改 `BuildSymbolTabs` 比對時也對 `f.Symbol` 做相同 `Split(' ')[0]` 處理。

**3. summary.json 的 equity 路徑**

`BacktestResultLoader.LoadSummary` 走的路徑：
```
charts → "Strategy Equity" → series → Equity → values
```
每個 value 物件假設有 `x`（unix 秒，整數可接受 double）與 `y`（double）。

**驗證方法**：若視窗績效 tab 兩條線都是空的，但有檔案存在，打開 summary.json 確認：
- 是否有 `"Strategy Equity"` 這個鍵（含空格）
- `series` 底下是否有 `"Equity"` 這個 series
- `values` 是物件陣列（含 `x` / `y` 欄位）而非二維陣列

**4. ScottPlot 5 的視覺呈現**

API 編譯通過不代表畫出來正確：
- K 棒寬度（`OHLC` 的 `TimeSpan`）視覺上是否合理（不會疊在一起或太細）
- `DateTimeTicksBottom()` 的 X 軸標籤是否正確顯示日期時間（而非 OADate 數字）
- Marker 的 ▲▼ 位置是否落在對應 K 棒上（時區、OADate 轉換有沒有誤差）
- 績效 tab 上下兩個 plot 的 X 軸是否對齊（目前未強制同步，使用者可獨立縮放）

**5. _chartBarPeriod 改值的影響範圍**

`Algorithm.cs` 的 `_chartBarPeriod` 目前 4H。若改成 1H 或 1D：
- `OnSecuritiesChanged` 的 Consolidator 會跟著換 — ✅ 已用變數
- `BacktestChartWindow.BuildSymbolTabs` 的 OHLC 寬度 — ✅ 已從 `bars[1].Time - bars[0].Time` 推導
- 進出場標記時間與 K 棒對齊 — ✅ 標記用實際 fill 時間，與週期解耦

**驗證方法**：把 `_chartBarPeriod` 改成 `TimeSpan.FromHours(1)`，跑回測，確認 K 棒變密、標記仍對齊。

---

## 已知限制 / 未來可能升級

- 5 分鐘 alpha（`EngulfingCandlePatternAlpha`）的進場點落在 4H K 棒中間時看不到當下細節 — 之後若常用 5min 策略，再升級成「每支 alpha 一組 K 棒」（原規劃時討論的 Option 3）
- 績效 tab 兩個 plot X 軸未同步縮放 — 之後可用 ScottPlot 5 的 AxisLink 或 OnAxesChanged 事件補上
- stats bar 缺 key 顯示 `-` 而非紅色警告 — 對 debug 而言可能該凸顯
