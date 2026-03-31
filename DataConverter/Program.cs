using QuantConnect;
using QuantConnect.Brokerages;
using DataConverter;

if (args.Length < 3)
{
    Console.WriteLine("使用方式：DataConverter <開始日期> <結束日期> <symbols檔案路徑>");
    Console.WriteLine("範例：DataConverter 20240101 20241231 symbols.txt");
    return;
}

var startDate = DateTime.ParseExact(args[0], "yyyyMMdd", null);
var endDate = DateTime.ParseExact(args[1], "yyyyMMdd", null);
var symbolsFile = args[2];

if (!File.Exists(symbolsFile))
{
    Console.WriteLine($"找不到檔案：{symbolsFile}");
    return;
}

var symbols = File.ReadAllLines(symbolsFile).Select(s => s.Trim()).Where(s => !string.IsNullOrEmpty(s)).ToArray();
var dataFolder = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\SignalRadar\data\"));

Console.WriteLine($"開始轉換資料：{startDate:yyyyMMdd} ~ {endDate:yyyyMMdd}");
Console.WriteLine($"標的數量：{symbols.Length}");
Console.WriteLine($"輸出到：{dataFolder}");
Console.WriteLine();

HistoryDataLoader.Load(BrokerageName.Binance, startDate, endDate, SecurityType.Crypto, symbols, Resolution.Minute, dataFolder);

Console.WriteLine();
Console.WriteLine("全部完成！");