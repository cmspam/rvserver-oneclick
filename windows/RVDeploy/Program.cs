namespace RVDeploy;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        // --test: does everything except starting the game server, so a test VPS never registers.
        Application.Run(new MainForm(args.Contains("--test")));
    }
}
