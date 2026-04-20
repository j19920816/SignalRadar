# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Run

本方案由 Visual Studio 開啟 `SignalRadar.slnx` 直接編譯執行，不再透過 QuantConnect Lean CLI 操作。

```bash
# 命令列建置（需要 .NET 10 SDK）
dotnet build SignalRadar.slnx

# 執行（進入點是 Launcher）
dotnet run --project Launcher/Launcher.csproj
```

- **啟動專案**：`Launcher`（Console Exe）。
- **策略組件**：`SignalRadar/SignalRadar.Algorithm.csproj` 編成 `SignalRadar.Algorithm.dll`，由 `Launcher/config.json` 的 `algorithm-location` 指向載入。
- **執行模式切換**：改 `Launcher/config.json` 的 `environment` 欄位：`backtesting`（回測）/ `live-paper` / `live-interactive`（Live）。
- **平台**：解決方案設定 `Any CPU`；`SignalRadar.Algorithm` 支援 `AnyCPU;x64`。

本專案無測試套件。

## Solution 結構

```
SignalRadar.slnx
├── Launcher/                       Console Exe — Lean Engine 進入點
│   ├── Program.cs                  啟動 Lean (Initializer / Engine / AlgorithmManager)
│   ├── Launcher.csproj             ProjectReference → SignalRadar.Algorithm
│   └── config.json                 Lean 執行組態（environment、data-folder 等）
└── SignalRadar/                    策略 DLL
    ├── SignalRadar.Algorithm.csproj
    ├── Algorithm.cs                SignalRadarAlgorithm（QCAlgorithm 子類）
    ├── Alphas/                     AlphaModel 實作
    ├── PortfolioConstruction/      PCM 實作
    ├── Execution/                  ExecutionModel 實作
    ├── BacktestModels/             回測專用 FeeModel / RiskModel
    ├── Universe/                   標的過濾器
    ├── Providers/                  IWarmUpProvider 實作
    ├── Interfaces/                 ISignalAlpha、IWarmUpProvider
    ├── Network/                    TCP 送單機
    ├── Signals/                    SignalMessage DTO
    ├── HistoryDataLoader.cs        回測用：從 SQL Server 匯出歷史資料為 Lean zip csv
    ├── DbConfig.cs                 DB 連線設定
    └── Giraffy.dll                 本地 DLL（Binance REST + TCP NetClient）
```

## Architecture

SignalRadar 是一個基於 **QuantConnect Lean Framework** 的加密貨幣策略雷達，鎖定 Binance USDT 永續合約。回測模式在 Lean 引擎內下單；Live 模式透過 TCP 把訊號送往外部 WPF 下單機，自己不執行交易。

時區設為 `Asia/Taipei`（UTC+8），但 4H K 棒邊界（00/04/08/12/16/20）仍以 **UTC** 為準，對齊 Binance 的 K 棒收盤。

### Framework 三層管線（Algorithm.cs 組裝）

```
UniverseSelection → AlphaModel → PortfolioConstructionModel → ExecutionModel
  篩出可交易標的   產生 Insight    轉成 PortfolioTarget       回測下單 / Live 送 TCP
```

