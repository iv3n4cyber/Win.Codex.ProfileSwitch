namespace Win.Codex.ProfileSwitch;

internal sealed class MainForm : Form
{
    private readonly ProfileSwitcherService profileService;
    private readonly ListBox profileList = new();
    private readonly Label details = new();

    public event EventHandler? ProfilesChanged;

    public MainForm(ProfileSwitcherService profileService)
    {
        this.profileService = profileService;
        Text = AppText.S(
            "Win.Codex.ProfileSwitch - Profile Switcher",
            "Win.Codex.ProfileSwitch - Profile 管理"
        );
        AutoScaleMode = AutoScaleMode.Dpi;
        Width = 920;
        Height = 560;
        MinimumSize = new Size(820, 500);
        StartPosition = FormStartPosition.CenterScreen;
        BuildUi();
        RefreshProfiles();
        Shown += (_, _) => FitInitialWindowToContent();
    }

    private void BuildUi()
    {
        Text = AppText.S(
            "Win.Codex.ProfileSwitch - Profile Switcher",
            "Win.Codex.ProfileSwitch - Profile 管理"
        );

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            Padding = new Padding(12)
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 280));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 96));
        Controls.Add(root);

        profileList.Dock = DockStyle.Fill;
        profileList.SelectedIndexChanged += (_, _) => UpdateDetails();
        root.Controls.Add(profileList, 0, 0);

        details.Dock = DockStyle.Fill;
        details.TextAlign = ContentAlignment.TopLeft;
        details.Padding = new Padding(12);
        root.Controls.Add(details, 1, 0);

        var leftActions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            WrapContents = true
        };
        leftActions.Controls.Add(LanguageSelector());
        leftActions.Controls.Add(Button(AppText.S("Refresh", "刷新"), (_, _) => RefreshProfiles()));
        leftActions.Controls.Add(Button(AppText.S("Import Existing Configs", "扫描已有配置"), ImportExistingProfiles));
        leftActions.Controls.Add(Button(AppText.S("Open Profiles Folder", "打开目录"), (_, _) => profileService.OpenProfilesFolder()));
        root.Controls.Add(leftActions, 0, 1);

        var rightActions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            WrapContents = true,
            FlowDirection = FlowDirection.RightToLeft
        };
        rightActions.Controls.Add(Button(AppText.S("Switch to Selected Profile", "切换到选中 Profile"), SwitchSelectedProfile));
        rightActions.Controls.Add(Button(AppText.S("Create Profile From Current Config", "从当前配置创建 Profile"), CreateProfileFromCurrent));
        rightActions.Controls.Add(Button(AppText.S("Rename Profile", "重命名 Profile"), RenameSelectedProfile));
        rightActions.Controls.Add(Button(AppText.S("Edit config", "编辑 config"), (_, _) => OpenSelectedProfileFile("config.toml")));
        rightActions.Controls.Add(Button(AppText.S("Edit auth", "编辑 auth"), (_, _) => OpenSelectedProfileFile("auth.json")));
        rightActions.Controls.Add(Button(AppText.S("Open Profile", "打开 Profile"), OpenSelectedProfileFolder));
        root.Controls.Add(rightActions, 1, 1);
    }

    private ComboBox LanguageSelector()
    {
        var selector = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 120,
            Margin = new Padding(4)
        };
        var options = new[]
        {
            new LanguageOption(AppLanguage.English, "English"),
            new LanguageOption(AppLanguage.Chinese, "中文")
        };
        selector.DataSource = options;
        selector.SelectedItem = options.First(option => option.Language == AppText.CurrentLanguage);
        selector.SelectedIndexChanged += (_, _) =>
        {
            if (selector.SelectedItem is not LanguageOption option ||
                option.Language == AppText.CurrentLanguage)
            {
                return;
            }

            AppText.SetLanguage(option.Language);
            ApplyLanguage();
        };
        return selector;
    }

    public void ApplyLanguage()
    {
        Controls.Clear();
        BuildUi();
        RefreshProfiles();
    }

    private void RefreshProfiles()
    {
        var profiles = profileService.ListProfiles();
        profileList.DataSource = null;
        profileList.DataSource = profiles;
        UpdateDetails();
        ProfilesChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateDetails()
    {
        var currentProfile = profileService.ListProfiles().FirstOrDefault(item => item.IsCurrent);
        var currentProfileText = currentProfile is null
            ? AppText.S("No matching profile", "未匹配任何 Profile")
            : currentProfile.Name;

        if (profileList.SelectedItem is not CodexProfile profile)
        {
            details.Text = AppText.S($"""
            Current Profile:
            {currentProfileText}

            Profiles Folder:
            {CodexPaths.ProfilesRoot}

            Active Codex Config:
            {CodexPaths.AuthJsonPath}
            {CodexPaths.ConfigTomlPath}

            Session history is not switched:
            {Path.Combine(CodexPaths.CodexRoot, "sessions")}
            {Path.Combine(CodexPaths.CodexRoot, "archived_sessions")}
            """, $"""
            当前 Profile：
            {currentProfileText}

            Profile 目录：
            {CodexPaths.ProfilesRoot}

            当前 Codex 配置：
            {CodexPaths.AuthJsonPath}
            {CodexPaths.ConfigTomlPath}

            历史会话目录不会被切换：
            {Path.Combine(CodexPaths.CodexRoot, "sessions")}
            {Path.Combine(CodexPaths.CodexRoot, "archived_sessions")}
            """);
            return;
        }

        var selectedStatus = profile.IsCurrent
            ? AppText.S("Currently active", "当前正在使用")
            : AppText.S("Not active", "未使用");
        var authStatus = File.Exists(profile.AuthJsonPath)
            ? AppText.S("Present", "存在")
            : AppText.S("Missing", "缺失");
        var configStatus = File.Exists(profile.ConfigTomlPath)
            ? AppText.S("Present", "存在")
            : AppText.S("Missing", "缺失");
        details.Text = AppText.S($"""
        Profile: {profile.Name}
        Status: {selectedStatus}

        Current Profile:
        {currentProfileText}

        Path:
        {profile.DirectoryPath}

        Files:
        auth.json: {authStatus}
        config.toml: {configStatus}

        Switching only overwrites:
        {CodexPaths.AuthJsonPath}
        {CodexPaths.ConfigTomlPath}

        sessions / archived_sessions are not moved.
        """, $"""
        Profile：{profile.Name}
        状态：{selectedStatus}

        当前 Profile：
        {currentProfileText}

        路径：
        {profile.DirectoryPath}

        文件：
        auth.json: {authStatus}
        config.toml: {configStatus}

        切换时只覆盖：
        {CodexPaths.AuthJsonPath}
        {CodexPaths.ConfigTomlPath}

        不会移动 sessions / archived_sessions。
        """);
    }

    private void CreateProfileFromCurrent(object? sender, EventArgs e)
    {
        var name = Prompt.Show(
            AppText.S("Enter profile name", "输入 profile 名称"),
            AppText.S("Create Profile From Current auth.json/config.toml", "从当前 auth.json/config.toml 创建 Profile")
        );
        if (name is null)
        {
            return;
        }

        try
        {
            var profile = profileService.CreateProfileFromCurrent(name);
            RefreshProfiles();
            profileList.SelectedItem = profileList.Items.Cast<CodexProfile>()
                .FirstOrDefault(item => item.Name.Equals(profile.Name, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private void ImportExistingProfiles(object? sender, EventArgs e)
    {
        try
        {
            var imported = profileService.ImportExistingProfiles();
            RefreshProfiles();
            MessageBox.Show(
                imported.Count == 0
                    ? AppText.S(
                        "No matching auth*.json / config*.toml pairs were found to import.",
                        "没有找到可导入的 auth*.json / config*.toml 成对配置。"
                    )
                    : AppText.S(
                        $"Imported/refreshed {imported.Count} profile(s):\n\n{string.Join("\n", imported.Select(item => item.Name))}",
                        $"已导入/刷新 {imported.Count} 个 profile：\n\n{string.Join("\n", imported.Select(item => item.Name))}"
                    ),
                "Win.Codex.ProfileSwitch"
            );
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private void RenameSelectedProfile(object? sender, EventArgs e)
    {
        if (profileList.SelectedItem is not CodexProfile profile)
        {
            return;
        }

        var name = Prompt.Show(
            AppText.S("Enter the new profile name", "输入新的 profile 名称"),
            AppText.S($"Rename Profile: {profile.Name}", $"重命名 Profile：{profile.Name}")
        );
        if (name is null)
        {
            return;
        }

        try
        {
            var renamed = profileService.RenameProfile(profile, name);
            RefreshProfiles();
            profileList.SelectedItem = profileList.Items.Cast<CodexProfile>()
                .FirstOrDefault(item => item.Name.Equals(renamed.Name, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private void OpenSelectedProfileFile(string fileName)
    {
        if (profileList.SelectedItem is not CodexProfile profile)
        {
            return;
        }

        try
        {
            profileService.OpenProfileFile(profile, fileName);
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private void OpenSelectedProfileFolder(object? sender, EventArgs e)
    {
        if (profileList.SelectedItem is not CodexProfile profile)
        {
            return;
        }

        try
        {
            profileService.OpenProfileFolder(profile);
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private void SwitchSelectedProfile(object? sender, EventArgs e)
    {
        if (profileList.SelectedItem is not CodexProfile profile)
        {
            return;
        }

        try
        {
            profileService.SwitchTo(profile);
            RefreshProfiles();
            MessageBox.Show(
                AppText.S(
                    $"Switched to {profile.Name}.\n\nStart a new Codex session or restart the Codex client.",
                    $"已切换到 {profile.Name}。\n\n建议新开 Codex 会话或重启 Codex 客户端。"
                ),
                "Win.Codex.ProfileSwitch"
            );
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private static Button Button(string text, EventHandler handler)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(0, 30),
            Padding = new Padding(8, 3, 8, 3),
            Margin = new Padding(4)
        };
        button.Click += handler;
        return button;
    }

    private void FitInitialWindowToContent()
    {
        var preferred = GetPreferredSize(Size.Empty);
        var workingArea = Screen.FromControl(this).WorkingArea;
        Width = Math.Min(Math.Max(Width, preferred.Width + 32), workingArea.Width - 80);
        Height = Math.Min(Math.Max(Height, preferred.Height + 32), workingArea.Height - 80);
    }

    private static void ShowError(Exception ex) =>
        MessageBox.Show(ex.Message, "Win.Codex.ProfileSwitch", MessageBoxButtons.OK, MessageBoxIcon.Error);
}

internal static class Prompt
{
    public static string? Show(string label, string title)
    {
        using var form = new Form
        {
            Width = 420,
            Height = 150,
            Text = title,
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false
        };
        var textLabel = new Label { Left = 12, Top = 16, Width = 380, Text = label };
        var textBox = new TextBox { Left = 12, Top = 42, Width = 380 };
        var ok = new Button
        {
            Text = AppText.S("OK", "确定"),
            Left = 232,
            Width = 75,
            Top = 76,
            DialogResult = DialogResult.OK
        };
        var cancel = new Button
        {
            Text = AppText.S("Cancel", "取消"),
            Left = 317,
            Width = 75,
            Top = 76,
            DialogResult = DialogResult.Cancel
        };
        form.Controls.AddRange([textLabel, textBox, ok, cancel]);
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        return form.ShowDialog() == DialogResult.OK ? textBox.Text : null;
    }
}
