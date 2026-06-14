#!/bin/bash
# Maintenance helper — runs apt update + upgrade non-interactively.
# Called via sudo from server-monitor. Do not modify paths without updating sudoers.
set -e
export DEBIAN_FRONTEND=noninteractive
export APT_LISTCHANGES_FRONTEND=none

echo "[$(date '+%Y-%m-%d %H:%M:%S')] apt-get update"
apt-get update -q 2>&1

echo "[$(date '+%Y-%m-%d %H:%M:%S')] apt-get upgrade"
apt-get upgrade -y -q \
  -o Dpkg::Options::="--force-confdef" \
  -o Dpkg::Options::="--force-confold" \
  2>&1

echo "[$(date '+%Y-%m-%d %H:%M:%S')] apt upgrade completed"
