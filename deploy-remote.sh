#!/bin/bash
# The part of a deploy that runs on the VPS (used by deploy.sh and the Windows deploy tool), as root:
#   deploy-remote.sh check                                   checks the VPS and prints KEY=value lines
#   deploy-remote.sh install <ssh key base64> <reinstall url> <coreos version>
#       also writes the Fedora CoreOS configuration and prepares the reinstall; "READY=1" at the end
#       means a reboot starts it. Problems come back as a FAIL=reason line.
set -euo pipefail
MODE=$1; KEY_B64=${2:-}; REINSTALL_URL=${3:-}; FCOS_VERSION=${4:-}
fail() { echo "FAIL=$*"; exit 0; }

[ "$(id -u)" = 0 ] || fail "needs root"
[ "$(uname -m)" = x86_64 ] || fail "this VPS is $(uname -m); the game server needs x86_64"
VIRT=$(systemd-detect-virt -c 2>/dev/null || true)
[ -z "$VIRT" ] || [ "$VIRT" = none ] || fail "this VPS is a container ($VIRT); it cannot change its operating system. A KVM VPS is needed."
. /etc/os-release 2>/dev/null || true

MEM_MB=$(awk '/^MemTotal:/ {print int($2/1024)}' /proc/meminfo)
CPUS=$(nproc)
DISK_GB=$(lsblk -dbno SIZE,TYPE | awk '$2=="disk" {if ($1>m) m=$1} END {print int(m/1e9)}')

IFACE=$(ip -4 route show default | awk '{print $5; exit}')
[ -n "$IFACE" ] || fail "no IPv4 default route"
MAC=$(cat /sys/class/net/"$IFACE"/address)
V4LINE=$(ip -4 -o addr show dev "$IFACE" scope global | head -1)
V4=$(echo "$V4LINE" | awk '{print $4}')
V4MODE=static; echo "$V4LINE" | grep -q dynamic && V4MODE=dhcp
GW4=$(ip -4 route show default dev "$IFACE" | awk '{print $3; exit}')
V6=""; GW6=""; V6MODE=auto
V6LINE=$(ip -6 -o addr show dev "$IFACE" scope global | grep -v -E "temporary|deprecated" | head -1 || true)
if [ -n "$V6LINE" ]; then
    V6=$(echo "$V6LINE" | awk '{print $4}')
    echo "$V6LINE" | grep -q -E "dynamic|mngtmpaddr" || V6MODE=static
    GW6=$(ip -6 route show default | awk '{print $3; exit}')
fi
DNS=$( (resolvectl dns "$IFACE" 2>/dev/null | cut -d: -f2-; resolvectl dns 2>/dev/null | cut -d: -f2-; grep -E '^nameserver' /etc/resolv.conf | awk '{print $2}') \
      | tr ' ' '\n' | grep -v -E '^$|^127\.|^::1$|%' | awk '!s[$0]++' | head -4 | tr '\n' ' ')
[ -n "$DNS" ] || DNS="1.1.1.1 8.8.8.8"
TZ=$(timedatectl show -p Timezone --value 2>/dev/null || cat /etc/timezone 2>/dev/null || echo UTC)

echo "OS=${PRETTY_NAME:-unknown}"; echo "MEM_MB=$MEM_MB"; echo "CPUS=$CPUS"; echo "DISK_GB=$DISK_GB"
echo "IFACE=$IFACE"; echo "MAC=$MAC"; echo "V4=$V4"; echo "V4MODE=$V4MODE"; echo "GW4=$GW4"
echo "V6=$V6"; echo "V6MODE=$V6MODE"; echo "GW6=$GW6"; echo "DNS=$DNS"; echo "TZ=$TZ"
[ "$MODE" = install ] || exit 0

# ------------------------------------------------------------ Fedora CoreOS configuration (Ignition)
KEY=$(echo "$KEY_B64" | base64 -d)
v4dns=""; v6dns=""
for d in $DNS; do case "$d" in *:*) v6dns="$v6dns$d;" ;; *) v4dns="$v4dns$d;" ;; esac; done
[ -n "$v4dns" ] || v4dns="1.1.1.1;8.8.8.8;"
{
    echo "[connection]"; echo "id=wired"; echo "type=ethernet"; echo "autoconnect=true"; echo "autoconnect-retries=0"
    echo; echo "[ethernet]"; echo "mac-address=$MAC"
    echo; echo "[ipv4]"
    if [ "$V4MODE" = dhcp ]; then echo "method=auto"
    else
        # Gateway first as an on-link host route: works whether or not it lies inside the subnet.
        echo "method=manual"; echo "address1=$V4"; echo "route1=$GW4/32"; echo "route2=0.0.0.0/0,$GW4"
    fi
    echo "dns=$v4dns"
    echo; echo "[ipv6]"
    if [ -n "$V6" ] && [ "$V6MODE" = static ] && [ -n "$GW6" ]; then
        echo "method=manual"; echo "address1=$V6"
        case "$GW6" in fe80:*) echo "route1=::/0,$GW6" ;; *) echo "route1=$GW6/128"; echo "route2=::/0,$GW6" ;; esac
    else echo "method=auto"; fi
    [ -n "$v6dns" ] && echo "dns=$v6dns"
    echo "may-fail=true"
} > /root/rv-wired.nmconnection

