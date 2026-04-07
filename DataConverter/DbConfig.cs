using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataConverter
{
    public class DbConfig
    {
        [JsonPropertyName("server")]
        public string Server { get; set; } = "";

        [JsonPropertyName("database")]
        public string Database { get; set; } = "";

        [JsonPropertyName("account")]
        public string Account { get; set; } = "";

        [JsonPropertyName("password")]
        public string Password { get; set; } = "";

        [JsonPropertyName("dataFolder")]
        public string DataFolder { get; set; } = "";

        public static DbConfig G_Config = new DbConfig();
        public void Load()
        {
            var configPath = Path.Combine(AppContext.BaseDirectory, "config.json");
            if (!File.Exists(configPath))
                throw new FileNotFoundException($"找不到設定檔：{configPath}");

            var json = File.ReadAllText(configPath);
            var config = JsonSerializer.Deserialize<DbConfig>(json) ?? throw new InvalidOperationException("config.json 格式錯誤");

            Server = config.Server;
            Database = config.Database;
            Account = config.Account;
            Password = config.Password;
            DataFolder = config.DataFolder;
        }

        public string GetConnectionString()
        {
            return "Server=" + Server + ";Initial Catalog=" + Database + ";User ID=" + Account + ";Password=" + Password +
                   ";Persist Security Info=False;MultipleActiveResultSets=False;Encrypt=True;TrustServerCertificate=True;Connection Timeout=30;";
        }
    }
}