- **UniverseSelectionModel**（`Universe/FilteredUniverseSelectionModel`）— Live 專用。繼承 `ScheduledUniverseSelectionModel`，**啟動立即**跑一次（拉上一個 4H boundary 的收盤資料做初始篩選）+ 之後每日 UTC 00/04/08/12/16/20 各跑一次。每次從 Giraffy `SymbolsRule` 撈所有 USDT 合約，透過 `SymbolPropertiesDatabase.SetEntry` 即時補註 Lean 不認得的新上幣（避免 `Failed to resolve base currency` 崩潰），再交給 `SymbolFilterModel.RunAsync` 做三層篩選。
- **AlphaModel**（`Alphas/EngulfingCandlePatternAlpha`）— 策略訊號層。Minute 訂閱 + `TradeBarConsolidator` 合成 15m K 棒，餵 `Engulfing` 指標。Bull Engulfing → `Insight.Up`，Bear Engulfing → `Insight.Down`；發 Insight 前先檢查 symbol 是否在 `_symbolFilter.ActiveSymbols` 內。實作 `ISignalAlpha` 以提供 `StrategyId`、`TimeFrame`、`GetStopPrice()`（回傳前一根 K 棒的 Low 作為止損價）。
- **PortfolioConstructionModel**（`PortfolioConstruction/FixedPortfolioConstructionModel`）— 將 Insight 轉為目標持倉數量：每個 Symbol 比例 = `1 / Securities.Count`，`quantity = sign × (TotalPortfolioValue × fraction) / price`。
- **ExecutionModel**（`Execution/SignalExecutionModel`）— 計算倉位差值；回測走 `MarketOrder`，Live 封裝成 `SignalMessage` 經 `TcpSignalSender` 送出。已持倉時只接受平倉訊號（`target.Quantity == 0`），忽略新的進場訊號。

### Universe 篩選流程（Live）

1. **FilteredUniverseSelectionModel.SelectSymbols**（每 4H 觸發一次）
   - 讀 Lean SPDB 現有的 binance CryptoFuture ticker 清單
   - 掃 Giraffy `SymbolsRule`，挑出所有 `QuoteAsset == USDT` 的合約
   - SPDB 沒登記過的 → 用 Giraffy 的 `TickPriceStep` / `QuantityStep` 組 `SymbolProperties`，呼叫 `spdb.SetEntry(...)` 補進去
   - 產生 candidate `Symbol` 清單
2. **SymbolFilterModel.RunAsync**（SemaphoreSlim 限制 10 併發 REST）
   - 每支 candidate 透過 `IWarmUpProvider.GetBarsAsync` 拉 100 根 4H K 棒
   - 每次重新跑指標 warm-up，不保留狀態（避免 websocket 斷線後指標漂移）
   - 三層篩選（詳見下方）通過者進入 `ActiveSymbols`
3. Lean 收到回傳清單後自動 `AddSecurity` / `RemoveSecurity`，觸發 Alpha 的 `OnSecuritiesChanged`

### SymbolFilterModel 三層篩選

| 層 | 條件 | 指標 |
|---|---|---|
| 流動性 | 過去 10 根 4H 成交金額均值 ≥ 100 萬 USDT | `VolumeSMA(10)` |
| 活躍度 | ADX ≥ 35 **且** 當前 4H 成交金額 > 均值 × 1.5 | `ADX(14)` + `CurrentUsdtVolume` |
| 量能持續性 | OBV > SMA10(OBV) | `OBV` + `OBV_SMA(10)` |

- **Live 路徑**：`RunAsync` 由 `FilteredUniverseSelectionModel` 每 4H 呼叫，用 REST 拉資料跑指標。
- **回測路徑**：`RegisterSymbol` 由 Alpha 的 `OnSecuritiesChanged` 呼叫，為每個 symbol 掛 4H `TradeBarConsolidator`，靠 Lean 回測 feed 累積指標，每根 4H 收盤後更新 `ActiveSymbols`。

### 輔助模組

