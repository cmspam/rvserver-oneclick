#!/usr/bin/env bash
# Turns a fresh VPS into a Rumbleverse server, run from your own computer (Linux, macOS or WSL).
#
#   curl -fsSLO https://raw.githubusercontent.com/cmspam/rvserver-oneclick/main/deploy.sh
#   bash deploy.sh root@203.0.113.10 ~/Downloads/Rumbleverse-client-z.zip
#
# The VPS needs a freshly installed Debian or Ubuntu that you can log in to with SSH. The script:
#   1. checks the VPS and reads its network settings
#   2. asks a few questions about the server
#   3. ERASES the VPS and installs Fedora CoreOS on it, tuned for a game server
#   4. uploads your game zip (it resumes if the connection drops)
#   5. runs install.sh there with your answers, which starts the server
# After that you log in as core@<address> with your SSH key.
set -euo pipefail

INSTALL_SH_URL="${RV_INSTALL_SH_URL:-https://raw.githubusercontent.com/cmspam/rvclient-community-servers/main/linux/install.sh}"
REMOTE_SH_URL="${RV_REMOTE_SH_URL:-https://raw.githubusercontent.com/cmspam/rvserver-oneclick/main/deploy-remote.sh}"
REINSTALL_URL="https://raw.githubusercontent.com/cmspam/cache22/fcos-ignition/installer/reinstall"
FCOS_VERSION="${RV_FCOS_VERSION:-44.20260913.3.2}"
REMOTE_ZIP=/var/home/core/Rumbleverse-client-z.zip
MODES_ALL="solo playground duos trios squads"

bold() { printf '\033[1m%s\033[0m\n' "$*"; }
info() { printf '  %s\n' "$*"; }
warn() { printf '\033[33m  %s\033[0m\n' "$*"; }
die() { printf '\033[31m\n  %s\033[0m\n\n' "$*" >&2; exit 1; }
ask() {   # ask "question" "default" -> REPLY
    if [ -n "${2:-}" ]; then read -r -p "  $1 [$2]: " REPLY; else read -r -p "  $1: " REPLY; fi
    REPLY="${REPLY:-${2:-}}"
}
yesno() { ask "$1 (y/n)" "$2"; case "$REPLY" in [Yy]*) return 0 ;; *) return 1 ;; esac; }
filesize() { wc -c < "$1" | tr -d ' '; }
sha256() { if command -v sha256sum >/dev/null 2>&1; then sha256sum "$1" | cut -c1-64; else shasum -a 256 "$1" | cut -c1-64; fi; }

TARGET="${1:-}"; ZIP="${2:-}"
[ -n "$TARGET" ] || { echo "Usage: bash deploy.sh [user@]vps-address[:port] [path/to/Rumbleverse-client-z.zip]"; exit 1; }
case "$TARGET" in *@*) LOGIN_USER="${TARGET%@*}"; HOST="${TARGET#*@}" ;; *) LOGIN_USER=root; HOST="$TARGET" ;; esac
PORT=22; case "$HOST" in *:*) PORT="${HOST##*:}"; HOST="${HOST%:*}" ;; esac
for t in ssh ssh-keygen; do command -v "$t" >/dev/null 2>&1 || die "Needs '$t' on this computer."; done

STATE="$HOME/.rvdeploy/$HOST"; mkdir -p "$STATE"; chmod 700 "$HOME/.rvdeploy"
CTL="$STATE/ctl-%r"
SSH_OLD=(ssh -p "$PORT" -o ServerAliveInterval=15 -o ControlMaster=auto -o "ControlPath=$CTL" -o ControlPersist=900 -o StrictHostKeyChecking=accept-new)
SSH_NEW=(ssh -p "$PORT" -o ServerAliveInterval=15 -o ControlMaster=auto -o "ControlPath=$CTL" -o ControlPersist=900
         -o "UserKnownHostsFile=$STATE/known_hosts" -o StrictHostKeyChecking=accept-new)
# Already our Fedora CoreOS (a second run after an interrupted upload)?
is_coreos() {
    ssh -p "$PORT" -o BatchMode=yes -o ConnectTimeout=10 -o UserKnownHostsFile=/dev/null -o StrictHostKeyChecking=no \
        -o LogLevel=ERROR "core@$HOST" "grep -q 'Fedora CoreOS' /etc/os-release" </dev/null 2>/dev/null
}
pin_coreos_key() { rm -f "$STATE/known_hosts"; "${SSH_NEW[@]}" -o BatchMode=yes "core@$HOST" true </dev/null; }
trap 'ssh -p "$PORT" -o "ControlPath=$CTL" -O exit "$LOGIN_USER@$HOST" >/dev/null 2>&1 || true; ssh -p "$PORT" -o "ControlPath=$CTL" -O exit "core@$HOST" >/dev/null 2>&1 || true' EXIT

