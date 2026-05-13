namespace Win.Codex.ProfileSwitch;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon trayIcon;
    private readonly ProfileSwitcherService profileService = new();
    private readonly CodexClientRestartService restartService = new();
    private MainForm? mainForm;

    public TrayApplicationContext()
    {
        trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "Win.Codex.ProfileSwitch",
            Visible = true,
            ContextMenuStrip = BuildMenu()
        };
        trayIcon.DoubleClick += (_, _) => ShowMainForm();
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("打开管理窗口", null, (_, _) => ShowMainForm());
        var profilesMenu = new ToolStripMenuItem("切换 Profile");
        foreach (var profile in profileService.ListProfiles())
        {
            var item = new ToolStripMenuItem(profile.ToString())
            {
                Enabled = profile.IsComplete
            };
            item.Click += (_, _) => SwitchProfile(profile);
            profilesMenu.DropDownItems.Add(item);
        }
        if (profilesMenu.DropDownItems.Count == 0)
        {
            profilesMenu.DropDownItems.Add(new ToolStripMenuItem("暂无 profile") { Enabled = false });
        }
        menu.Items.Add(profilesMenu);
        menu.Items.Add("重启 Codex 客户端", null, async (_, _) => await RestartCodexClientAsync());
        menu.Items.Add("刷新托盘菜单", null, (_, _) => trayIcon.ContextMenuStrip = BuildMenu());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitThread());
        return menu;
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
            trayIcon.Text = $"Win.Codex.ProfileSwitch - {profile.Name}";
            MessageBox.Show(
                $"已切换到 {profile.Name}。\n\n历史 sessions 仍然保持在同一份 .codex 目录中。",
                "Win.Codex.ProfileSwitch"
            );
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "切换失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task RestartCodexClientAsync()
    {
        try
        {
            trayIcon.Text = "Win.Codex.ProfileSwitch - 正在重启 Codex";
            var result = await restartService.RestartAsync();
            trayIcon.Text = result.Started
                ? "Win.Codex.ProfileSwitch - Codex 已重启"
                : "Win.Codex.ProfileSwitch";

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
            MessageBox.Show(ex.Message, "重启 Codex 失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    protected override void ExitThreadCore()
    {
        trayIcon.Visible = false;
        trayIcon.Dispose();
        base.ExitThreadCore();
    }
}
