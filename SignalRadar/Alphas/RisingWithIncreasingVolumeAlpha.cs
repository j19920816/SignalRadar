using SignalRadar.Algorithm.Interfaces;
using SignalRadar.Algorithm.Universe;
using QuantConnect;
using QuantConnect.Algorithm;
using QuantConnect.Algorithm.Framework.Alphas;
using QuantConnect.Data;
using QuantConnect.Data.Consolidators;
using QuantConnect.Data.Market;
using QuantConnect.Data.UniverseSelection;
using QuantConnect.Indicators;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace SignalRadar.Algorithm.Alphas
{
    /// <summary>
    /// 預估量爆 Alpha：盤中以「目前累積量 / 已過分鐘 × 240」推估這根 4H K 棒的全棒成交金額，
    /// 若預估值 > SMA10(4H) × VolumeMultiplier 且當前 4H 棒為綠 K（close > open），發 Insight.Up。
    /// 一根 4H 棒內最多只發一次訊號，棒收盤時 reset。
    /// </summary>
    public class RisingWithIncreasingVolumeAlpha : SignalAlphaBase
    {
        public override TimeSpan TimeSpanBar => TimeSpan.FromHours(4);
        public override string StrategyId => nameof(RisingWithIncreasingVolumeAlpha);
        public override string TimeFrame => TimeSpanBar.TotalMinutes.ToString();

        // 預估量超過 SMA10 × 倍數時觸發
        const decimal VOLUME_MULTIPLIER = 1.5m;

        // 本棒漲幅至少要達到 ATR(14) 的這個比例才發訊號（避免「貼著開盤線爬」的疲弱綠 K）
        // 用 ATR 正規化跨幣種波動差異:BTC 與 altcoin 的 1% 不是同一回事
        const decimal MIN_GAIN_ATR_RATIO = 0.5m;

        // 整根 4H 共 240 分鐘；至少要走過這個分鐘數才開始評估，避免早期樣本太少預估爆噪
        const int FULL_BAR_MINUTES = 240;
        const int MIN_ELAPSED_MINUTES = 30;

        private readonly ConcurrentDictionary<Symbol, VolumeData> _volumeData = new();
        private readonly SymbolFilterBase _symbolFilter;
        private readonly IWarmUpProvider _warmUpProvider;

        public RisingWithIncreasingVolumeAlpha(IWarmUpProvider warmUpProvider, SymbolFilterBase symbolFilter, string sourceId = null) : base(sourceId)
        {
            _warmUpProvider = warmUpProvider;
            _symbolFilter = symbolFilter;
        }

        public override async void OnSecuritiesChanged(QCAlgorithm algorithm, SecurityChanges changes)
        {
            try
            {
                foreach (var security in changes.AddedSecurities)
                {
                    var symbol = security.Symbol;
                    if (_volumeData.ContainsKey(symbol))
                        continue;

                    // 程式啟動點通常不會對齊 4H 邊界（例如 11:58 或 12:02 啟動），
                    // 導致 consolidator 的第一根 4H 棒只累積到部分時間的 minute bar → Volume/High/Low 都殘缺。
                    // 用兩個旗標跳過這個過渡期：第一根殘缺棒不更新 SMA/PreviousBars，當前未滿的 4H 棒也不發訊號。
                    // 必須在 consolidator 掛上前就設好,否則「掛上到設旗標之間 consolidator 先 fire」會把殘缺棒當正常棒處理。
                    _volumeData[symbol] = new VolumeData
                    {
                        SkipFirstConsolidatedBar = true,
                        SkipCurrentBar = true,
                    };

                    // warm-up → consolidator 順序由 base class 鎖死
                    // warm-up 拉 15 根 4H 棒：14 根餵 ATR(14)（同時涵蓋 SMA10），+ 1 根當作前一根 K 棒供止損用
                    await WarmUpAndAttachConsolidatorAsync(algorithm, symbol, _warmUpProvider, 15, FeedVolumeWarmUpBar, OnFourHourBar);

                    // 回測：替每個 symbol 掛 Filter 自己的 4H Consolidator（Live 由 UniverseSelection 透過 REST 跑）
                    if (!algorithm.LiveMode)
                        _symbolFilter.RegisterSymbol(algorithm, symbol);
                }
            }
            catch (Exception ex)
            {
                algorithm.Error($"[RisingWithIncreasingVolumeAlpha] OnSecuritiesChanged 失敗: {ex.Message}");
            }
        }

        private void FeedVolumeWarmUpBar(Symbol symbol, TradeBar bar)
        {
            var data = _volumeData[symbol];
            var usdtVolume = bar.Volume * bar.Close;
            data.VolumeSma.Update(bar.EndTime, usdtVolume);
            data.Atr.Update(bar);
            data.PreviousBars.Add(bar);
        }

        /// <summary>
        /// 每根 4H 收盤觸發：更新歷史 SMA10、保存當前棒供下一根止損用、清空盤中累積狀態。
        /// </summary>
        private void OnFourHourBar(object sender, TradeBar bar)
        {
            // symbol 已被 universe remove 但 consolidator 殘留 fire → TryGetValue 取不到就跳過
            if (!_volumeData.TryGetValue(bar.Symbol, out var data))
                return;

            // 啟動後第一根 consolidator 棒從訂閱時點才開始累積，數據殘缺 → 不更新 SMA/PreviousBars，
            // 只清旗標。下一根（完整）4H 棒收盤時才正式開始更新狀態。
            if (data.SkipFirstConsolidatedBar)
            {
                data.SkipFirstConsolidatedBar = false;
                data.SkipCurrentBar = false;
            }
            else
            {
                var usdtVolume = bar.Volume * bar.Close;
                data.VolumeSma.Update(bar.EndTime, usdtVolume);
                data.Atr.Update(bar);
                data.PreviousBars.Add(bar);
            }

            // 重置盤中累積（下一根 4H 重新計算）
            data.CurrentBarUsdtVolume = 0m;
            data.CurrentBarStartUtc = null;
            data.CurrentBarOpen = 0m;
            data.SignaledThisBar = false;
        }

        /// <summary>
        /// 每個 Slice（Minute Bar）都會被呼叫：
        ///   1. 累加當前 4H 棒的成交金額
        ///   2. 推估全棒總量 = 累積量 × 240 / 已過分鐘
        ///   3. 預估值 > SMA10 × 1.5 且當前 4H 棒為綠 K → 發 Insight.Up
        /// </summary>
        public override IEnumerable<Insight> Update(QCAlgorithm algorithm, Slice data)
        {
            // 用 UTC 對齊 Binance 4H 邊界：00/04/08/12/16/20，與 StartupTimeRule 的時序一致
            var utcNow = algorithm.UtcTime;
            var currentBarStartUtc = new DateTime(utcNow.Year, utcNow.Month, utcNow.Day, (utcNow.Hour / 4) * 4, 0, 0, DateTimeKind.Utc);

            foreach (var kvp in _volumeData)
            {
                var symbol = kvp.Key;
                var vd = kvp.Value;

                // 該 symbol 這個 Slice 沒有 minute bar 就跳過（可能停盤或剛上幣資料還沒進來）
                if (!data.Bars.TryGetValue(symbol, out var minuteBar))
                    continue;

                // 啟動時當前這根 4H 棒只有部分時間的資料，預估會偏 → 跳過直到下一根完整 4H 棒開始
                if (vd.SkipCurrentBar)
                    continue;

                // 偵測新一根 4H 棒開盤：清空盤中累積、記錄開盤價（取本棒第一根 minute bar 的 Open）
                if (vd.CurrentBarStartUtc == null || vd.CurrentBarStartUtc != currentBarStartUtc)
                {
                    vd.CurrentBarStartUtc = currentBarStartUtc;
                    vd.CurrentBarUsdtVolume = 0m;
                    vd.CurrentBarOpen = minuteBar.Open;
                    vd.SignaledThisBar = false;
                }

                // 累加當前 4H 棒到目前為止的成交金額
                vd.CurrentBarUsdtVolume += minuteBar.Volume * minuteBar.Close;

                if (!BelongsToSource(symbol))
                    continue;

                if (!_symbolFilter.ActiveSymbols.Contains(symbol) || !vd.VolumeSma.IsReady || !vd.Atr.IsReady)
                    continue;

                // 同一根 4H 內已發過訊號就跳過，避免每分鐘重複進場
                if (vd.SignaledThisBar)
                    continue;

                // 棒剛開盤前 N 分鐘樣本太少，預估值波動極大（一張大單就誤判），先不評估
                var minutesElapsed = (utcNow - currentBarStartUtc).TotalMinutes;
                if (minutesElapsed < MIN_ELAPSED_MINUTES)
                    continue;

                // 推估全棒總量 = 累積量 × (240 / 已過分鐘)
                var predictVolume = vd.CurrentBarUsdtVolume * (decimal)(FULL_BAR_MINUTES / minutesElapsed);
                var threshold = vd.VolumeSma.Current.Value * VOLUME_MULTIPLIER;

                // 條件 1：預估量爆量
                if (predictVolume <= threshold)
                    continue;

                // 條件 2：本棒漲幅 >= ATR(14) × 0.5（綠 K 且強度足夠，過濾貼著開盤線爬的疲弱訊號）
                // 用 ATR 正規化波動度差異,固定 % 在 BTC 與小幣間會偏鬆/偏嚴
                var gain = minuteBar.Close - vd.CurrentBarOpen;
                var minGain = vd.Atr.Current.Value * MIN_GAIN_ATR_RATIO;
                if (gain < minGain)
                    continue;

                vd.SignaledThisBar = true;
                yield return Insight.Price(symbol, TimeSpanBar, InsightDirection.Up);
            }
        }

        /// <summary>
        /// 止損價：取上一根（已收盤）4H K 棒的低點。
        /// 盤中進場止損距離雖較大但較穩定，不易被當前棒洗掉。
        /// </summary>
        public override decimal GetStopPrice(Symbol symbol)
        {
            if (!_volumeData.TryGetValue(symbol, out var d) || !d.PreviousBars.IsReady)
                return 0;

            // PreviousBars[0] = 最新已收盤的 4H 棒 = 上一根
            return d.PreviousBars[0].Low;
        }
    }

    /// <summary>
    /// 每個 Symbol 的盤中量能狀態
    /// </summary>
    public class VolumeData
    {
        // 過去 10 根已收盤 4H 棒的成交金額均值
        public SimpleMovingAverage VolumeSma { get; } = new SimpleMovingAverage(10);

        // 過去 14 根已收盤 4H 棒的 ATR(Wilders),用來正規化「強漲幅」門檻
        public AverageTrueRange Atr { get; } = new AverageTrueRange(14);

        // 保留最近 1 根已收盤 4H 棒給止損用（之後若要參考前 N 根可調大）
        public RollingWindow<TradeBar> PreviousBars { get; } = new RollingWindow<TradeBar>(1);

        // 當前未收盤 4H 棒的累積成交金額
        public decimal CurrentBarUsdtVolume { get; set; }

        // 當前未收盤 4H 棒的 UTC 起始時間（用來偵測棒邊界切換）
        public DateTime? CurrentBarStartUtc { get; set; }

        // 當前 4H 棒的開盤價（取本棒第一根 minute bar 的 Open）
        public decimal CurrentBarOpen { get; set; }

        // 同一根 4H 棒內是否已發過訊號（latch，避免每分鐘重複發）
        public bool SignaledThisBar { get; set; }

        // 啟動後第一根 consolidator 棒會殘缺（從訂閱時點才累積到 4H 邊界），
        // 不該餵進 SMA10、也不該寫入 PreviousBars 當止損依據
        public bool SkipFirstConsolidatedBar { get; set; }

        // 啟動時當前未滿的這根 4H 棒：盤中累積資料只有從訂閱時點開始，predictVolume 會偏 → 不發訊號
        public bool SkipCurrentBar { get; set; }
    }
}
