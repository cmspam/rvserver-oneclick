# Rumbleverse server one-click installer

Turns a fresh VPS into a Rumbleverse community or private server, from your own computer. You need:

- a VPS: x86_64, KVM (not a container VPS), at least 4 GB RAM and 30 GB disk, with a fresh Debian or
  Ubuntu you can log in to with SSH (root password or SSH key)
- your Rumbleverse game zip (about 11 GB). This project never provides game files.

**The VPS is erased.** It gets Fedora CoreOS, set up for a game server, and then the server from
[rvclient-community-servers](https://github.com/cmspam/rvclient-community-servers) (Linux container).

What happens, in order:

1. checks the VPS and reads its network settings (static or DHCP, IPv6)
2. asks about the server: community or private, your Discord name, modes, RAM saving, web admin page
3. installs Fedora CoreOS (about 5 to 10 minutes; the VPS reboots)
4. uploads your game zip (continues where it stopped if the connection drops, and checks it afterwards)
5. runs the server's `install.sh` with your answers, which starts the server

After that you log in as `core@<address>` with your SSH key.

## Windows

Download `RVDeploy.exe` from [Releases](../../releases) and run it. Fill in the VPS address, the root
password (or pick your SSH key), choose your game zip, press **Check the VPS**, then **Install**.

Without an SSH key it makes one for you with Windows' own `ssh-keygen` (in `%USERPROFILE%\.ssh`). Keep it:
you need it to log in to the finished server.

If the upload is interrupted, run it again with the same address and key. It finds the finished CoreOS
install and continues with the upload.

## Linux, macOS, WSL

```sh
curl -fsSLO https://raw.githubusercontent.com/cmspam/rvserver-oneclick/main/deploy.sh
bash deploy.sh root@203.0.113.10 ~/Downloads/Rumbleverse-client-z.zip
```

Add the port if SSH is not on 22: `root@203.0.113.10:2222`. Run it again after an interruption, as above.

## RAM saving

On by default. The server frees graphics and sound data it never uses, so each mode needs about 2.5 GB
instead of 3.9 GB. It changes nothing for players. See
[Saving memory](https://github.com/cmspam/rvclient-community-servers#saving-memory) for details and how to
turn it off later (`RV_SLIM=off`).

## Files

| File | |
|---|---|
| `deploy.sh` | the installer for Linux, macOS and WSL |
| `deploy-remote.sh` | the part that runs on the VPS (both installers use it) |
| `windows/RVDeploy` | the Windows program (C#, .NET 8, [SSH.NET](https://github.com/sshnet/SSH.NET)); `deploy-remote.sh` is built into it |

Build the Windows program yourself:

```sh
dotnet publish windows/RVDeploy -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist
```

A tag `v*` builds it on GitHub and attaches it to the release.
