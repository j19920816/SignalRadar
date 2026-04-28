using QuantConnect;
using System.Collections.Generic;

namespace SignalRadar.Algorithm.Universe
{
    /// <summary>
    /// 紀錄 symbol 由哪個 UniverseSelectionModel 篩出。
    /// UniverseSelectionModel 篩完用 Set 覆寫該來源的最新名單；Alpha 用 Belongs 判斷是否屬於自己訂閱的來源。
    /// </summary>
    public static class UniverseSource
    {
        private static readonly Dictionary<string, HashSet<Symbol>> _map = new();
        private static readonly object _lock = new();

        public static void Set(string sourceId, IEnumerable<Symbol> symbols)
        {
            lock (_lock) _map[sourceId] = new HashSet<Symbol>(symbols);
        }

        public static bool Belongs(Symbol symbol, string sourceId)
        {
            lock (_lock) return _map.TryGetValue(sourceId, out var set) && set.Contains(symbol);
        }
    }
}
