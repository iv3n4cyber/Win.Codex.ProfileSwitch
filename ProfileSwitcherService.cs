using System.Text;

namespace Win.Codex.ProfileSwitch;

internal sealed record CodexProfile(string Name, string DirectoryPath, bool IsCurrent = false)
{
    public string AuthJsonPath => Path.Combine(DirectoryPath, "auth.json");
    public string ConfigTomlPath => Path.Combine(DirectoryPath, "config.toml");
    public bool IsComplete => File.Exists(AuthJsonPath) && File.Exists(ConfigTomlPath);
    public override string ToString()
    {
        if (!IsComplete)
        {
            return AppText.S($"{Name} (missing files)", $"{Name} (缺少文件)");
        }

        return IsCurrent ? AppText.S($"{Name} (current)", $"{Name} (当前)") : Name;
    }
}

internal sealed class ProfileSwitcherService
{
    public IReadOnlyList<CodexProfile> ListProfiles()
    {
        CodexPaths.EnsureDirectories();
        return Directory.GetDirectories(CodexPaths.ProfilesRoot)
            .Select(path => new CodexProfile(Path.GetFileName(path), path))
            .Select(profile => profile with { IsCurrent = IsCurrentProfile(profile) })
            .OrderBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public CodexProfile CreateProfileFromCurrent(string name)
    {
        var profileName = NormalizeProfileName(name);
        var directory = Path.Combine(CodexPaths.ProfilesRoot, profileName);
        Directory.CreateDirectory(directory);

        CopyRequiredCurrentFile(CodexPaths.AuthJsonPath, Path.Combine(directory, "auth.json"));
        CopyRequiredCurrentFile(CodexPaths.ConfigTomlPath, Path.Combine(directory, "config.toml"));
        return new CodexProfile(profileName, directory);
    }

    public CodexProfile CreateProfileFromOAuth(string preferredName, string authJson, string configToml)
    {
        CodexPaths.EnsureDirectories();

        var profileName = UniqueProfileName(preferredName);
        var directory = Path.Combine(CodexPaths.ProfilesRoot, profileName);
        Directory.CreateDirectory(directory);

        File.WriteAllText(Path.Combine(directory, "auth.json"), authJson, Encoding.UTF8);
        File.WriteAllText(Path.Combine(directory, "config.toml"), configToml, Encoding.UTF8);
        return new CodexProfile(profileName, directory);
    }

    public CodexProfile RenameProfile(CodexProfile profile, string newName)
    {
        var profileName = NormalizeProfileName(newName);
        var destination = Path.Combine(CodexPaths.ProfilesRoot, profileName);
        if (Directory.Exists(destination))
        {
            throw new InvalidOperationException(AppText.S(
                $"Profile already exists: {profileName}",
                $"Profile 已存在：{profileName}"
            ));
        }

        Directory.Move(profile.DirectoryPath, destination);
        return new CodexProfile(profileName, destination);
    }

    public void SwitchTo(CodexProfile profile)
    {
        if (!profile.IsComplete)
        {
            throw new InvalidOperationException(AppText.S(
                "This profile must contain both auth.json and config.toml.",
                "这个 profile 必须同时包含 auth.json 和 config.toml"
            ));
        }

        CodexPaths.EnsureDirectories();
        BackupIfPresent(CodexPaths.AuthJsonPath);
        BackupIfPresent(CodexPaths.ConfigTomlPath);
        File.Copy(profile.AuthJsonPath, CodexPaths.AuthJsonPath, overwrite: true);
        File.Copy(profile.ConfigTomlPath, CodexPaths.ConfigTomlPath, overwrite: true);
    }

    public void OpenProfilesFolder()
    {
        CodexPaths.EnsureDirectories();
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = CodexPaths.ProfilesRoot,
            UseShellExecute = true
        });
    }

    private static void CopyRequiredCurrentFile(string source, string destination)
    {
        if (!File.Exists(source))
        {
            throw new FileNotFoundException(AppText.S(
                $"The current .codex folder does not contain {Path.GetFileName(source)}.",
                $"当前 .codex 中不存在 {Path.GetFileName(source)}"
            ), source);
        }

        File.Copy(source, destination, overwrite: true);
    }

    public IReadOnlyList<CodexProfile> ImportExistingProfiles()
    {
        CodexPaths.EnsureDirectories();
        var imported = new List<CodexProfile>();
        var existingProfiles = ListProfiles();

        foreach (var authPath in Directory.GetFiles(CodexPaths.CodexRoot, "auth*.json", SearchOption.TopDirectoryOnly))
        {
            var suffix = ProfileSuffixFromAuthFile(authPath);
            var configPath = Path.Combine(CodexPaths.CodexRoot, $"config{suffix}.toml");
            if (!File.Exists(configPath))
            {
                continue;
            }

            var profileName = suffix.Length == 0
                ? "default"
                : NormalizeProfileName(suffix.TrimStart('-', '_', '.'));
            if (suffix.Length == 0 && HasMatchingExistingProfile(existingProfiles, authPath, configPath))
            {
                continue;
            }

            var directory = Path.Combine(CodexPaths.ProfilesRoot, profileName);
            if (Directory.Exists(directory))
            {
                continue;
            }

            Directory.CreateDirectory(directory);
            File.Copy(authPath, Path.Combine(directory, "auth.json"), overwrite: true);
            File.Copy(configPath, Path.Combine(directory, "config.toml"), overwrite: true);
            var profile = new CodexProfile(profileName, directory);
            imported.Add(profile);
            existingProfiles = existingProfiles.Append(profile).ToList();
        }

        return imported
            .OrderBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public void OpenProfileFile(CodexProfile profile, string fileName)
    {
        var path = fileName switch
        {
            "auth.json" => profile.AuthJsonPath,
            "config.toml" => profile.ConfigTomlPath,
            _ => throw new InvalidOperationException(AppText.S("Unsupported file.", "不支持的文件"))
        };

        if (!File.Exists(path))
        {
            throw new FileNotFoundException(AppText.S(
                $"The profile does not contain {fileName}.",
                $"Profile 中不存在 {fileName}"
            ), path);
        }

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true
        });
    }

    public void OpenProfileFolder(CodexProfile profile)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = profile.DirectoryPath,
            UseShellExecute = true
        });
    }

    private static string ProfileSuffixFromAuthFile(string authPath)
    {
        var fileName = Path.GetFileNameWithoutExtension(authPath);
        return fileName.Length <= "auth".Length ? "" : fileName["auth".Length..];
    }

    private static bool HasMatchingExistingProfile(
        IEnumerable<CodexProfile> profiles,
        string authPath,
        string configPath
    )
    {
        return profiles.Any(profile =>
            profile.IsComplete &&
            FilesHaveSameContent(profile.AuthJsonPath, authPath) &&
            FilesHaveSameContent(profile.ConfigTomlPath, configPath));
    }

    private static bool IsCurrentProfile(CodexProfile profile)
    {
        return profile.IsComplete &&
            FilesHaveSameContent(profile.AuthJsonPath, CodexPaths.AuthJsonPath) &&
            FilesHaveSameContent(profile.ConfigTomlPath, CodexPaths.ConfigTomlPath);
    }

    private static bool FilesHaveSameContent(string firstPath, string secondPath)
    {
        if (!File.Exists(firstPath) || !File.Exists(secondPath))
        {
            return false;
        }

        var first = new FileInfo(firstPath);
        var second = new FileInfo(secondPath);
        if (first.Length != second.Length)
        {
            return false;
        }

        return File.ReadAllBytes(firstPath).SequenceEqual(File.ReadAllBytes(secondPath));
    }

    private static void BackupIfPresent(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        var fileName = Path.GetFileName(path);
        var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
        File.Copy(path, Path.Combine(CodexPaths.BackupsRoot, $"{fileName}.{stamp}.bak"), overwrite: true);
    }

    private static string NormalizeProfileName(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
        {
            throw new InvalidOperationException(AppText.S(
                "Profile name cannot be empty.",
                "Profile 名称不能为空"
            ));
        }

        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            trimmed = trimmed.Replace(invalid, '-');
        }

        return trimmed;
    }

    private static string UniqueProfileName(string preferredName)
    {
        var baseName = NormalizeProfileName(preferredName);
        var candidate = baseName;
        var index = 2;
        while (Directory.Exists(Path.Combine(CodexPaths.ProfilesRoot, candidate)))
        {
            candidate = $"{baseName}-{index}";
            index++;
        }

        return candidate;
    }
}
