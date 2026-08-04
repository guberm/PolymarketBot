#!/usr/bin/env bash
set -Eeuo pipefail

NS="polymarket-bot"
HOST_IF="pmb-host"
NS_IF="pmb-net"
SUBNET="10.203.0.0/30"
RUN_DIR="/run/polymarket-bot-vpn"

log() { printf '[VPN] %s\n' "$*"; }
die() { printf '[VPN] ERROR: %s\n' "$*" >&2; exit 1; }
ns() { ip netns exec "$NS" "$@"; }

config_value() {
  python3 - "$CONFIG" "$1" <<'PY'
import json, sys
value = json.load(open(sys.argv[1], encoding="utf-8")).get(sys.argv[2], "")
print("" if value is None else value)
PY
}

linux_path() {
  case "$1" in
    /*) printf '%s\n' "$1" ;;
    [A-Za-z]:\\*|[A-Za-z]:/*) wslpath -u "$1" ;;
    *) die "Config path must be absolute: $1" ;;
  esac
}

cleanup() {
  set +e
  if ip netns list 2>/dev/null | grep -q "^${NS}\b"; then
    pids="$(ip netns pids "$NS" 2>/dev/null)"
    [ -z "$pids" ] || kill $pids 2>/dev/null
    sleep 1
    pids="$(ip netns pids "$NS" 2>/dev/null)"
    [ -z "$pids" ] || kill -9 $pids 2>/dev/null
    ip netns del "$NS" 2>/dev/null
  fi
  ip link del "$HOST_IF" 2>/dev/null
  iptables -t nat -D POSTROUTING -s "$SUBNET" -j MASQUERADE 2>/dev/null
  iptables -D FORWARD -i "$HOST_IF" -j ACCEPT 2>/dev/null
  iptables -D FORWARD -o "$HOST_IF" -m conntrack --ctstate ESTABLISHED,RELATED -j ACCEPT 2>/dev/null
  rm -rf "$RUN_DIR" "/etc/netns/$NS"
}

wireguard_has_placeholder() {
  grep -Eqi '^\s*PrivateKey\s*=\s*<[^>]+>' "$1"
}

prepare_wireguard_config() {
  local source="$1" target="$2" private="$3" public="$4" derived line replaced=0
  if [ -n "$private" ]; then
    derived="$(printf '%s' "$private" | wg pubkey 2>/dev/null)" || die 'WireGuard private key is invalid'
    [ -z "$public" ] || [ "$derived" = "$public" ] || die 'WireGuard public key does not match the private key'
    while IFS= read -r line || [ -n "$line" ]; do
      if [[ "$line" =~ ^[[:space:]]*PrivateKey[[:space:]]*= ]]; then
        printf 'PrivateKey = %s\n' "$private"
        replaced=1
      else
        printf '%s\n' "$line"
      fi
    done < "$source" > "$target"
    [ "$replaced" = 1 ] || die 'WireGuard config has no PrivateKey field'
  else
    wireguard_has_placeholder "$source" && die 'Paste the Surfshark WireGuard private key in Settings'
    cp "$source" "$target"
  fi
}

wireguard_dns_servers() {
  awk -F= 'tolower($1) ~ /^[[:space:]]*dns[[:space:]]*$/ {print $2}' "$1" |
    tr ',' '\n' | sed -E 's/^[[:space:]]+|[[:space:]]+$//g' | grep -E '^[0-9a-fA-F:.]+$' || true
}

vpn_ip_once() {
  VPN_IP="$(ns curl --fail --silent --max-time 8 https://api.ipify.org 2>/dev/null)"
}

wait_for_vpn_ip() {
  local attempts="${1:-6}" pause="${2:-2}" attempt
  for attempt in $(seq 1 "$attempts"); do
    if vpn_ip_once && [ -n "$VPN_IP" ]; then return 0; fi
    [ "$attempt" = "$attempts" ] || { log "Waiting for VPN connectivity ($attempt/$attempts)..."; sleep "$pause"; }
  done
  return 1
}

wireguard_handshake_ready() {
  local timestamp
  timestamp="$(ns wg show wg0 latest-handshakes | awk 'NR == 1 { print $2 }')"
  [ "${timestamp:-0}" -gt 0 ]
}

set_wireguard_endpoint() {
  ns wg set wg0 peer "$WG_PEER" endpoint "$1:$2"
}

trigger_wireguard_handshake() {
  ns curl --insecure --silent --max-time 4 https://1.1.1.1 >/dev/null 2>&1 || true
}

try_wireguard_endpoints() {
  local port="$1" endpoints="$2" endpoint
  while read -r endpoint; do
    [ -n "$endpoint" ] || continue
    log "Testing WireGuard endpoint $endpoint..."
    set_wireguard_endpoint "$endpoint" "$port"
    trigger_wireguard_handshake
    wireguard_handshake_ready && return 0
  done <<< "$endpoints"
  return 1
}

self_test() {
  local tmp
  tmp="$(mktemp)"
  printf '{"network_mode":"wireguard","openvpn_username":"service-user"}' > "$tmp"
  CONFIG="$tmp"
  [ "$(config_value network_mode)" = wireguard ]
  [ "$(config_value openvpn_username)" = service-user ]
  printf 'PrivateKey = <insert_your_private_key_here>\n' > "$tmp"
  wireguard_has_placeholder "$tmp"
  local private public prepared
  private="$(wg genkey)"
  public="$(printf '%s' "$private" | wg pubkey)"
  prepared="${tmp}.prepared"
  prepare_wireguard_config "$tmp" "$prepared" "$private" "$public"
  grep -Fq "PrivateKey = $private" "$prepared"
  printf '[Interface]\nDNS = 162.252.172.57, 149.154.159.92\n' > "$tmp"
  [ "$(wireguard_dns_servers "$tmp")" = $'162.252.172.57\n149.154.159.92' ]
  local probe_count=0
  vpn_ip_once() {
    probe_count=$((probe_count + 1))
    [ "$probe_count" -ge 3 ] || { VPN_IP=""; return 1; }
    VPN_IP="169.150.227.143"
  }
  VPN_IP=""
  wait_for_vpn_ip 3 0
  [ "$probe_count" = 3 ]
  [ "$VPN_IP" = 169.150.227.143 ]
  local tested_endpoints="" active_endpoint=""
  set_wireguard_endpoint() { active_endpoint="$1"; tested_endpoints="${tested_endpoints}${1} "; }
  trigger_wireguard_handshake() { :; }
  wireguard_handshake_ready() { [ "$active_endpoint" = 169.150.227.142 ]; }
  try_wireguard_endpoints 51820 $'169.150.227.140\n169.150.227.142\n169.150.227.147'
  [ "$tested_endpoints" = '169.150.227.140 169.150.227.142 ' ]
  rm -f "$prepared"
  rm -f "$tmp"
  printf 'vpn runner self-checks passed\n'
}

[ "${1:-}" = "--self-test" ] && { self_test; exit 0; }
[ "${1:-}" = "--stop" ] && { [ "$(id -u)" = 0 ] || die 'Root is required'; cleanup; exit 0; }

CONFIG="" DATA="" ROOT="" BOT_MODE="python"
BOT_ARGS=()
while [ "$#" -gt 0 ]; do
  case "$1" in
    --config) CONFIG="$2"; shift 2 ;;
    --data) DATA="$2"; shift 2 ;;
    --root) ROOT="$2"; shift 2 ;;
    --mode) BOT_MODE="$2"; shift 2 ;;
    --verbose|--console) BOT_ARGS+=("$1"); shift ;;
    *) die "Unknown argument: $1" ;;
  esac
done

[ "$(id -u)" = 0 ] || die 'Root is required'
[ -f "$CONFIG" ] || die "Bot config not found: $CONFIG"
[ -d "$DATA" ] || mkdir -p "$DATA"
[ -d "$ROOT" ] || die "Bot root not found: $ROOT"

VPN_MODE="$(config_value network_mode)"
case "$VPN_MODE" in wireguard|openvpn) ;; *) die "Expected WireGuard or OpenVPN mode, got: ${VPN_MODE:-empty}" ;; esac
VPN_CONFIG="$(linux_path "$(config_value vpn_config_path)")"
[ -f "$VPN_CONFIG" ] || die "VPN config not found: $VPN_CONFIG"

if ip netns list 2>/dev/null | grep -q "^${NS}\b"; then
  die 'The isolated bot network is already running. Stop the bot first.'
fi

packages=(iproute2 iptables wireguard-tools openvpn curl ca-certificates)
commands=(ip iptables wg wg-quick openvpn curl)
if [ "$BOT_MODE" = dotnet ]; then
  packages+=(dotnet-sdk-8.0)
  commands+=(dotnet)
else
  packages+=(python3 python3-venv python3-pip)
  commands+=(python3)
fi
missing=0
for command in "${commands[@]}"; do command -v "$command" >/dev/null || missing=1; done
if [ "$missing" = 1 ]; then
  log 'Installing required Ubuntu components (first start only)...'
  export DEBIAN_FRONTEND=noninteractive
  apt-get update
  apt-get install -y --no-install-recommends "${packages[@]}"
fi

if [ "$BOT_MODE" = dotnet ]; then
  log 'Building the Linux .NET bot...'
  dotnet build "$ROOT/dotnet/PolymarketBot/PolymarketBot.csproj" -c Release --nologo
  BOT=(dotnet "$ROOT/dotnet/PolymarketBot/bin/Release/net8.0/PolymarketBot.dll" "${BOT_ARGS[@]}")
  BOT_CWD="$ROOT/dotnet/PolymarketBot"
else
  VENV="/var/lib/polymarket-bot/venv"
  if [ ! -x "$VENV/bin/python" ]; then python3 -m venv "$VENV"; fi
  if ! "$VENV/bin/python" -c 'import anthropic, py_clob_client, requests' 2>/dev/null; then
    log 'Installing Python bot dependencies (first start only)...'
    "$VENV/bin/pip" install -r "$ROOT/python/requirements.txt"
  fi
  BOT=("$VENV/bin/python" "$ROOT/python/main.py" "${BOT_ARGS[@]}")
  BOT_CWD="$ROOT/python"
fi

trap cleanup EXIT
trap 'exit 143' INT TERM
mkdir -p "$RUN_DIR" "/etc/netns/$NS"
chmod 700 "$RUN_DIR"

ip netns add "$NS"
ip link add "$HOST_IF" type veth peer name "$NS_IF"
ip link set "$NS_IF" netns "$NS"
ip addr add 10.203.0.1/30 dev "$HOST_IF"
ip link set "$HOST_IF" up
ns ip addr add 10.203.0.2/30 dev "$NS_IF"
ns ip link set lo up
ns ip link set "$NS_IF" up
ns ip route add default via 10.203.0.1
sysctl -q -w net.ipv4.ip_forward=1
iptables -t nat -A POSTROUTING -s "$SUBNET" -j MASQUERADE
iptables -A FORWARD -i "$HOST_IF" -j ACCEPT
iptables -A FORWARD -o "$HOST_IF" -m conntrack --ctstate ESTABLISHED,RELATED -j ACCEPT

grep '^nameserver ' /etc/resolv.conf > "/etc/netns/$NS/resolv.conf" || printf 'nameserver 1.1.1.1\n' > "/etc/netns/$NS/resolv.conf"
ns iptables -P OUTPUT DROP
ns iptables -P INPUT DROP
ns iptables -A OUTPUT -o lo -j ACCEPT
ns iptables -A INPUT -i lo -j ACCEPT
ns iptables -A OUTPUT -m conntrack --ctstate ESTABLISHED,RELATED -j ACCEPT
ns iptables -A INPUT -m conntrack --ctstate ESTABLISHED,RELATED -j ACCEPT
ns iptables -A OUTPUT -o "$NS_IF" -p udp --dport 53 -j ACCEPT
ns iptables -A OUTPUT -o "$NS_IF" -p tcp --dport 53 -j ACCEPT
ns iptables -A OUTPUT -o wg0 -j ACCEPT
ns iptables -A OUTPUT -o 'tun+' -j ACCEPT

allow_endpoint() {
  local host="$1" found=0 ipaddr
  RESOLVED_ENDPOINT_IPS=""
  while read -r ipaddr; do
    [ -n "$ipaddr" ] || continue
    ns iptables -A OUTPUT -o "$NS_IF" -d "$ipaddr" -j ACCEPT
    RESOLVED_ENDPOINT_IPS="${RESOLVED_ENDPOINT_IPS}${ipaddr}"$'\n'
    found=1
  done < <(getent ahostsv4 "$host" | awk '{print $1}' | sort -u)
  [ "$found" = 1 ] || die "Cannot resolve VPN endpoint: $host"
}

configure_smtp_split_route() {
  local SMTP_ENABLED SMTP_HOST SMTP_IP
  SMTP_ENABLED="$(config_value email_enabled | tr '[:upper:]' '[:lower:]')"
  [ "$SMTP_ENABLED" = true ] || return 0
  SMTP_HOST="$(config_value email_smtp_host)"
  [[ "$SMTP_HOST" =~ ^[A-Za-z0-9.-]+$ ]] || { log 'SMTP direct route skipped: invalid host'; return 0; }
  SMTP_IP="$(ns getent ahostsv4 "$SMTP_HOST" | awk 'NR == 1 { print $1 }')"
  [ -n "$SMTP_IP" ] || { log "SMTP direct route skipped: cannot resolve $SMTP_HOST"; return 0; }

  cp /etc/hosts "/etc/netns/$NS/hosts"
  printf '%s %s\n' "$SMTP_IP" "$SMTP_HOST" >> "/etc/netns/$NS/hosts"
  ns ip rule add priority 100 to "$SMTP_IP/32" lookup main
  ns iptables -A OUTPUT -o "$NS_IF" -p tcp -d "$SMTP_IP" -m multiport --dports 465,587 -j ACCEPT
  log "SMTP direct route active for $SMTP_HOST on ports 465/587."
}

VPN_DNS=""
if [ "$VPN_MODE" = wireguard ]; then
  grep -Eqi '^\s*(Pre|Post)(Up|Down)\s*=' "$VPN_CONFIG" && die 'WireGuard hook commands are not allowed'
  WG_SOURCE="$RUN_DIR/wg-source.conf"
  WG_CONFIG="$RUN_DIR/wg0.conf"
  VPN_DNS="$(wireguard_dns_servers "$VPN_CONFIG")"
  tr -d '\r' < "$VPN_CONFIG" | sed -E '/^[[:space:]]*DNS[[:space:]]*=/Id' > "$WG_SOURCE"
  prepare_wireguard_config "$WG_SOURCE" "$WG_CONFIG" "$(config_value wireguard_private_key)" "$(config_value wireguard_public_key)"
  chmod 600 "$WG_CONFIG"
  endpoint="$(awk -F= 'tolower($1) ~ /^[[:space:]]*endpoint[[:space:]]*$/ {gsub(/[[:space:]]/, "", $2); print $2; exit}' "$WG_CONFIG")"
  endpoint_host="${endpoint%:*}"
  endpoint_port="${endpoint##*:}"
  [ -n "$endpoint_host" ] || die 'WireGuard Endpoint is missing'
  allow_endpoint "$endpoint_host"
  WG_ENDPOINT_IPS="$RESOLVED_ENDPOINT_IPS"
  log 'Connecting WireGuard inside the isolated bot network...'
  ns wg-quick up "$WG_CONFIG"
  ns wg show wg0 >/dev/null || die 'WireGuard interface did not start'
  WG_PEER="$(ns wg show wg0 peers | head -n 1)"
  try_wireguard_endpoints "$endpoint_port" "$WG_ENDPOINT_IPS" || die 'No WireGuard endpoint completed a handshake'
else
  OPENVPN_CONFIG="$RUN_DIR/client.ovpn"
  tr -d '\r' < "$VPN_CONFIG" > "$OPENVPN_CONFIG"
  chmod 600 "$OPENVPN_CONFIG"
  while read -r host; do allow_endpoint "$host"; done < <(awk 'tolower($1)=="remote" {print $2}' "$OPENVPN_CONFIG" | sort -u)
  USERNAME="$(config_value openvpn_username)"
  PASSWORD="$(config_value openvpn_password)"
  [ -n "$USERNAME" ] && [ -n "$PASSWORD" ] || die 'OpenVPN service username and password are required'
  [[ "$USERNAME$PASSWORD" != *$'\n'* && "$USERNAME$PASSWORD" != *$'\r'* ]] || die 'OpenVPN credentials cannot contain newlines'
  printf '%s\n%s\n' "$USERNAME" "$PASSWORD" > "$RUN_DIR/openvpn.auth"
  chmod 600 "$RUN_DIR/openvpn.auth"
  log 'Connecting OpenVPN inside the isolated bot network...'
  ns openvpn --config "$OPENVPN_CONFIG" --auth-user-pass "$RUN_DIR/openvpn.auth" --auth-nocache \
    --writepid "$RUN_DIR/openvpn.pid" --log "$RUN_DIR/openvpn.log" --daemon
  ready=0
  for _ in $(seq 1 45); do
    grep -q 'Initialization Sequence Completed' "$RUN_DIR/openvpn.log" 2>/dev/null && { ready=1; break; }
    grep -Eq 'AUTH_FAILED|Exiting due to fatal error' "$RUN_DIR/openvpn.log" 2>/dev/null && break
    sleep 1
  done
  if [ "$ready" != 1 ]; then tail -n 20 "$RUN_DIR/openvpn.log" >&2 || true; die 'OpenVPN connection failed'; fi
fi

: > "/etc/netns/$NS/resolv.conf"
if [ -n "$VPN_DNS" ]; then
  while read -r dns; do printf 'nameserver %s\n' "$dns"; done <<< "$VPN_DNS" > "/etc/netns/$NS/resolv.conf"
else
  printf 'nameserver 1.1.1.1\nnameserver 9.9.9.9\n' > "/etc/netns/$NS/resolv.conf"
fi
ns iptables -D OUTPUT -o "$NS_IF" -p udp --dport 53 -j ACCEPT
ns iptables -D OUTPUT -o "$NS_IF" -p tcp --dport 53 -j ACCEPT

VPN_IP=""
wait_for_vpn_ip || die 'VPN tunnel has no Internet access after retries; bot was not started'
log "Tunnel ready. Bot external IP: $VPN_IP"
log 'Kill switch active: direct traffic from the bot namespace is blocked.'
configure_smtp_split_route

export CONFIG_FILE="$CONFIG" DATA_DIR="$DATA"
cd "$BOT_CWD"
ns env CONFIG_FILE="$CONFIG" DATA_DIR="$DATA" "${BOT[@]}" &
BOT_PID=$!
wait "$BOT_PID"