echo
bold "Rumbleverse server deploy"
echo
info "Target: $LOGIN_USER@$HOST"
echo

# ---------------------------------------------------------------- local checks
bold "1. On this computer"
while :; do
    if [ -z "$ZIP" ]; then
        for f in ./Rumbleverse*.zip "$HOME"/Rumbleverse*.zip "$HOME"/Downloads/Rumbleverse*.zip; do [ -f "$f" ] && { ZIP="$f"; break; }; done
        ask "Where is your Rumbleverse game zip?" "$ZIP"; ZIP="${REPLY/#\~/$HOME}"
    fi
    [ -f "$ZIP" ] || { warn "No file at $ZIP"; ZIP=""; continue; }
    ZIP_BYTES=$(filesize "$ZIP")
    [ "$ZIP_BYTES" -ge $((5000 * 1048576)) ] || { warn "$ZIP is only $((ZIP_BYTES / 1048576)) MB - the game zip is about 11 GB."; ZIP=""; continue; }
    break
done
info "Game zip: $ZIP ($((ZIP_BYTES / 1048576)) MB)"

KEY=""
for k in "$HOME/.ssh/id_ed25519.pub" "$HOME/.ssh/id_ecdsa.pub" "$HOME/.ssh/id_rsa.pub"; do [ -f "$k" ] && { KEY="$k"; break; }; done
if [ -z "$KEY" ]; then
    warn "No SSH key found in ~/.ssh. You need one to log in to the server afterwards."
    yesno "Create one now (ssh-keygen)?" "y" || die "Create an SSH key first (ssh-keygen -t ed25519), then run this again."
    ssh-keygen -t ed25519 -f "$HOME/.ssh/id_ed25519" </dev/tty
    KEY="$HOME/.ssh/id_ed25519.pub"
fi
PUBKEY="$(cat "$KEY")"
info "SSH key:  $KEY"
echo

# ---------------------------------------------------------------- the VPS as it is now
bold "2. Checking the VPS"
ALREADY=0
if is_coreos; then
    ALREADY=1
    pin_coreos_key
    info "$HOST already runs Fedora CoreOS with your key: continuing with the upload."
    MEM_MB=$("${SSH_NEW[@]}" "core@$HOST" "awk '/^MemTotal:/ {print int(\$2/1024)}' /proc/meminfo" </dev/null)
    INFO=""
else
info "Logging in to $LOGIN_USER@$HOST (enter its password if asked)."
SUDO=""; [ "$LOGIN_USER" = root ] || SUDO="sudo"
"${SSH_OLD[@]}" "$LOGIN_USER@$HOST" true </dev/null || die "Could not log in to $LOGIN_USER@$HOST."

# The part that runs on the VPS (deploy-remote.sh, next to this script or downloaded).
HERE="$(cd "$(dirname "$0")" && pwd)"
if [ -f "$HERE/deploy-remote.sh" ]; then "${SSH_OLD[@]}" "$LOGIN_USER@$HOST" "cat > /tmp/rv-prepare.sh" < "$HERE/deploy-remote.sh"
else "${SSH_OLD[@]}" "$LOGIN_USER@$HOST" </dev/null "curl -fsSLo /tmp/rv-prepare.sh $(printf %q "$REMOTE_SH_URL") || wget -qO /tmp/rv-prepare.sh $(printf %q "$REMOTE_SH_URL")"; fi

