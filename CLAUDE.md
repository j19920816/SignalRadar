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

SignalRadar 是一個基於 **QuantConnect Lean Framework** 的加密貨幣策略雷達。回測模式在 Lean 引擎內下單；Live 模式透過 TCP 將訊號送往外部 WPF 下單機，本身不執行交易。

### Framework 三層管線（Algorithm.cs 組裝）

```
AlphaModel → PortfolioConstructionModel → ExecutionModel
  產生 Insight     轉成 PortfolioTarget       回測下單 / Live 送 TCP
```

- **AlphaModel**（`Alphas/`）— 策略訊號層。偵測 K 棒形態，產出 `Insight.Up` / `Insight.Down`。實作 `ISignalAlpha` 介面以提供 `StrategyId`、`TimeFrame`、`GetStopPrice()`。
- **PortfolioConstructionModel**（`PortfolioConstruction/FixedPortfolioConstructionModel`）— 將 Insight 轉為目標持倉數量：每個 Symbol 比例 = `1 / Securities.Count`，`quantity = sign × (TotalPortfolioValue × fraction) / price`。
- **ExecutionModel**（`Execution/SignalExecutionModel`）— 計算倉位差值；回測走 `MarketOrder`，Live 封裝成 `SignalMessage` 經 `TcpSignalSender` 送出。已持倉時只接受平倉訊號（`target.Quantity == 0`），忽略新的進場訊號。

### 輔助模組

| 路徑 | 用途 |
|---|---|
| `Universe/SymbolFilterModel` | 三層標的過濾器（流動性 / ADX 活躍度 / OBV 量能持續性），每 4H 更新 `ActiveSymbols` |
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

- **解析度**：Live 訂閱 Tick + 手動建 Consolidator 合成 1H/4H 棒；回測訂閱 Minute 級別 + Lean 自動合成。
- **標的範圍**：Live 目前測試階段只訂閱前 10 大 USDT 合約（BTC/ETH/BNB/SOL/XRP/DOGE/ADA/AVAX/LINK/DOT）；回測僅 BTC/ETH。
- **RiskModel / FeeModel**：僅回測啟用，Live 由下單機自行管理。
- **warm-up**：Live 透過 `IWarmUpProvider` 拉歷史 K 棒初始化指標；回測靠 `HistoryDataLoader` 預先匯出資料到 Lean data-folder。
- **TCP 連線**：僅 Live 建立 `TcpSignalSender`；回測 `_sender` 為 null。

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
