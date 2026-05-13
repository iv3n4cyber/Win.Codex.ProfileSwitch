namespace Win.Codex.ProfileSwitch;

internal static class CodexPaths
{
    public static string HomeDirectory =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string CodexRoot =>
        Path.Combine(HomeDirectory, ".codex");

    public static string AuthJsonPath =>
        Path.Combine(CodexRoot, "auth.json");

    public static string ConfigTomlPath =>
        Path.Combine(CodexRoot, "config.toml");

    public static string AppRoot =>
        Path.Combine(CodexRoot, "win-codex-profile-switch");

    public static string AppConfigPath =>
        Path.Combine(AppRoot, "config.json");

    public static string BackupsRoot =>
        Path.Combine(AppRoot, "backups");

    public static string ProfilesRoot =>
        Path.Combine(CodexRoot, "profiles");

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(CodexRoot);
        Directory.CreateDirectory(AppRoot);
        Directory.CreateDirectory(BackupsRoot);
        Directory.CreateDirectory(ProfilesRoot);
    }
}