INFO="$("${SSH_OLD[@]}" "$LOGIN_USER@$HOST" "$SUDO bash /tmp/rv-prepare.sh check" </dev/null)"
FAIL=$(echo "$INFO" | sed -n 's/^FAIL=//p'); [ -z "$FAIL" ] || die "This VPS can't be used: $FAIL"
get() { echo "$INFO" | sed -n "s/^$1=//p" | head -1; }
MEM_MB=$(get MEM_MB); DISK_GB=$(get DISK_GB)
info "System:   $(get OS)"
info "Hardware: $(get CPUS) CPUs, $((MEM_MB / 1024)).$(( (MEM_MB % 1024) * 10 / 1024 )) GB RAM, $DISK_GB GB disk"
info "IPv4:     $(get V4) via $(get GW4) ($(get V4MODE))"
[ -n "$(get V6)" ] && info "IPv6:     $(get V6) via $(get GW6) ($(get V6MODE))"
info "DNS:      $(get DNS)"
[ "$DISK_GB" -ge 30 ] || die "The disk is $DISK_GB GB; the game zip and the unpacked game need about 30 GB."
[ "$DISK_GB" -ge 40 ] || warn "$DISK_GB GB of disk works, but 40 GB or more leaves room for updates."
[ "$MEM_MB" -ge 3500 ] || die "The VPS has $MEM_MB MB of RAM; one game mode needs about 4 GB."
fi
echo

# ---------------------------------------------------------------- questions
bold "3. Your server"
info "  1) Community server: public, players everywhere join it through matchmaking."
info "     The rVclient admins approve it first."
info "  2) Private server: only you and the friends you share it with."
ask "Choose 1 or 2" "1"
EDITION=community; [ "$REPLY" = 2 ] && EDITION=private
CONTACT=""; NAME=""
if [ "$EDITION" = community ]; then
    while :; do ask "Your Discord name (the admins contact you about the approval)" ""; [ -n "$REPLY" ] && break; done
    CONTACT="$REPLY"
    ask "Name for your server (only the admins see it)" ""; NAME="$REPLY"
else
    ask "Name for your server (you and your friends see it)" "Linux private server"; NAME="$REPLY"
    info "You'll be asked for a setup code from your launcher at the end (it's only valid 30 minutes)."
fi
info "RAM saving frees graphics and sound data the game server never uses: about 2.5 GB per mode"
info "instead of 3.9 GB. It changes nothing for players."
SLIM=on; yesno "Switch RAM saving on? (recommended)" "y" || SLIM=off
if [ "$SLIM" = on ]; then FIRST=2900; EACH=2500; else FIRST=3900; EACH=3900; fi
FIT=$(( 1 + (MEM_MB - 900 - FIRST) / EACH )); [ "$FIT" -lt 1 ] && FIT=1; [ "$FIT" -gt 5 ] && FIT=5
info "Game modes (each is its own game server; about $FIT fit in this VPS's RAM):"
i=1; for m in Solos Playground Duos Trios Squads; do info "  $i) $m"; i=$((i + 1)); done
DEF="1"; [ "$EDITION" = private ] && DEF="2"
while :; do
    ask "Modes to run (numbers separated by spaces)" "$DEF"
    MODES=""; ok=1
    for n in $REPLY; do
        case "$n" in [1-5]) MODES="${MODES:+$MODES,}$(echo $MODES_ALL | cut -d' ' -f"$n")" ;; *) ok=0 ;; esac
    done
    [ "$ok" = 1 ] && [ -n "$MODES" ] && break
    warn "Use the numbers 1 to 5."
done
COUNT=$(echo "$MODES" | tr ',' '\n' | sort -u | wc -l | tr -d ' ')
KSM=off; [ "$COUNT" -gt 1 ] && KSM=on
WEBUI=on; yesno "Turn on the web admin page (port 8080)?" "y" || WEBUI=off
echo

# ---------------------------------------------------------------- the point of no return
if [ "$ALREADY" = 0 ]; then
bold "4. Install Fedora CoreOS"
warn "This ERASES everything on $HOST and installs Fedora CoreOS."
warn "It keeps the network settings shown above. You'll log in as core@$HOST with your SSH key."
ask "Type yes to continue" ""
[ "$REPLY" = yes ] || die "Stopped. Nothing was changed."
KEY_B64=$(printf '%s' "$PUBKEY" | base64 | tr -d '\n')
OUT="$("${SSH_OLD[@]}" "$LOGIN_USER@$HOST" "$SUDO bash /tmp/rv-prepare.sh install $KEY_B64 $REINSTALL_URL $FCOS_VERSION" </dev/null)"
FAIL=$(echo "$OUT" | sed -n 's/^FAIL=//p'); [ -z "$FAIL" ] || die "Could not start the install: $FAIL"
echo "$OUT" | grep -q '^READY=1' || die "The install did not get ready. Output:
$OUT"
info "Rebooting into the installer. Writing CoreOS takes about 5-10 minutes."
"${SSH_OLD[@]}" "$LOGIN_USER@$HOST" "$SUDO sh -c 'sleep 2; reboot' >/dev/null 2>&1 &" </dev/null || true
ssh -p "$PORT" -o "ControlPath=$CTL" -O exit "$LOGIN_USER@$HOST" >/dev/null 2>&1 || true
rm -f "$STATE/known_hosts"
sleep 60
START=$(date +%s)
# The installer in between answers SSH with its own host key, so nothing is recorded while waiting;
# CoreOS's key is pinned once the login works and the system says it is Fedora CoreOS.
until is_coreos; do
    el=$(( $(date +%s) - START ))
    [ "$el" -lt 2400 ] || die "CoreOS did not come up within 40 minutes. Check the VPS console in your provider's panel."
    printf '\r  waiting for CoreOS... %d min' $((el / 60)); sleep 15
