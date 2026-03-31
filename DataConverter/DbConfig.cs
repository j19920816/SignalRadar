namespace DataConverter
{
    public class DbConfig
    {
        public const string SERVER = "192.168.102.83";
        public const string DATABASE = "BinanceCryptoBar";
        public const string ACCOUNT = "kuan";
        public const string PASSWORD = "@giant99";

        public static string GetConnectionString()
        {
            return "Server=(local);Initial Catalog=" + DATABASE + ";User ID=" + ACCOUNT + ";Password=" + PASSWORD +
                   ";Persist Security Info=False;MultipleActiveResultSets=False;Encrypt=True;TrustServerCertificate=True;Connection Timeout=30;";
        }
    }
}