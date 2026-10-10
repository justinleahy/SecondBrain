#!/usr/bin/env bash
# Disposable supported-profile qualification. Requires root; never uses live unit names or roots.
set -euo pipefail
if [ "$(id -u)" != 0 ]; then
    printf '%s\n' 'Blocked: root is required to create isolated systemd units and numeric-UID fixture roots.' >&2
    exit 2
fi
if [ "$#" -lt 2 ]; then
    printf 'Usage: %s MERGED_PUBLISH_DIRECTORY WORKFLOW_PYTHON [PORT]\n' "$0" >&2
    exit 2
fi
qualification_binary=$(realpath "$1")
qualification_harness=$(realpath "$2")
qualification_port=${3:-17171}
[[ "$qualification_port" =~ ^[0-9]+$ ]] && (( qualification_port > 1024 && qualification_port < 65536 ))
qualification_repo=$(cd "$(dirname "$0")/../.." && pwd)
qualification_config=$(realpath "${SECONDBRAIN_QUALIFICATION_CONFIG_TEMPLATE:-$qualification_repo/deploy/tests/config.m1-systemd.yaml}")
for qualification_uid in 1654 1655 1656; do getent passwd "$qualification_uid" >/dev/null; done
getent group 1654 >/dev/null
for qualification_file in brain SecondBrain.Server SecondBrain.Extractor SecondBrain.Server.staticwebassets.endpoints.json; do test -f "$qualification_binary/$qualification_file"; done
test -d "$qualification_binary/wwwroot"
test -f "$qualification_harness"
qualification_root=$(mktemp -d /srv/sb-m1-qualification.XXXXXXXX)
qualification_name=$(basename "$qualification_root")
qualification_runtime=/run/$qualification_name
qualification_install=/opt/$qualification_name
qualification_unit=/run/systemd/system/$qualification_name
qualification_password=$(python3 -c 'import secrets; print(secrets.token_urlsafe(32))')
qualification_cleanup() {
    systemctl stop "$qualification_name.service" "$qualification_name-extractor.socket" "$qualification_name-extractor.service" 2>/dev/null || true
    rm -f "$qualification_unit.service" "$qualification_unit-extractor.socket" "$qualification_unit-extractor.service"
    systemctl daemon-reload
    rm -rf -- "$qualification_runtime" "$qualification_install" "$qualification_root"
}
trap qualification_cleanup EXIT
mkdir -p "$qualification_root"/{data,incoming,config,secrets,results} "$qualification_runtime" "$qualification_install"
chmod 0755 "$qualification_root" "$qualification_install"
chown 1654:1654 "$qualification_root/data" "$qualification_root/config" "$qualification_root/results" "$qualification_root/secrets"
chmod 0700 "$qualification_root/data" "$qualification_root/results" "$qualification_root/secrets"
chown 1656:1654 "$qualification_root/incoming"; chmod 0750 "$qualification_root/incoming"
mkdir -p "$qualification_root/incoming/inbox/.claimed"
chown 1654:1654 "$qualification_root/incoming/inbox" "$qualification_root/incoming/inbox/.claimed"
chmod 1770 "$qualification_root/incoming/inbox"
chmod 0700 "$qualification_root/incoming/inbox/.claimed"
chown 1655:1654 "$qualification_runtime"; chmod 0750 "$qualification_runtime"
cp -a "$qualification_binary/." "$qualification_install/"
chown -R 0:0 "$qualification_install"
chmod -R a+rX "$qualification_install"
if command -v selinuxenabled >/dev/null && selinuxenabled; then
    # Temporary labels on disposable copies only; no persistent fcontext or live install changes.
    chcon -R -t lib_t "$qualification_install"
    chcon -t bin_t "$qualification_install/brain" "$qualification_install/SecondBrain.Server" "$qualification_install/SecondBrain.Extractor"
