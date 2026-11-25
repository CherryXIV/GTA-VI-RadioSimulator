namespace RadioSim;

static class GuiProgram
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        var form = new MainForm();
        // "GTA VI Radio Simulator.exe" --selftest out.png ["Station"]: play muted for a few seconds, save a screenshot, exit
        if (args.Length >= 2 && args[0] == "--selftest")
            form.SelfTest(args[1], args.Length > 2 ? args[2] : "Flash FM");
        Application.Run(form);
    }
}
