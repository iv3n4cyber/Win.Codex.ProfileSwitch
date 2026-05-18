namespace Win.Codex.ProfileSwitch;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon trayIcon;
    private readonly ProfileSwitcherService profileService = new();
    private readonly CodexClientRestartService restartService = new();
    private readonly CodexUsageService usageService = new();
    private readonly System.Windows.Forms.Timer usageRefreshTimer = new();
    private readonly Icon appIcon;
    private CodexUsageStatus usageStatus = CodexUsageStatus.Unavailable("Refresh pending.");
    private bool isRefreshingUsage;
    private MainForm? mainForm;

    public TrayApplicationContext()
    {
        appIcon = LoadAppIcon();
        usageStatus = usageService.GetLocalSnapshotStatus();
        trayIcon = new NotifyIcon
        {
            Icon = appIcon,
            Text = "Win.Codex.ProfileSwitch",
            Visible = true
        };
        trayIcon.ContextMenuStrip = BuildMenu();
        trayIcon.DoubleClick += (_, _) => ShowMainForm();

        usageRefreshTimer.Interval = 60_000;
        usageRefreshTimer.Tick += async (_, _) => await RefreshUsageStatusAsync();
        usageRefreshTimer.Start();
        _ = RefreshUsageStatusAsync();
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
        AddGroupedProfileMenuItems(profilesMenu, profiles);
        if (profilesMenu.DropDownItems.Count == 0)
        {
            profilesMenu.DropDownItems.Add(new ToolStripMenuItem(AppText.S("No profiles", "暂无 profile")) { Enabled = false });
        }
        menu.Items.Add(profilesMenu);
        AddUsageMenuItems(menu);
        menu.Items.Add(BuildLanguageMenu());
        menu.Items.Add(AppText.S("Restart Codex Client", "重启 Codex 客户端"), null, async (_, _) => await RestartCodexClientAsync());
        menu.Items.Add(AppText.S("Refresh Tray Menu", "刷新托盘菜单"), null, async (_, _) => await RefreshUsageStatusAsync());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(AppText.S("Exit", "退出"), null, (_, _) => ExitThread());
        return menu;
    }

    private void AddUsageMenuItems(ContextMenuStrip menu)
    {
        menu.Items.Add(new ToolStripSeparator());
        var usageItem = new ToolStripMenuItem(FormatUsageStatus())
        {
            Enabled = usageStatus.Kind == CodexUsageStatusKind.Available
        };
        menu.Items.Add(usageItem);
        menu.Items.Add(AppText.S("Refresh Usage", "刷新 Usage"), null, async (_, _) => await RefreshUsageStatusAsync());
    }

    private void AddGroupedProfileMenuItems(ToolStripMenuItem profilesMenu, IReadOnlyList<CodexProfile> profiles)
    {
        var completeProfiles = profiles.Where(profile => profile.IsComplete).ToList();
        foreach (var group in completeProfiles
            .GroupBy(ProfileGroupName)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            AddProfileMenuGroup(profilesMenu, group.Key, group);
        }

        AddProfileMenuGroup(
            profilesMenu,
            AppText.S("Incomplete Profiles", "缺文件的 Profile"),
            profiles.Where(profile => !profile.IsComplete)
        );
    }

    private void AddProfileMenuGroup(
        ToolStripMenuItem profilesMenu,
        string groupName,
        IEnumerable<CodexProfile> profiles
    )
    {
        var groupProfiles = profiles
            .OrderBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (groupProfiles.Count == 0)
        {
            return;
        }

        var groupItem = new ToolStripMenuItem($"[{groupName}]") { Enabled = false };
        profilesMenu.DropDownItems.Add(groupItem);
        foreach (var profile in groupProfiles)
        {
            var item = new ToolStripMenuItem(ProfileMenuDisplayName(profile))
            {
                Enabled = profile.IsComplete,
                Checked = profile.IsCurrent
            };
            item.Click += (_, _) => SwitchProfile(profile);
            profilesMenu.DropDownItems.Add(item);
        }
    }

    private static string ProfileGroupName(CodexProfile profile) =>
        profile.Name.Contains('@', StringComparison.Ordinal) ? "OAuth" : "Provider";

    private static string ProfileMenuDisplayName(CodexProfile profile) =>
        profile.IsComplete
            ? profile.Name
            : AppText.S($"{profile.Name} (missing files)", $"{profile.Name} (缺少文件)");

    private string FormatUsageStatus()
    {
        return usageStatus.Kind switch
        {
            CodexUsageStatusKind.NotOAuth => AppText.S(
                "Usage: OAuth profile required",
                "Usage：需要 OAuth Profile"
            ),
            CodexUsageStatusKind.Unavailable => AppText.S(
                $"Usage: {usageStatus.Message}",
                $"Usage：{usageStatus.Message}"
            ),
            CodexUsageStatusKind.Available when usageStatus.Snapshot is { } snapshot => AppText.S(
                $"Usage: {FormatPlanType(snapshot)} {FormatLimitLabel(snapshot.Primary)} {FormatRemaining(snapshot.Primary)} left, reset in {FormatResetDistance(snapshot.Primary)} | {FormatLimitLabel(snapshot.Secondary)} {FormatRemaining(snapshot.Secondary)} left",
                $"Usage：{FormatPlanType(snapshot)} {FormatLimitLabel(snapshot.Primary)} 剩余 {FormatRemaining(snapshot.Primary)}，{FormatResetDistance(snapshot.Primary)} 后刷新 | {FormatLimitLabel(snapshot.Secondary)} 剩余 {FormatRemaining(snapshot.Secondary)}"
            ),
            _ => AppText.S("Usage: unavailable", "Usage：不可用")
        };
    }

    private static string FormatPlanType(CodexUsageSnapshot snapshot)
    {
        return string.IsNullOrWhiteSpace(snapshot.PlanType)
            ? ""
            : snapshot.PlanType.ToUpperInvariant();
    }

    private static string FormatLimitLabel(CodexUsageLimit limit)
    {
        return limit.WindowSeconds switch
        {
            18000 => "5h",
            604800 => "7d",
            >= 86400 when limit.WindowSeconds % 86400 == 0 => $"{limit.WindowSeconds / 86400}d",
            >= 3600 when limit.WindowSeconds % 3600 == 0 => $"{limit.WindowSeconds / 3600}h",
            >= 60 when limit.WindowSeconds % 60 == 0 => $"{limit.WindowSeconds / 60}m",
            _ => $"{limit.WindowSeconds}s"
        };
    }

    private static string FormatRemaining(CodexUsageLimit limit)
    {
        var remaining = Math.Round(limit.RemainingPercent, 1);
        return remaining % 1 == 0
            ? $"{remaining:0}%"
            : $"{remaining:0.0}%";
    }

    private static string FormatResetDistance(CodexUsageLimit limit)
    {
        if (limit.ResetAt is null)
        {
            return "--";
        }

        var remaining = limit.ResetAt.Value - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            return "0m";
        }

        var days = (int)remaining.TotalDays;
        if (days > 0)
        {
            return $"{days}d{remaining.Hours}h";
        }

        return remaining.Hours > 0
            ? $"{remaining.Hours}h{remaining.Minutes:00}m"
            : $"{remaining.Minutes}m";
    }

    private async Task RefreshUsageStatusAsync()
    {
        if (isRefreshingUsage)
        {
            return;
        }

        isRefreshingUsage = true;
        try
        {
            usageStatus = await usageService.GetStatusAsync();
            trayIcon.ContextMenuStrip = BuildMenu();
        }
        finally
        {
            isRefreshingUsage = false;
        }
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
            usageStatus = usageService.GetLocalSnapshotStatus();
            trayIcon.ContextMenuStrip = BuildMenu();
            _ = RefreshUsageStatusAsync();
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
            await restartService.RestartAsync();
            usageStatus = await usageService.GetStatusAsync();
            trayIcon.ContextMenuStrip = BuildMenu();
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
        usageRefreshTimer.Stop();
        usageRefreshTimer.Dispose();
        trayIcon.Visible = false;
        trayIcon.Dispose();
        appIcon.Dispose();
        base.ExitThreadCore();
    }
}
