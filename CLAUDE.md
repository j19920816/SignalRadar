# CLAUDE.md

## Build & Run

VS 開 `SignalRadar.slnx` 或命令列 `dotnet build SignalRadar.slnx` / `dotnet run --project Launcher/Launcher.csproj`（需 .NET 10）。啟動專案 `Launcher`（Console Exe），載入 `SignalRadar/SignalRadar.Algorithm.csproj` 編出的 `SignalRadar.Algorithm.dll`。執行模式切換改 `Launcher/config.json` 的 `environment`：`backtesting` / `live-paper` / `live-interactive`。平台 `Any CPU`（`SignalRadar.Algorithm` 支援 `AnyCPU;x64`）。**無測試套件**。

## Solution 結構

```
Launcher/                Lean Engine 進入點（Program.cs / config.json）
SignalRadar/             策略 DLL
  Algorithm.cs           SignalRadarAlgorithm（QCAlgorithm 子類）
  Alphas/ PortfolioConstruction/ Execution/ Universe/
  BacktestModels/        回測 FeeModel / RiskModel
  Providers/ Interfaces/ Network/ Signals/
  HistoryDataLoader.cs   回測：SQL Server → Lean zip csv
  DbConfig.cs            SQL Server 連線設定
  Giraffy.dll            本地 DLL（Binance REST + TCP NetClient）
```

## Architecture

基於 **QuantConnect Lean Framework**，鎖定 Binance USDT 永續合約。回測在 Lean 內下單；Live 經 TCP（127.0.0.1:2026）送 WPF 下單機，策略端不直接交易。時區 `Asia/Taipei`（UTC+8），但 4H K 棒邊界（00/04/08/12/16/20）以 **UTC** 為準，對齊 Binance。

### Framework 管線：`UniverseSelection → AlphaModel → PCM → ExecutionModel`

- **UniverseSelection**：`FilteredUniverseSelectionModel`（Live）每 4H 觸發。`BinanceCryptoUniverse.GetTradableCryptos` 撈 Binance USDT 合約 candidates、自動 `SPDB.SetEntry` 補新上幣 → 注入的 `SymbolFilterBase` 子類做篩選 → 結果寫入 `UniverseSource[SourceId]`。
- **AlphaModel**：多支並行（`EngulfingCandlePatternAlpha`、`RisingWithIncreasingVolumeAlpha`），繼承 `SignalAlphaBase`；發 Insight 前用 `UniverseSource.Belongs(symbol, SourceId)` 過濾，只處理自己訂閱的來源。實作 `ISignalAlpha`（`StrategyId` / `TimeFrame` / `GetStopPrice()`）。
- **PortfolioConstruction**：`FixedPortfolioConstructionModel` 每 Symbol 比例 = `1 / Securities.Count`，`quantity = sign × TotalPortfolioValue × fraction / price`；`PortfolioTarget.Tag = insight.SourceModel`（alpha 類別名）。
- **Execution**：`SignalExecutionModel` 用 `target.Tag` 反查發訊號的 alpha 取 `StrategyId` / `TimeFrame` / `StopPrice`；回測走 `MarketOrder`，Live 封裝成 `SignalMessage` 經 `TcpSignalSender` 送出。已持倉時只接受平倉訊號（`target.Quantity == 0`）。

### `SymbolFilterBase` 篩選器（抽象基底）

子類提供兩條路徑：**Live** — `EvaluateBars`（基底 `RunAsync` SemaphoreSlim 10 併發 + `BinanceWarmUpProvider` 拉 500 根 4H 棒，無狀態評估，自動丟棄未收盤最後一根）；**回測** — `RegisterSymbol`（由 Alpha 在 `OnSecuritiesChanged` 呼叫，掛 4H `TradeBarConsolidator`，每根收盤後重新評估並更新 `ActiveSymbols`）。

具體實作：
- `LiquidityAdxObvFilter`：流動性 VolumeSMA10 ≥ 100 萬 USDT、ADX(14) ≥ 35 且當期成交金額 > 均值×1.5、OBV > SMA10(OBV)
- `WBottomUniverseFilter`：W 底 higher low 反轉型 — fractal pivot 找兩個低點 + 中間 peak + 最新收盤距 peak ≤ 1.5×ATR(14)

### Live vs 回測差異

| 面向 | Live | 回測 |
|---|---|---|
| 訂閱解析度 | Minute + `TradeBarConsolidator` 合成 | Minute + Lean 自動合成 |
| 標的範圍 | `FilteredUniverseSelectionModel` 動態篩選 | 手動 `AddCryptoFuture("BTCUSDT" / "ETHUSDT")` |
| 篩選驅動 | `EvaluateBars`（REST warm-up） | `RegisterSymbol`（Consolidator 累積） |
| warm-up | `BinanceWarmUpProvider` 拉歷史 K 棒 | `HistoryDataLoader` 從 SQL Server 匯出 zip csv |
| Risk / Fee | 下單機自管 | `TrailingStopRiskModel` + `PercentageFeeModel(0.002)` |
| 下單 | `SignalMessage` → TCP | Lean `MarketOrder` |

### 套件與 Namespace

.NET 10、QuantConnect.Lean 2.5.*、QuantConnect.DataSource.Libraries 2.5.*、Binance.Net 12.11.0、Microsoft.Data.SqlClient 7.0.0、Giraffy.dll（本地 — `CryptoExchange` + `Net`）。策略類別在 `SignalRadar.Algorithm.*`；PCM 在 `SignalRadar.PortfolioConstruction`；`Launcher` 在 `Launcher`。

## Conventions

- 不使用 `??` null 合併運算子，改用三元運算子
- `const` 變數用 `UPPER_SNAKE_CASE`
- Git commit 訊息：`+` 新增、`-` 刪除、`=` 修改

## Documentation

策略文件放 `doc/`，格式參照 `doc/LiquidityAdxObvFilter.md`：(1) 概覽一句話 (2) 完整 Live 流程 Mermaid flowchart（`%%{init: {"theme": "default"}}%%`，白底）(3) 條件／規則表格 (4) Code Snippets (5) 非顯而易見的設計決策（stateless、並行節流等）。