fi
python3 - "$qualification_repo" "$qualification_root" "$qualification_runtime" "$qualification_install" "$qualification_name" "$qualification_port" "$qualification_config" <<'PY'
import pathlib, sys
repo, root, runtime, binary, name, port, config_template=sys.argv[1:]
replacements={'/srv/secondbrain-incoming':root+'/incoming','/srv/secondbrain':root+'/data','/etc/secondbrain/secrets':root+'/secrets','/etc/secondbrain/config.yaml':root+'/config/config.yaml','/etc/secondbrain':root+'/config','/run/secondbrain':runtime,'/usr/local/lib/secondbrain':binary,'secondbrain-extractor':name+'-extractor'}
for src,suffix in [('secondbrain.service','.service'),('secondbrain-extractor.service','-extractor.service'),('secondbrain-extractor.socket','-extractor.socket')]:
    text=(pathlib.Path(repo)/'deploy/systemd'/src).read_text()
    for old,new in replacements.items(): text=text.replace(old,new)
    text=text.replace('User=secondbrain-extract','User=1655').replace('User=secondbrain','User=1654').replace('Group=secondbrain','Group=1654').replace('SocketUser=secondbrain-extract','SocketUser=1655').replace('SocketGroup=secondbrain','SocketGroup=1654')
    if src=='secondbrain-extractor.service': text=text.replace('InaccessiblePaths='+root+'/data '+root+'/incoming '+root+'/config', 'InaccessiblePaths='+root+'/data '+root+'/incoming '+root+'/config '+root+'/secrets')
    if src=='secondbrain.service': text=text.replace('Environment=DOTNET_EnableDiagnostics=0', 'Environment=DOTNET_EnableDiagnostics=0\nEnvironment=SECONDBRAIN_SECRETS_DIRECTORY='+root+'/secrets')
    pathlib.Path('/run/systemd/system/'+name+suffix).write_text(text)
config=pathlib.Path(config_template).read_text()
for old,new in replacements.items(): config=config.replace(old,new)
config=config.replace('7171',port)
if '\nextractor:' not in config: config+='\nextractor:\n  socket_path: '+runtime+'/extractor.sock\n'
pathlib.Path(root+'/config/config.yaml').write_text(config)
PY
chown 1654:1654 "$qualification_root/config/config.yaml"; chmod 0600 "$qualification_root/config/config.yaml"
setpriv --reuid 1654 --regid 1654 --clear-groups env SECONDBRAIN_CONFIG="$qualification_root/config/config.yaml" SECONDBRAIN_SECRETS_DIRECTORY="$qualification_root/secrets" SECONDBRAIN_BOOTSTRAP_PASSWORD="$qualification_password" "$qualification_install/brain" init --json > "$qualification_root/results/init.json"
chmod 0600 "$qualification_root/results/init.json"
systemctl daemon-reload
systemd-analyze verify "$qualification_unit.service" "$qualification_unit-extractor.socket" "$qualification_unit-extractor.service"
systemctl start "$qualification_name-extractor.socket" "$qualification_name.service"
qualification_origin=http://127.0.0.1:$qualification_port
for qualification_attempt in $(seq 1 60); do
    if curl --fail --silent "$qualification_origin/health" > "$qualification_root/results/health.json"; then break; fi
    sleep 1
done
curl --fail --silent "$qualification_origin/health" > "$qualification_root/results/health.json"
curl --fail --silent --show-error --retry 30 --retry-all-errors --retry-delay 1 --max-time 10 "$qualification_origin/ready" > "$qualification_root/results/ready.json"
# The operator's workflow harness receives disposable paths and credentials only through environment.
env SECONDBRAIN_QUALIFICATION_ORIGIN="$qualification_origin" SECONDBRAIN_QUALIFICATION_ROOT="$qualification_root" SECONDBRAIN_QUALIFICATION_INCOMING="$qualification_root/incoming" SECONDBRAIN_QUALIFICATION_PASSWORD="$qualification_password" SECONDBRAIN_QUALIFICATION_UNIT="$qualification_name.service" python3 "$qualification_harness"
systemctl show "$qualification_name.service" "$qualification_name-extractor.service" -p User -p Group -p MemoryMax -p TasksMax -p PrivateNetwork -p ProtectSystem -p NoNewPrivileges -p CPUUsageNSec
printf '%s\n' 'Qualification finished; disposable units and roots will now be removed.'
