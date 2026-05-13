namespace Win.Codex.ProfileSwitch;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon trayIcon;
    private readonly ProfileSwitcherService profileService = new();
    private readonly CodexClientRestartService restartService = new();
    private readonly Icon appIcon;
    private MainForm? mainForm;

    public TrayApplicationContext()
    {
        appIcon = LoadAppIcon();
        trayIcon = new NotifyIcon
        {
            Icon = appIcon,
            Text = "Win.Codex.ProfileSwitch",
            Visible = true
        };
        trayIcon.ContextMenuStrip = BuildMenu();
        trayIcon.DoubleClick += (_, _) => ShowMainForm();
    }

    private ContextMenuStrip BuildMenu()
    {
        var profiles = profileService.ListProfiles();
        var currentProfile = profiles.FirstOrDefault(profile => profile.IsCurrent);
        UpdateTrayText(currentProfile?.Name);

        var menu = new ContextMenuStrip();
        menu.Items.Add(AppText.S("Open Management Window", "打开管理窗口"), null, (_, _) => ShowMainForm());
        menu.Items.Add(
            currentProfile is null
                ? AppText.S("Current Profile: no match", "当前 Profile：未匹配")
                : AppText.S($"Current Profile: {currentProfile.Name}", $"当前 Profile：{currentProfile.Name}"),
            null,
            (_, _) => ShowMainForm()
        ).Enabled = false;
        menu.Items.Add(new ToolStripSeparator());
        var profilesMenu = new ToolStripMenuItem(AppText.S("Switch Profile", "切换 Profile"));
        foreach (var profile in profiles)
        {
            var item = new ToolStripMenuItem(profile.ToString())
            {
                Enabled = profile.IsComplete,
                Checked = profile.IsCurrent
            };
            item.Click += (_, _) => SwitchProfile(profile);
            profilesMenu.DropDownItems.Add(item);
        }
        if (profilesMenu.DropDownItems.Count == 0)
        {
            profilesMenu.DropDownItems.Add(new ToolStripMenuItem(AppText.S("No profiles", "暂无 profile")) { Enabled = false });
        }
        menu.Items.Add(profilesMenu);
        menu.Items.Add(BuildLanguageMenu());
        menu.Items.Add(AppText.S("Restart Codex Client", "重启 Codex 客户端"), null, async (_, _) => await RestartCodexClientAsync());
        menu.Items.Add(AppText.S("Refresh Tray Menu", "刷新托盘菜单"), null, (_, _) => trayIcon.ContextMenuStrip = BuildMenu());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(AppText.S("Exit", "退出"), null, (_, _) => ExitThread());
        return menu;
    }

    private ToolStripMenuItem BuildLanguageMenu()
    {
        var languageMenu = new ToolStripMenuItem(AppText.S("Language", "语言"));
        AddLanguageItem(languageMenu, AppLanguage.English, "English");
        AddLanguageItem(languageMenu, AppLanguage.Chinese, "中文");
        return languageMenu;
    }

    private void AddLanguageItem(ToolStripMenuItem languageMenu, AppLanguage language, string displayName)
    {
        var item = new ToolStripMenuItem(displayName)
        {
            Checked = AppText.CurrentLanguage == language
        };
        item.Click += (_, _) =>
        {
            AppText.SetLanguage(language);
            mainForm?.ApplyLanguage();
            trayIcon.ContextMenuStrip = BuildMenu();
        };
        languageMenu.DropDownItems.Add(item);
    }

    private void ShowMainForm()
    {
        if (mainForm is { IsDisposed: false })
        {
            mainForm.Activate();
            mainForm.Show();
            return;
        }

        mainForm = new MainForm(profileService);
        mainForm.ProfilesChanged += (_, _) => trayIcon.ContextMenuStrip = BuildMenu();
        mainForm.FormClosed += (_, _) => mainForm = null;
        mainForm.Show();
    }

    private void SwitchProfile(CodexProfile profile)
    {
        try
        {
            profileService.SwitchTo(profile);
            trayIcon.ContextMenuStrip = BuildMenu();
            MessageBox.Show(
                AppText.S(
                    $"Switched to {profile.Name}.\n\nSession history remains in the shared .codex folder.",
                    $"已切换到 {profile.Name}。\n\n历史 sessions 仍然保持在同一份 .codex 目录中。"
                ),
                "Win.Codex.ProfileSwitch"
            );
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, AppText.S("Switch Failed", "切换失败"), MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task RestartCodexClientAsync()
    {
        try
        {
            trayIcon.Text = AppText.S(
                "Win.Codex.ProfileSwitch - Restarting Codex",
                "Win.Codex.ProfileSwitch - 正在重启 Codex"
            );
            var result = await restartService.RestartAsync();
            trayIcon.ContextMenuStrip = BuildMenu();

            MessageBox.Show(
                result.Message,
                "Win.Codex.ProfileSwitch",
                MessageBoxButtons.OK,
                result.Started ? MessageBoxIcon.Information : MessageBoxIcon.Warning
            );
        }
        catch (Exception ex)
        {
            trayIcon.Text = "Win.Codex.ProfileSwitch";
            MessageBox.Show(ex.Message, AppText.S("Restart Codex Failed", "重启 Codex 失败"), MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void UpdateTrayText(string? currentProfileName)
    {
        trayIcon.Text = currentProfileName is null
            ? AppText.S(
                "Win.Codex.ProfileSwitch - No Matching Profile",
                "Win.Codex.ProfileSwitch - 未匹配 Profile"
            )
            : $"Win.Codex.ProfileSwitch - {currentProfileName}";
    }

    private static Icon LoadAppIcon()
    {
        var assembly = typeof(TrayApplicationContext).Assembly;
        using var stream = assembly.GetManifestResourceStream("AppIcon.ico");
        if (stream is null)
        {
            return (Icon)SystemIcons.Application.Clone();
        }

        using var icon = new Icon(stream);
        return (Icon)icon.Clone();
    }

    protected override void ExitThreadCore()
    {
        trayIcon.Visible = false;
        trayIcon.Dispose();
        appIcon.Dispose();
        base.ExitThreadCore();
    }
}