| 路徑 | 用途 |
|---|---|
| `Universe/FilteredUniverseSelectionModel` | Live 專用 `ScheduledUniverseSelectionModel`，每 4H 重新篩選 Binance USDT 永續合約 |
| `Universe/StartupTimeRule` | `ITimeRule` 實作，啟動立即觸發一次 + 之後每日 UTC 00/04/08/12/16/20 |
| `Universe/SymbolFilterModel` | 三層標的過濾器（流動性 / ADX 活躍度 / OBV 量能持續性），維護 `ActiveSymbols` |
| `Providers/BinanceWarmUpProvider` | Live 模式下透過 Binance REST API 取歷史 K 棒，餵指標做 warm-up，會自動丟棄未收盤的最後一根 |
| `Interfaces/IWarmUpProvider` | warm-up 抽象介面（`GetBarsAsync`） |
| `Interfaces/ISignalAlpha` | Alpha 需額外提供 StrategyId / TimeFrame / StopPrice |
| `Network/TcpSignalSender` | TCP client，透過 Giraffy.Net 連接下單機（127.0.0.1:2026） |
| `Signals/SignalMessage` | 訊號 JSON DTO（SignalId / StrategyId / Symbol / Side / TimeFrame / Price / StopPrice / Timestamp） |
| `BacktestModels/PercentageFeeModel` | 回測用百分比手續費 |
| `BacktestModels/TrailingStopRiskModel` | 回測用兩階段停損（固定停損 → 移動停損） |
| `HistoryDataLoader` | 回測用：從 SQL Server 讀分鐘 K 棒，補齊斷層後寫成每日 zip csv，餵給 Lean 的 data-folder |
| `DbConfig` | SQL Server 連線設定（Binance K 棒資料庫） |

### 關鍵行為差異：Live vs 回測

| 面向 | Live | 回測 |
|---|---|---|
| 訂閱解析度 | `UniverseSettings.Resolution = Minute` + `TradeBarConsolidator` 合成 15m/4H | Minute 訂閱 + Lean 自動合成 |
| 標的範圍 | `FilteredUniverseSelectionModel` 每 4H 動態篩選所有 USDT 永續合約 | 僅手動 `AddCryptoFuture("BTCUSDT")` / `AddCryptoFuture("ETHUSDT")` |
| 篩選器驅動方式 | `SymbolFilterModel.RunAsync`（REST + 指標 warm-up） | `SymbolFilterModel.RegisterSymbol`（掛 4H Consolidator） |
| warm-up | `IWarmUpProvider` 拉歷史 K 棒初始化 Alpha 指標 | `HistoryDataLoader` 先從 SQL Server 匯出資料到 Lean data-folder |
| Risk / Fee Model | 下單機自行管理，策略端不啟用 | `TrailingStopRiskModel` + `PercentageFeeModel(0.002)` |
| 下單路徑 | `SignalMessage` → `TcpSignalSender`（127.0.0.1:2026） | Lean `MarketOrder` |
| 未收盤 K 棒處理 | `BinanceWarmUpProvider` 判斷 `LastOpenTime + barInterval > Now` → 丟棄最後一根 | 不需處理 |

### 套件與外部依賴

- **.NET 10**（`net10.0`）。
- **QuantConnect.Lean** 2.5.* — 策略框架（`Launcher` 與 `SignalRadar.Algorithm` 皆參照）。
- **QuantConnect.DataSource.Libraries** 2.5.*。
- **Binance.Net** 12.11.0。
- **Microsoft.Data.SqlClient** 7.0.0 — `HistoryDataLoader` 連 SQL Server 用。
- **Giraffy.dll**（本地 DLL）— 提供 `CryptoExchange`（REST API caller、SymbolsRule）與 `Net`（TCP NetClient）。

### Namespace

策略相關類別使用 `SignalRadar.Algorithm.*`（`SignalRadar.Algorithm`、`SignalRadar.Algorithm.Alphas`、`SignalRadar.Algorithm.Execution`、`SignalRadar.Algorithm.Universe`、`SignalRadar.Algorithm.Providers`、`SignalRadar.Algorithm.Network`、`SignalRadar.Algorithm.Signals`、`SignalRadar.Algorithm.Interfaces`、`SignalRadar.Algorithm.Backtest`）。PCM 在 `SignalRadar.PortfolioConstruction`。`Launcher` 在 `Launcher` namespace。

## Conventions

- 不使用 `??` null 合併運算子，改用三元運算子。
- Git commit 訊息格式：`+` 新增、`-` 刪除、`=` 修改。
