namespace Win.Codex.ProfileSwitch;

static class Program
{
    [STAThread]
    static void Main()
    {
        AppText.Load();
        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApplicationContext());
    }    
}
