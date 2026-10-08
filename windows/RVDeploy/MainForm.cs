using Renci.SshNet;

namespace RVDeploy;

public sealed class MainForm : Form
{
    readonly bool testOnly;
    readonly TextBox host = new() { Width = 260 }, user = new() { Width = 120, Text = "root" };
    readonly TextBox password = new() { Width = 260, UseSystemPasswordChar = true };
    readonly TextBox keyFile = new() { Width = 360 }, zipFile = new() { Width = 360 };
    readonly Button browseKey = new() { Text = "Browse...", AutoSize = true }, newKey = new() { Text = "Create a new key", AutoSize = true };
    readonly Button browseZip = new() { Text = "Browse...", AutoSize = true };
    readonly Button check = new() { Text = "Check the VPS", AutoSize = true };
    readonly Label vpsInfo = new() { AutoSize = true, MaximumSize = new Size(620, 0), Text = "Not checked yet." };
    readonly RadioButton community = new() { Text = "Community server (public, the rVclient admins approve it)", AutoSize = true, Checked = true };
    readonly RadioButton privateServer = new() { Text = "Private server (you and your friends)", AutoSize = true };
    readonly TextBox contact = new() { Width = 260 }, serverName = new() { Width = 260 };
    readonly Label contactLabel = new() { Text = "Your Discord name:", AutoSize = true };
    readonly CheckBox[] modes = { new() { Text = "Solos" }, new() { Text = "Playground" }, new() { Text = "Duos" }, new() { Text = "Trios" }, new() { Text = "Squads" } };
    readonly CheckBox slim = new() { Text = "RAM saving (recommended: about 2.5 GB per mode instead of 3.9 GB)", AutoSize = true, Checked = true };
    readonly CheckBox webui = new() { Text = "Web admin page (port 8080)", AutoSize = true, Checked = true };
    readonly Button install = new() { Text = "Install", AutoSize = true, Enabled = false };
    readonly ProgressBar bar = new() { Width = 620, Visible = false };
    readonly TextBox log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Width = 620, Height = 200, Font = new Font(FontFamily.GenericMonospace, 8.5f) };
    Dictionary<string, string>? info;
    bool alreadyCoreOS;
    readonly CancellationTokenSource cancel = new();

    static readonly string[] ModeIds = { "solo", "playground", "duos", "trios", "squads" };

    public MainForm(bool testOnly)
    {
        this.testOnly = testOnly;
        Text = "Rumbleverse server setup" + (testOnly ? " (TEST: does not start the server)" : "");
        AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink; Padding = new Padding(12);
        FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false;

        var root = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        Controls.Add(root);

        root.Controls.Add(Heading("1. Your VPS"));
        root.Controls.Add(Note("A VPS with a freshly installed Debian or Ubuntu, x86_64, at least 4 GB RAM and 30 GB disk. Everything on it will be erased."));
        root.Controls.Add(Row(Label("Address:"), host, Label("User:"), user));
        root.Controls.Add(Note("The VPS's IP address. If its SSH port isn't 22, add it like this: 203.0.113.10:2222"));
        root.Controls.Add(Row(Label("Password:"), password, Note("(leave empty if your key already works)")));
        root.Controls.Add(Row(Label("SSH key:"), keyFile, browseKey, newKey));
        root.Controls.Add(Note("You log in to the finished server with this key. Without one, a new key is created for you."));

        root.Controls.Add(Heading("2. Your game zip"));
        root.Controls.Add(Row(Label("Zip:"), zipFile, browseZip));

        root.Controls.Add(Row(check));
        root.Controls.Add(vpsInfo);

        root.Controls.Add(Heading("3. Your server"));
        root.Controls.Add(community); root.Controls.Add(privateServer);
        root.Controls.Add(Row(contactLabel, contact));
        root.Controls.Add(Row(Label("Server name:"), serverName));
        var modeRow = Row(Label("Modes:"));
        foreach (var m in modes) { m.AutoSize = true; modeRow.Controls.Add(m); }
        modes[0].Checked = true;
        root.Controls.Add(modeRow);
        root.Controls.Add(slim);
        root.Controls.Add(webui);

        root.Controls.Add(Heading("4. Install"));
        root.Controls.Add(Row(install));
        root.Controls.Add(bar);
        root.Controls.Add(log);

        keyFile.Text = DefaultKey() ?? "";
        foreach (var z in new[] { Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + "\\Downloads", Environment.GetFolderPath(Environment.SpecialFolder.Desktop) })
            if (Directory.Exists(z)) { var f = Directory.GetFiles(z, "Rumbleverse*.zip").FirstOrDefault(); if (f != null) { zipFile.Text = f; break; } }

        browseKey.Click += (_, _) => { using var d = new OpenFileDialog { Title = "Your private SSH key (not the .pub file)", InitialDirectory = SshDir() }; if (d.ShowDialog() == DialogResult.OK) keyFile.Text = d.FileName; };
        browseZip.Click += (_, _) => { using var d = new OpenFileDialog { Filter = "Zip files|*.zip" }; if (d.ShowDialog() == DialogResult.OK) zipFile.Text = d.FileName; };
        newKey.Click += (_, _) => Try(() => { keyFile.Text = Deployer.CreateKey(NewKeyPath()); Log($"Created a new SSH key: {keyFile.Text}"); });
        community.CheckedChanged += (_, _) => { contact.Enabled = contactLabel.Enabled = community.Checked; };
        check.Click += async (_, _) => await CheckVps();
        install.Click += async (_, _) => await Install();
        FormClosing += (_, _) => cancel.Cancel();
    }

    static Label Heading(string t) => new() { Text = t, AutoSize = true, Font = new Font(SystemFonts.DefaultFont.FontFamily, 10, FontStyle.Bold), Margin = new Padding(0, 12, 0, 4) };
    static Label Note(string t) => new() { Text = t, AutoSize = true, MaximumSize = new Size(620, 0), ForeColor = SystemColors.GrayText };
    static Label Label(string t) => new() { Text = t, AutoSize = true, Margin = new Padding(3, 7, 3, 3) };
    static FlowLayoutPanel Row(params Control[] c) { var p = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) }; p.Controls.AddRange(c); return p; }
    static string SshDir() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
    static string? DefaultKey() => new[] { "id_ed25519", "id_ecdsa", "id_rsa", "rvserver_ed25519" }.Select(k => Path.Combine(SshDir(), k)).FirstOrDefault(File.Exists);
    static string NewKeyPath() { var p = Path.Combine(SshDir(), "rvserver_ed25519"); for (int i = 2; File.Exists(p); i++) p = Path.Combine(SshDir(), $"rvserver{i}_ed25519"); return p; }

    void Log(string s) { if (InvokeRequired) { BeginInvoke(() => Log(s)); return; } log.AppendText(s + Environment.NewLine); }
    void Progress(double? f) { if (InvokeRequired) { BeginInvoke(() => Progress(f)); return; } bar.Visible = f != null; if (f != null) bar.Value = (int)(Math.Clamp(f.Value, 0, 1) * 100); }
    void Try(Action a) { try { a(); } catch (Exception e) { MessageBox.Show(this, e.Message, "Rumbleverse server setup", MessageBoxButtons.OK, MessageBoxIcon.Warning); } }
    void Busy(bool b) { UseWaitCursor = b; check.Enabled = !b; install.Enabled = !b && info != null; }

    Deployer NewDeployer() => new(Log, Progress);

    async Task CheckVps()
    {
        if (host.Text.Trim() == "") { Try(() => throw new InvalidOperationException("Enter the VPS address.")); return; }
        Busy(true);
        try
        {
            var d = NewDeployer();
            if (File.Exists(keyFile.Text) && await Task.Run(() => d.IsCoreOS(host.Text, keyFile.Text)))
            {
                alreadyCoreOS = true;
                info = await Task.Run(() =>
                {
                    using var ssh = d.Connect(host.Text, "core", null, keyFile.Text);
                    return Deployer.Parse(d.Run(ssh, "echo MEM_MB=$(awk '/^MemTotal:/ {print int($2/1024)}' /proc/meminfo); " +
                        "echo V4=$(ip -4 route get 1.1.1.1 | sed -n 's/.* src \\([0-9.]*\\).*/\\1/p')").output);
                });
                vpsInfo.Text = "This VPS already runs Fedora CoreOS with your key. Install continues with the upload of the game zip.";
                vpsInfo.ForeColor = SystemColors.ControlText;
                Log("Already set up with Fedora CoreOS: Install will continue with the upload.");
                return;
            }
            alreadyCoreOS = false;
            Log($"Connecting to {user.Text}@{host.Text}...");
            info = await Task.Run(() => { using var ssh = d.Connect(host.Text.Trim(), user.Text.Trim(), password.Text, keyFile.Text); return d.Check(ssh, password.Text); });
            int mem = int.Parse(info["MEM_MB"]), disk = int.Parse(info["DISK_GB"]);
            var problems = new List<string>();
            if (disk < 30) problems.Add($"The disk is {disk} GB; about 30 GB is needed.");
            if (mem < 3500) problems.Add($"The VPS has {mem} MB of RAM; one game mode needs about 4 GB.");
            // About 2.9 GB for the first mode and 2.5 GB for each further one with RAM saving, 3.9 GB without.
            int fit = Math.Clamp(1 + (mem - 900 - 2900) / 2500, 1, 5), fitPlain = Math.Clamp((mem - 900) / 3900, 1, 5);
            vpsInfo.Text = $"{info.GetValueOrDefault("OS")}: {info["CPUS"]} CPUs, {mem / 1024.0:0.0} GB RAM, {disk} GB disk\n" +
                           $"IPv4 {info.GetValueOrDefault("V4")} via {info.GetValueOrDefault("GW4")} ({info.GetValueOrDefault("V4MODE")})" +
                           (string.IsNullOrEmpty(info.GetValueOrDefault("V6")) ? "" : $"\nIPv6 {info["V6"]} via {info.GetValueOrDefault("GW6")} ({info.GetValueOrDefault("V6MODE")})") +
                           $"\nAbout {fit} game mode(s) fit in this RAM with RAM saving ({fitPlain} without)." + (problems.Count > 0 ? "\n\n" + string.Join("\n", problems) : "");
            vpsInfo.ForeColor = problems.Count > 0 ? Color.DarkRed : SystemColors.ControlText;
            if (problems.Count > 0) info = null;
            Log(problems.Count > 0 ? "This VPS can't be used." : "The VPS looks fine.");
        }
        catch (Exception e) { info = null; Log("Check failed: " + e.Message); Try(() => throw e); }
        finally { Busy(false); }
    }

    async Task Install()
    {
        if (info == null) return;
        if (!File.Exists(zipFile.Text) || new FileInfo(zipFile.Text).Length < 5000L * 1048576) { Try(() => throw new InvalidOperationException("Choose your Rumbleverse game zip (about 11 GB).")); return; }
        if (community.Checked && contact.Text.Trim() == "") { Try(() => throw new InvalidOperationException("Enter your Discord name, so the admins can contact you about the approval.")); return; }
        var chosen = modes.Select((m, i) => m.Checked ? ModeIds[i] : null).Where(x => x != null).ToList();
        if (chosen.Count == 0) { Try(() => throw new InvalidOperationException("Choose at least one mode.")); return; }
        if (!alreadyCoreOS && MessageBox.Show(this, $"This ERASES everything on {host.Text} and installs Fedora CoreOS with the Rumbleverse server.\n\nThe upload of the game zip can take a while. Continue?",
            "Erase the VPS?", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;

        Busy(true); install.Enabled = false;
        string h = host.Text.Trim(), u = user.Text.Trim(), pw = password.Text, zip = zipFile.Text;
        try
        {
            if (!File.Exists(keyFile.Text)) { keyFile.Text = Deployer.CreateKey(NewKeyPath()); Log($"Created a new SSH key: {keyFile.Text}"); }
            string key = keyFile.Text, pub = File.ReadAllText(key + ".pub");
            var d = NewDeployer();
            var env = new Dictionary<string, string>
            {
                ["RV_GAME_ZIP"] = Deployer.RemoteZip, ["RV_DATA_DIR"] = "/var/srv/rvserver",
                ["RV_EDITION"] = community.Checked ? "community" : "private",
                ["RV_MODES"] = string.Join(",", chosen), ["RV_SLIM"] = slim.Checked ? "on" : "off", ["RV_KSM"] = chosen.Count > 1 ? "on" : "off", ["RV_WEBUI"] = webui.Checked ? "on" : "off",
            };
            if (community.Checked) env["RV_CONTACT"] = contact.Text.Trim();
            if (serverName.Text.Trim() != "") env["RV_NAME"] = serverName.Text.Trim();

            if (!alreadyCoreOS)
            {
                Log("Preparing the VPS for Fedora CoreOS...");
                await Task.Run(() => { using var ssh = d.Connect(h, u, pw, key); d.Check(ssh, pw); d.StartReinstall(ssh, pw, pub); });
                Log("The VPS is rebooting into the installer. Writing Fedora CoreOS takes about 5-10 minutes.");
                await Task.Run(() => { using var ssh = d.WaitForCoreOS(h, key, cancel.Token); });
                alreadyCoreOS = true;
                Log("Fedora CoreOS is up.");
            }
            Log("Uploading the game zip...");
            await Task.Run(() => d.UploadZip(() => d.Connect(h, "core", null, key), zip, cancel.Token));
            await Task.Run(() => { using var ssh = d.Connect(h, "core", null, key); d.VerifyZip(ssh, zip); });

            if (privateServer.Checked)
            {
                var code = Prompt("Setup code", "Get a setup code in your rVclient launcher:\nServer Status > My private servers > Set up a private server.\n\nSetup code (like ABCDE-FGH23):");
                if (string.IsNullOrWhiteSpace(code)) throw new InvalidOperationException("No setup code given. Press Install again when you have one (the upload is kept).");
                env["RV_SETUP_CODE"] = code.Trim();
            }
            Log("Starting the game server...");
            var output = await Task.Run(() => { using var ssh = d.Connect(h, "core", null, key); return d.RunInstall(ssh, env, testOnly); });
            Log("");
            Log("All done.");
            Log($"Log in to your server:  ssh -i \"{key}\" core@{h}");
            Log("Server menu:            sudo podman exec -it rvserver rv menu");
            MessageBox.Show(this, "Your Rumbleverse server is installed. The details are in the log window.", "Done", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { Log("Stopped: " + e.Message); Try(() => throw e); }
        finally { Progress(null); Busy(false); }
    }

    string? Prompt(string title, string text)
    {
        using var f = new Form { Text = title, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent, Padding = new Padding(12), MinimizeBox = false, MaximizeBox = false };
        var box = new TextBox { Width = 260 };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true };
        var p = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true };
        p.Controls.Add(new Label { Text = text, AutoSize = true }); p.Controls.Add(box); p.Controls.Add(ok);
        f.Controls.Add(p); f.AcceptButton = ok;
        return f.ShowDialog(this) == DialogResult.OK ? box.Text : null;
    }
}
