using System.Text.Json;

namespace Win.Codex.ProfileSwitch;

internal enum AppLanguage
{
    English,
    Chinese
}

internal static class AppText
{
    public static AppLanguage CurrentLanguage { get; private set; } = AppLanguage.English;

    public static void Load()
    {
        CodexPaths.EnsureDirectories();
        if (!File.Exists(CodexPaths.AppConfigPath))
        {
            CurrentLanguage = AppLanguage.English;
            return;
        }

        try
        {
            var config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(CodexPaths.AppConfigPath));
            CurrentLanguage = Enum.TryParse<AppLanguage>(config?.Language, ignoreCase: true, out var language)
                ? language
                : AppLanguage.English;
        }
        catch
        {
            CurrentLanguage = AppLanguage.English;
        }
    }

    public static void SetLanguage(AppLanguage language)
    {
        CurrentLanguage = language;
        CodexPaths.EnsureDirectories();
        var config = new AppConfig { Language = language.ToString() };
        File.WriteAllText(CodexPaths.AppConfigPath, JsonSerializer.Serialize(config, JsonOptions));
    }

    public static string S(string english, string chinese) =>
        CurrentLanguage == AppLanguage.Chinese ? chinese : english;

    private static JsonSerializerOptions JsonOptions => new() { WriteIndented = true };

    private sealed class AppConfig
    {
        public string? Language { get; set; }
    }
}

internal sealed record LanguageOption(AppLanguage Language, string DisplayName)
{
    public override string ToString() => DisplayName;
}