done
printf '\r  CoreOS is up.                    \n'
pin_coreos_key
echo
fi

# ---------------------------------------------------------------- the game zip
bold "5. Uploading the game zip"
RSIZE() { "${SSH_NEW[@]}" "core@$HOST" "stat -c %s $REMOTE_ZIP 2>/dev/null || echo 0" </dev/null; }
while :; do
    have=$(RSIZE)
    [ "$have" -le "$ZIP_BYTES" ] || { "${SSH_NEW[@]}" "core@$HOST" "rm -f $REMOTE_ZIP" </dev/null; have=0; }
    [ "$have" -lt "$ZIP_BYTES" ] || break
    [ "$have" -gt 0 ] && info "Resuming at $((have / 1048576)) MB."
    ( while sleep 20; do s=$(RSIZE 2>/dev/null || echo 0); printf '\r  %d of %d MB' $((s / 1048576)) $((ZIP_BYTES / 1048576)); done ) &
    PROG=$!
    tail -c +$((have + 1)) "$ZIP" | "${SSH_NEW[@]}" "core@$HOST" "cat >> $REMOTE_ZIP" || true
    kill "$PROG" 2>/dev/null || true; wait "$PROG" 2>/dev/null || true
    echo
    [ "$(RSIZE)" -ge "$ZIP_BYTES" ] && break
    warn "The upload stopped. Retrying in 10 seconds (Ctrl+C to stop; run the script again later to resume)."
    sleep 10
done
info "Checking the upload..."
L=$(sha256 "$ZIP"); R=$("${SSH_NEW[@]}" "core@$HOST" "sha256sum $REMOTE_ZIP | cut -c1-64" </dev/null)
[ "$L" = "$R" ] || { "${SSH_NEW[@]}" "core@$HOST" "rm -f $REMOTE_ZIP" </dev/null; die "The uploaded zip is damaged and was removed. Run the script again to upload it again."; }
info "Upload complete and verified."
echo

# ---------------------------------------------------------------- the game server
bold "6. Starting the game server"
CODE=""
if [ "$EDITION" = private ]; then
    info "Get a setup code in your rVclient launcher: Server Status > My private servers > Set up a private server."
    while :; do ask "Setup code (like ABCDE-FGH23)" ""; [ -n "$REPLY" ] && break; done
    CODE="$REPLY"
fi
if [ -n "${RV_INSTALL_SH:-}" ]; then "${SSH_NEW[@]}" "core@$HOST" "cat > /tmp/install.sh" < "$RV_INSTALL_SH"
else "${SSH_NEW[@]}" "core@$HOST" "curl -fsSLo /tmp/install.sh $(printf %q "$INSTALL_SH_URL")" </dev/null; fi
# The public IP is left to install.sh, which asks an outside service (right behind NAT too).
ENVS="RV_UNATTENDED=1 RV_GAME_ZIP=$REMOTE_ZIP RV_DATA_DIR=/var/srv/rvserver RV_EDITION=$EDITION"
ENVS="$ENVS RV_MODES=$MODES RV_SLIM=$SLIM RV_KSM=$KSM RV_WEBUI=$WEBUI"
[ -n "$CONTACT" ] && ENVS="$ENVS RV_CONTACT=$(printf %q "$CONTACT")"
[ -n "$NAME" ] && ENVS="$ENVS RV_NAME=$(printf %q "$NAME")"
[ -n "$CODE" ] && ENVS="$ENVS RV_SETUP_CODE=$(printf %q "$CODE")"
"${SSH_NEW[@]}" -t "core@$HOST" "sudo env $ENVS bash /tmp/install.sh"

echo
bold "All done."
info "Log in to your server:  ssh core@$HOST"
info "Server menu:            sudo podman exec -it rvserver rv menu"
echo
