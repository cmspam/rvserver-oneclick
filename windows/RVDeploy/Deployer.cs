using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Renci.SshNet;

namespace RVDeploy;

// The steps of a deploy. Talks to the VPS over SSH; the work on the VPS is done by deploy-remote.sh,
// which is built into this program, and the server's install.sh, which the VPS downloads.
public sealed class Deployer
{
    public const string ReinstallUrl = "https://raw.githubusercontent.com/cmspam/cache22/fcos-ignition/installer/reinstall";
    public const string FcosVersion = "44.20260913.3.2";
    public const string RemoteZip = "/var/home/core/Rumbleverse-client-z.zip";
    public const string InstallShUrl = "https://raw.githubusercontent.com/cmspam/rvclient-community-servers/main/linux/install.sh";

    readonly Action<string> log;
    readonly Action<double?> progress;   // 0..1, or null to hide
    public Deployer(Action<string> log, Action<double?> progress) { this.log = log; this.progress = progress; }

    public static string Resource(string name)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name) ?? throw new InvalidOperationException($"missing {name}");
        using var r = new StreamReader(s);
        return r.ReadToEnd().Replace("\r\n", "\n");
    }

    // ------------------------------------------------------------ SSH
    // "host" may carry a port: 203.0.113.10:2222
    public static (string host, int port) SplitHost(string host)
    {
        host = host.Trim();
        int i = host.LastIndexOf(':');
        if (i > 0 && host.IndexOf(':') == i && int.TryParse(host[(i + 1)..], out var p)) return (host[..i], p);
        return (host, 22);
    }

    public SshClient Connect(string hostAndPort, string user, string? password, string? keyFile)
    {
        var (host, port) = SplitHost(hostAndPort);
        var methods = new List<AuthenticationMethod>();
        if (!string.IsNullOrEmpty(keyFile) && File.Exists(keyFile)) methods.Add(new PrivateKeyAuthenticationMethod(user, new PrivateKeyFile(keyFile)));
        if (!string.IsNullOrEmpty(password))
        {
            methods.Add(new PasswordAuthenticationMethod(user, password));
            var kbd = new KeyboardInteractiveAuthenticationMethod(user);
            kbd.AuthenticationPrompt += (_, e) => { foreach (var p in e.Prompts) p.Response = password; };
            methods.Add(kbd);
        }
        if (methods.Count == 0) throw new InvalidOperationException("Enter the VPS password or choose an SSH key.");
        var info = new ConnectionInfo(host, port, user, methods.ToArray()) { Timeout = TimeSpan.FromSeconds(20) };
        var ssh = new SshClient(info) { KeepAliveInterval = TimeSpan.FromSeconds(15) };
        ssh.HostKeyReceived += (_, e) => e.CanTrust = true;   // first contact with a fresh VPS
        ssh.Connect();
        return ssh;
    }

    public static string Quote(string s) => "'" + s.Replace("'", "'\"'\"'") + "'";

    // Runs a command; root commands go through sudo when logged in as someone else.
    public (int code, string output) Run(SshClient ssh, string command, string? sudoPassword = null, bool asRoot = false, Action<string>? lines = null)
    {
        bool sudo = asRoot && ssh.ConnectionInfo.Username != "root";
        string text = sudo ? $"sudo -S -p '' bash -c {Quote(command)}" : command;
        using var cmd = ssh.CreateCommand(text);
        cmd.CommandTimeout = TimeSpan.FromHours(3);
        var started = cmd.BeginExecute();
        if (sudo)
        {
            using var input = cmd.CreateInputStream();
            var b = Encoding.UTF8.GetBytes((sudoPassword ?? "") + "\n");
            input.Write(b, 0, b.Length);
        }
        var all = new StringBuilder();
        using (var reader = new StreamReader(cmd.OutputStream))
        {
            string? line;
            while ((line = reader.ReadLine()) != null) { all.AppendLine(line); lines?.Invoke(line); }
        }
        cmd.EndExecute(started);
        return (cmd.ExitStatus ?? -1, all.ToString() + cmd.Error);
    }

    public void PutText(SshClient ssh, string path, string text)
    {
        using var cmd = ssh.CreateCommand($"cat > {Quote(path)}");
        var started = cmd.BeginExecute();
        using (var input = cmd.CreateInputStream())
        {
            var b = Encoding.UTF8.GetBytes(text);
            input.Write(b, 0, b.Length);
        }
        cmd.EndExecute(started);
        if (cmd.ExitStatus != 0) throw new IOException($"could not write {path}: {cmd.Error}");
    }

    // ------------------------------------------------------------ steps
    public Dictionary<string, string> Check(SshClient ssh, string? password)
    {
        PutText(ssh, "/tmp/rv-prepare.sh", Resource("deploy-remote.sh"));
        var (_, output) = Run(ssh, "bash /tmp/rv-prepare.sh check", password, asRoot: true);
        var values = Parse(output);
        if (values.TryGetValue("FAIL", out var why)) throw new InvalidOperationException($"This VPS can't be used: {why}");
        if (!values.ContainsKey("MEM_MB")) throw new InvalidOperationException("The VPS check did not answer:\n" + output);
        return values;
    }

    public static Dictionary<string, string> Parse(string output)
    {
        var d = new Dictionary<string, string>();
        foreach (var line in output.Split('\n'))
        {
            var m = Regex.Match(line.TrimEnd('\r'), "^([A-Z0-9_]+)=(.*)$");
            if (m.Success && !d.ContainsKey(m.Groups[1].Value)) d[m.Groups[1].Value] = m.Groups[2].Value;
        }
        return d;
    }

    // Writes the CoreOS configuration, prepares the reinstall and reboots the VPS into it.
    public void StartReinstall(SshClient ssh, string? password, string publicKey)
    {
        var keyB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(publicKey.Trim()));
        var (_, output) = Run(ssh, $"bash /tmp/rv-prepare.sh install {keyB64} {ReinstallUrl} {FcosVersion}", password, asRoot: true, lines: l => { if (l.StartsWith("FAIL=")) log(l); });
        var v = Parse(output);
        if (v.TryGetValue("FAIL", out var why)) throw new InvalidOperationException($"Could not start the install: {why}");
        if (!v.ContainsKey("READY")) throw new InvalidOperationException("The install did not get ready:\n" + output);
        try { Run(ssh, "nohup sh -c 'sleep 3; reboot' >/dev/null 2>&1 &", password, asRoot: true); } catch { /* the connection drops */ }
    }

    // The installer in between answers SSH too; only Fedora CoreOS taking our key as core counts.
    public SshClient WaitForCoreOS(string host, string keyFile, CancellationToken ct)
    {
        var start = DateTime.UtcNow;
        Thread.Sleep(60000);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (IsCoreOS(host, keyFile)) return Connect(host, "core", null, keyFile);
            var min = (DateTime.UtcNow - start).TotalMinutes;
            if (min > 40) throw new TimeoutException("CoreOS did not come up within 40 minutes. Check the VPS console in your provider's panel.");
            log($"Waiting for Fedora CoreOS to come up... {min:0} min");
            Thread.Sleep(15000);
        }
    }

    // True when the VPS already runs Fedora CoreOS and takes our key as core.
    public bool IsCoreOS(string host, string keyFile)
    {
        try
        {
            using var ssh = Connect(host, "core", null, keyFile);
            return Run(ssh, "grep -q 'Fedora CoreOS' /etc/os-release && echo yes").output.Trim() == "yes";
        }
        catch { return false; }
    }

    long RemoteSize(SshClient ssh)
    {
        var (_, o) = Run(ssh, $"stat -c %s {RemoteZip} 2>/dev/null || echo 0");
        return long.TryParse(o.Trim().Split('\n')[0], out var n) ? n : 0;
    }

    // Uploads the zip through "cat >> file", resuming after a dropped connection.
    public void UploadZip(Func<SshClient> connect, string zip, CancellationToken ct)
    {
        long total = new FileInfo(zip).Length;
        for (int attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            using var ssh = connect();
            long have = RemoteSize(ssh);
            if (have > total) { Run(ssh, $"rm -f {RemoteZip}"); have = 0; }
            if (have == total) break;
            if (have > 0) log($"Resuming the upload at {have / 1048576} MB.");
            try
            {
                using var cmd = ssh.CreateCommand($"cat >> {RemoteZip}");
                cmd.CommandTimeout = TimeSpan.FromHours(24);
                var started = cmd.BeginExecute();
                using (var input = cmd.CreateInputStream())
                using (var fs = new FileStream(zip, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
                {
                    fs.Seek(have, SeekOrigin.Begin);
                    var buf = new byte[1 << 20];
                    long sent = have; var last = DateTime.UtcNow; long lastSent = sent;
                    int n;
                    while ((n = fs.Read(buf, 0, buf.Length)) > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        input.Write(buf, 0, n);
                        sent += n;
                        progress((double)sent / total);
                        if ((DateTime.UtcNow - last).TotalSeconds >= 10)
                        {
                            double rate = (sent - lastSent) / (DateTime.UtcNow - last).TotalSeconds;
                            var left = rate > 0 ? TimeSpan.FromSeconds((total - sent) / rate) : TimeSpan.Zero;
                            log($"Uploaded {sent / 1048576} of {total / 1048576} MB ({rate / 1048576:0.0} MB/s, about {left.TotalMinutes:0} min left)");
                            last = DateTime.UtcNow; lastSent = sent;
                        }
                    }
                }
                cmd.EndExecute(started);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e)
            {
                if (attempt >= 50) throw;
                log($"The upload stopped ({e.Message}). Retrying in 15 seconds.");
                Thread.Sleep(15000);
            }
        }
        progress(null);
    }

    public void VerifyZip(SshClient ssh, string zip)
    {
        log("Checking the upload (comparing checksums)...");
        string remote = "";
        var t = Task.Run(() => remote = Run(ssh, $"sha256sum {RemoteZip} | cut -c1-64").output.Trim());
        using var sha = SHA256.Create();
        using (var fs = new FileStream(zip, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
        {
            var buf = new byte[1 << 20]; long done = 0, total = fs.Length; int n;
            while ((n = fs.Read(buf, 0, buf.Length)) > 0) { sha.TransformBlock(buf, 0, n, null, 0); done += n; progress((double)done / total); }
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        }
        progress(null);
        t.Wait();
        var local = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
        if (local != remote)
        {
            Run(ssh, $"rm -f {RemoteZip}");
            throw new InvalidOperationException("The uploaded zip is damaged and was removed. Press Install again to upload it again.");
        }
        log("Upload complete and verified.");
    }

    public string RunInstall(SshClient ssh, Dictionary<string, string> env, bool testOnly)
    {
        var (got, _) = Run(ssh, $"curl -fsSLo /tmp/install.sh {Quote(InstallShUrl)}");
        if (got != 0) throw new InvalidOperationException("The VPS could not download install.sh from GitHub. Check its internet connection and press Install again.");
        var envs = string.Join(" ", env.Select(kv => $"{kv.Key}={Quote(kv.Value)}"));
        var script = testOnly ? "echo 'TEST: would run install.sh with:'; env | grep ^RV_ | sort" : "bash /tmp/install.sh";
        var ansi = new Regex(@"\x1b\[[0-9;]*m");
        var (code, output) = Run(ssh, $"sudo env RV_UNATTENDED=1 {envs} bash -c {Quote(script)}", lines: l => log(ansi.Replace(l, "")));
        if (code != 0) throw new InvalidOperationException("install.sh stopped with an error (see the log).");
        return ansi.Replace(output, "");
    }

    // ------------------------------------------------------------ SSH key (Windows' own ssh-keygen)
    public static string CreateKey(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var psi = new ProcessStartInfo("ssh-keygen", $"-t ed25519 -N \"\" -C rvserver -f \"{path}\"")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start ssh-keygen.");
        p.WaitForExit();
        if (p.ExitCode != 0 || !File.Exists(path)) throw new InvalidOperationException("ssh-keygen failed: " + p.StandardError.ReadToEnd());
        return path;
    }
}
