#!/usr/bin/env bash
# Tower deploy: stop -> publish -> start (per project deploy workflow).
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
TARGET="/home/atom/Tower"

echo "==> Stopping tower service (if running)"
sudo systemctl stop tower 2>/dev/null || true

echo "==> Publishing (Release) to $TARGET"
dotnet publish "$REPO/src/Tower/Tower.csproj" -c Release -o "$TARGET"

echo "==> Copying maintenance script"
cp "$REPO/do_maintenance.sh" "$TARGET/do_maintenance.sh"
chmod +x "$TARGET/do_maintenance.sh"

echo "==> Installing sudoers (validate first, then install)"
if sudo visudo -c -f "$REPO/tower.sudoers" >/dev/null; then
    sudo install -m 0440 -o root -g root "$REPO/tower.sudoers" /etc/sudoers.d/tower
    echo "    /etc/sudoers.d/tower updated"
else
    echo "    ERROR: tower.sudoers failed validation — not installing" >&2
    exit 1
fi

echo "==> Starting tower service"
sudo systemctl start tower
sleep 2
sudo systemctl status tower --no-pager --lines=5 || true
echo "==> Done. Tower serving on http://0.0.0.0:8888"