# Network buffers sized to RAM: tcp_mem (4 KB pages) tops out at 1/8 of RAM.
HIGH=$((MEM_MB * 256 / 8)); MID=$((HIGH / 2)); LOW=$((HIGH / 4))
cat > /root/rv-network.conf <<EOF
# Network tuning for a Rumbleverse server ($MEM_MB MB RAM). bbr needs the tcp_bbr module (modules-load.d).
net.core.default_qdisc              = fq
net.ipv4.tcp_congestion_control     = bbr
net.core.rmem_max                   = 33554432
net.core.wmem_max                   = 33554432
net.core.rmem_default               = 262144
net.core.wmem_default               = 262144
net.core.netdev_max_backlog         = 16384
net.core.somaxconn                  = 8192
net.ipv4.tcp_mem                    = $LOW $MID $HIGH
net.ipv4.tcp_rmem                   = 4096 131072 33554432
net.ipv4.tcp_wmem                   = 4096 65536 33554432
net.ipv4.udp_rmem_min               = 8192
net.ipv4.udp_wmem_min               = 8192
net.ipv4.tcp_slow_start_after_idle  = 0
net.ipv4.tcp_mtu_probing            = 1
EOF
cat > /root/rv-memory.conf <<'EOF'
# Rumbleverse server with zram: keep the game's files cached, move rarely used memory to zram,
# and free memory in the background before the game has to wait for it.
vm.swappiness = 150
vm.watermark_scale_factor = 200
vm.page-cluster = 0
EOF
printf '[zram0]\nzram-size = ram\ncompression-algorithm = zstd\n' > /root/rv-zram.conf
printf 'tcp_bbr\nntsync\n' > /root/rv-modules.conf
echo rvserver > /root/rv-hostname

b64() { base64 -w0 < "$1"; }
file() {   # file <path> <octal mode> <local file>
    printf '{"path":"%s","mode":%d,"overwrite":true,"contents":{"source":"data:;base64,%s"}}' "$1" "$((8#$2))" "$(b64 "$3")"
}
{
    printf '{"ignition":{"version":"3.4.0"},'
    printf '"passwd":{"users":[{"name":"core","sshAuthorizedKeys":["%s"]}]},' "$KEY"
    printf '"storage":{"files":['
    file /etc/hostname 644 /root/rv-hostname; printf ','
    file /etc/NetworkManager/system-connections/wired.nmconnection 600 /root/rv-wired.nmconnection; printf ','
    file /etc/systemd/zram-generator.conf 644 /root/rv-zram.conf; printf ','
    file /etc/modules-load.d/rvserver.conf 644 /root/rv-modules.conf; printf ','
    file /etc/sysctl.d/90-rvserver-network.conf 644 /root/rv-network.conf; printf ','
    file /etc/sysctl.d/95-rvserver-memory.conf 644 /root/rv-memory.conf
    printf '],"links":[{"path":"/etc/localtime","target":"../usr/share/zoneinfo/%s","overwrite":true}]}}' "$TZ"
} > /root/rvserver.ign

# ------------------------------------------------------------ reinstall: write CoreOS and the config, reboot
cd /root
if command -v curl >/dev/null 2>&1; then curl -fsSLo reinstall.sh "$REINSTALL_URL/reinstall.sh"
else wget -qO reinstall.sh "$REINSTALL_URL/reinstall.sh"; fi
[ -s reinstall.sh ] || fail "could not download the reinstall script"
sed -i "s|^confhome=.*|confhome=$REINSTALL_URL|" reinstall.sh
IMG="https://builds.coreos.fedoraproject.org/prod/streams/stable/builds/$FCOS_VERSION/x86_64/fedora-coreos-$FCOS_VERSION-metal.x86_64.raw.xz"
bash reinstall.sh dd --img "$IMG" --username root --ssh-key "$KEY" --ignition /root/rvserver.ign > /root/rv-reinstall.log 2>&1 \
    || { tail -20 /root/rv-reinstall.log; fail "the reinstall script stopped with an error (log above)"; }
echo "READY=1"
