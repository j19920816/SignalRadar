# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Run

```bash
# 建置（需要 .NET 10 SDK）
dotnet build SignalRadar.csproj

# 透過 QuantConnect Lean CLI 執行回測
lean backtest .

# 透過 QuantConnect Lean CLI 執行 Live
lean live .
```

本專案無測試套件。

## Architecture

SignalRadar 是一個基於 **QuantConnect Lean Framework** 的加密貨幣策略雷達。回測模式在 Lean 引擎內下單；Live 模式透過 TCP 將訊號送往外部 WPF 下單機，本身不執行交易。

### Framework 三層管線（Main.cs 組裝）

```
AlphaModel → PortfolioConstructionModel → ExecutionModel
  產生 Insight     轉成 PortfolioTarget       回測下單 / Live 送 TCP
```

- **AlphaModel**（`Alphas/`）— 策略訊號層。偵測 K 棒形態，產出 `Insight.Up` / `Insight.Down`。實作 `ISignalAlpha` 介面以提供 `StrategyId`、`TimeFrame`、`GetStopPrice()`。
- **PortfolioConstructionModel**（`PortfolioConstruction/`）— 將 Insight 轉為目標持倉數量（等權分配）。
- **ExecutionModel**（`Execution/`）— 計算倉位差值；回測走 `MarketOrder`，Live 封裝成 `SignalMessage` 經 `TcpSignalSender` 送出。

### 輔助模組

| 路徑 | 用途 |
|---|---|
| `Universe/SymbolFilterModel` | 三層標的過濾器（流動性 / ADX 活躍度 / OBV 量能持續性），每 4H 更新 `ActiveSymbols` |
| `Providers/BinanceWarmUpProvider` | Live 模式下透過 Binance REST API 取歷史 K 棒，餵指標做 warm-up |
| `Interfaces/IWarmUpProvider` | warm-up 抽象介面（`GetBarsAsync`） |
| `Interfaces/ISignalAlpha` | Alpha 需額外提供 StrategyId / TimeFrame / StopPrice |
| `Network/TcpSignalSender` | TCP client，透過 Giraffy.Net 連接下單機（port 2026） |
| `Signals/SignalMessage` | 訊號 JSON DTO |
| `BacktestModels/PercentageFeeModel` | 回測用百分比手續費 |
| `BacktestModels/TrailingStopRiskModel` | 回測用兩階段停損（固定停損 → 移動停損） |

### 關鍵行為差異：Live vs 回測

- **解析度**：Live 訂閱 Tick + 手動建 Consolidator 合成 1H/4H 棒；回測訂閱 Minute 級別的歷史K棒資料 + Lean 自動合成。
- **標的範圍**：Live 動態訂閱全部 Binance USDT 合約；回測僅 BTC/ETH。
- **RiskModel / FeeModel**：僅回測啟用，Live 由下單機自行管理。
- **warm-up**：Live 透過 `IWarmUpProvider` 拉歷史 K 棒初始化指標；回測靠 Lean 自行暖機。

### 外部依賴

- **Giraffy.dll**（本地 DLL）— 提供 `CryptoExchange`（REST API caller、SymbolsRule）與 `Net`（TCP NetClient）。
- **QuantConnect.Lean** — 策略框架。

### Namespace

所有原始碼使用 `QuantConnect.Algorithm.CSharp.*` namespace（Lean 慣例）。

## Conventions

- 不使用 `??` null 合併運算子，改用三元運算子。
- Git commit 訊息格式：`+` 新增、`-` 刪除、`=` 修改。

