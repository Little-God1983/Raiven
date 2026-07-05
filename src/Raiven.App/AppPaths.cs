namespace Raiven.App;

public static class AppPaths
{
    public static string DataDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Raiven");

    public static string ConfigFile => Path.Combine(DataDir, "config.json");
    public static string HistoryFile => Path.Combine(DataDir, "history.json");
    public static string LogDir => Path.Combine(DataDir, "logs");
}
