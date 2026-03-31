using Giraffy.Util;
using Microsoft.Data.SqlClient;
using QuantConnect;
using QuantConnect.Brokerages;
using System.Data;
using System.IO.Compression;
using System.Text;

namespace DataConverter
{
    public static class HistoryDataLoader
    {
        public static void Load(BrokerageName brokerageName, DateTime startDatetime, DateTime endDateTime, SecurityType securityType, string[] symbols, Resolution resolution, string dataFolder)
        {
            foreach (var symbol in symbols)
            {
                Console.WriteLine($"[{symbol}] 開始載入資料...");

                var queryCommand = $"select * from {symbol}_{resolution}s_1 Where TimeData between {Web.GenerateTimeStamp(DateTime.SpecifyKind(startDatetime, DateTimeKind.Utc))} and {Web.GenerateTimeStamp(DateTime.SpecifyKind(endDateTime, DateTimeKind.Utc))} order by TimeData ASC";

                var builder = new SqlConnectionStringBuilder(DbConfig.GetConnectionString());
                var dataTable = SelectDataTable(queryCommand, builder.ConnectionString);

                Console.WriteLine($"[{symbol}] 查詢到 {dataTable.Rows.Count} 筆資料");

                string directoryPath = dataFolder + securityType.ToString().ToLower() + Path.DirectorySeparatorChar + brokerageName.ToLower() + Path.DirectorySeparatorChar + resolution.ToString().ToLower() + Path.DirectorySeparatorChar + symbol.ToLower() + Path.DirectorySeparatorChar;
                Directory.CreateDirectory(directoryPath);
                foreach (var zipFile in Directory.GetFiles(directoryPath, "*.zip"))
                    File.Delete(zipFile);

                string filePath;

                int intervalMiliSec = 60 * 1000;
                long diffMilliSecs, prevTimeStamp = 0;
                DateTime nowRowDateTime, fileDateTime = DateTime.MinValue;
                DataRow fileRow = null, nowRow;

                StringBuilder stringBuilder = new StringBuilder();
                for (int k = 0; k < dataTable.Rows.Count; k++)
                {
                    nowRow = dataTable.Rows[k];
                    nowRowDateTime = Web.TimestampToUtcDateTime((long)nowRow[0]);
                    do
                    {
                        // 換日期就存資料
                        if (k > 0 && nowRowDateTime.Date > fileDateTime.Date)
                        {
                            filePath = directoryPath + $"{fileDateTime:yyyyMMdd}_{symbol.ToLower()}_{resolution.ToString().ToLower()}_trade.csv";
                            File.WriteAllText(filePath, stringBuilder.ToString());

                            var zipFilePath = Path.GetDirectoryName(filePath) + Path.DirectorySeparatorChar + $"{fileDateTime:yyyyMMdd}_trade.zip";
                            CreateZip(zipFilePath, filePath);

                            stringBuilder.Clear();
                            File.Delete(filePath);
                        }

                        if (k == 0 || (long)nowRow["TimeData"] == prevTimeStamp)  // 時間沒有斷層
                        {
                            fileRow = nowRow;
                            prevTimeStamp = (long)nowRow["TimeData"];
                            fileDateTime = nowRowDateTime;
                        }
                        else  // 時間有斷層就用上一個收盤價補齊
                        {
                            var close = fileRow["ClosePrice"];
                            fileRow["TimeData"] = prevTimeStamp;
                            fileRow["OpenPrice"] = close;
                            fileRow["HighPrice"] = close;
                            fileRow["LowPrice"] = close;
                            fileRow["Volume"] = 0;
                            fileRow["QuoteAssetVolume"] = 0;

                            fileDateTime = Web.TimestampToUtcDateTime((long)fileRow["TimeData"]);
                        }

                        var timeMinuteStr = (fileDateTime - fileDateTime.Date).TotalMilliseconds;
                        stringBuilder.Append(timeMinuteStr + ",");

                        var lastColumnIndex = dataTable.Columns.Count - 2;
                        for (int i = 1; i <= lastColumnIndex; i++)
                        {
                            stringBuilder.Append(fileRow[i].ToString());
                            stringBuilder.Append(i == lastColumnIndex ? "\n" : ",");
                        }

                        diffMilliSecs = (long)nowRow[0] - prevTimeStamp;
                        prevTimeStamp += intervalMiliSec;
                    } while (diffMilliSecs != 0);  // 時間有斷層就繼續補齊 K 棒
                }

                // flush 最後一天資料
                if (stringBuilder.Length > 0)
                {
                    filePath = directoryPath + $"{fileDateTime:yyyyMMdd}_{symbol.ToLower()}_{resolution.ToString().ToLower()}_trade.csv";
                    File.WriteAllText(filePath, stringBuilder.ToString());
                    var zipFilePath = Path.GetDirectoryName(filePath) + Path.DirectorySeparatorChar + $"{fileDateTime:yyyyMMdd}_trade.zip";
                    CreateZip(zipFilePath, filePath);
                    File.Delete(filePath);
                }

                dataTable.Dispose();
                Console.WriteLine($"[{symbol}] 完成！");
            }
        }

        private static DataTable SelectDataTable(string query, string sqlConnectionString)
        {
            DataTable result = new DataTable();
            using (var con = new SqlConnection(sqlConnectionString))
            {
                using (SqlCommand command = new SqlCommand(query, con))
                {
                    SqlDataAdapter da = new SqlDataAdapter(command);
                    da.Fill(result);
                }
            }
            return result;
        }

        private static void CreateZip(string zipFilePath, string needCompressFile)
        {
            using (FileStream fs = new FileStream(zipFilePath, FileMode.Create))
            {
                using (ZipArchive arch = new ZipArchive(fs, ZipArchiveMode.Create))
                {
                    arch.CreateEntryFromFile(needCompressFile, Path.GetFileName(needCompressFile));
                }
            }
        }
    }
}
