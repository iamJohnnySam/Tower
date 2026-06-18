from fastapi import FastAPI, HTTPException
from typing import Any
import tinytuya
import json
import os
import socket
import ipaddress
import concurrent.futures

DEVICES_FILE = os.path.join(os.path.dirname(__file__), "devices.json")
app = FastAPI()


def _load() -> list[dict]:
    if not os.path.exists(DEVICES_FILE):
        return []
    with open(DEVICES_FILE) as f:
        return json.load(f)


def _save(devices: list[dict]) -> None:
    with open(DEVICES_FILE, "w") as f:
        json.dump(devices, f, indent=2)


def _poll(dev: dict) -> dict[str, Any]:
    try:
        d = tinytuya.OutletDevice(
            dev_id=dev["id"],
            address=dev["ip"],
            local_key=dev["key"],
            version=str(dev.get("version", "3.3")),
        )
        d.set_socketTimeout(2)
        result = d.status()
        if isinstance(result, dict) and "dps" in result:
            return result["dps"]
        return {}
    except Exception:
        return {}


def _check_port(ip: str, port: int = 6668, timeout: float = 0.5) -> bool:
    try:
        with socket.create_connection((ip, port), timeout=timeout):
            return True
    except Exception:
        return False


def _probe_subnet(subnet: str) -> list[str]:
    """Return IPs in subnet that have Tuya port 6668 open."""
    try:
        net = ipaddress.ip_network(subnet, strict=False)
    except ValueError:
        return []
    hosts = list(net.hosts())
    found = []
    with concurrent.futures.ThreadPoolExecutor(max_workers=64) as ex:
        results = {ex.submit(_check_port, str(h)): str(h) for h in hosts}
        for fut in concurrent.futures.as_completed(results):
            if fut.result():
                found.append(results[fut])
    return sorted(found, key=lambda ip: ipaddress.ip_address(ip))


@app.get("/health")
def health():
    return {"ok": True}


@app.get("/devices")
def list_devices():
    return [
        {
            "id": dev["id"],
            "name": dev.get("name", dev["id"]),
            "ip": dev.get("ip", ""),
            "version": str(dev.get("version", "3.3")),
            "dps": _poll(dev),
        }
        for dev in _load()
    ]


@app.get("/devices/{device_id}/state")
def get_state(device_id: str):
    dev = next((d for d in _load() if d["id"] == device_id), None)
    if dev is None:
        raise HTTPException(status_code=404, detail="Device not found")
    return {"dps": _poll(dev)}


@app.post("/scan")
async def scan(body: dict):
    api_key    = body.get("api_key", "")
    api_secret = body.get("api_secret", "")
    region     = body.get("region", "us")
    subnet     = body.get("subnet", "")

    cloud_devices: list[dict] = []
    cloud_error: str | None = None

    # ── 1. Cloud lookup ─────────────────────────────────────────────────────────
    try:
        c = tinytuya.Cloud(apiRegion=region, apiKey=api_key, apiSecret=api_secret)
        raw = c.getdevices(verbose=True)
        if isinstance(raw, dict):
            if not raw.get("success", True):
                cloud_error = f"Tuya API error {raw.get('code', '')}: {raw.get('msg', 'Unknown error')}"
            else:
                cloud_devices = raw.get("result", []) or []
        else:
            cloud_devices = raw or []
    except Exception as e:
        cloud_error = str(e)

    # ── 2. Local UDP broadcast ──────────────────────────────────────────────────
    udp_map: dict[str, str] = {}
    try:
        local_scan = tinytuya.deviceScan(verbose=False, maxretry=3)
        udp_map = {info.get("gwId", ""): ip for ip, info in local_scan.items()}
    except Exception:
        pass

    # ── 3. If cloud has devices, merge and save ─────────────────────────────────
    if cloud_devices:
        merged = [
            {
                "id":      dev.get("id", ""),
                "name":    dev.get("name", dev.get("id", "")),
                "ip":      dev.get("ip", "") or udp_map.get(dev.get("id", ""), ""),
                "key":     dev.get("local_key", dev.get("key", "")),
                "version": str(dev.get("version", "3.3")),
            }
            for dev in cloud_devices
        ]
        _save(merged)
        return {"devices": merged, "cloud_error": None, "probe_ips": []}

    # ── 4. No cloud devices — fall back to port probe ───────────────────────────
    if not subnet:
        # Auto-detect subnet from default route interface
        try:
            s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
            s.connect(("8.8.8.8", 80))
            local_ip = s.getsockname()[0]
            s.close()
            subnet = str(ipaddress.ip_network(f"{local_ip}/24", strict=False))
        except Exception:
            subnet = "192.168.1.0/24"

    probe_ips = _probe_subnet(subnet)

    hint = (
        cloud_error
        or "Cloud API returned 0 devices. Ensure your Smart Life account is linked "
           "to this project at iot.tuya.com → your project → Cloud → Link Tuya App Account."
    )

    return {"devices": [], "cloud_error": hint, "probe_ips": probe_ips}


@app.put("/devices/{device_id}/credentials")
async def set_credentials(device_id: str, body: dict):
    """Update local key (and optionally IP) for a device, then auto-probe LAN to find IP."""
    key = body.get("key", "").strip()
    ip  = body.get("ip", "").strip()

    devices = _load()
    dev = next((d for d in devices if d["id"] == device_id), None)
    if dev is None:
        dev = {"id": device_id, "name": device_id, "ip": "", "key": "", "version": "3.3"}
        devices.append(dev)

    dev["key"] = key

    # If no IP provided or current IP is not on LAN, probe all known Tuya IPs
    if key and (not ip or not ip.startswith("192.168.")):
        try:
            s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
            s.connect(("8.8.8.8", 80))
            local_ip = s.getsockname()[0]
            s.close()
            subnet = str(ipaddress.ip_network(f"{local_ip}/24", strict=False))
        except Exception:
            subnet = "192.168.1.0/24"

        probe_ips = _probe_subnet(subnet)
        matched_ip = ""
        for probe_ip in probe_ips:
            try:
                d = tinytuya.OutletDevice(
                    dev_id=device_id,
                    address=probe_ip,
                    local_key=key,
                    version=str(dev.get("version", "3.3")),
                )
                d.set_socketTimeout(2)
                result = d.status()
                if isinstance(result, dict) and ("dps" in result or "Error" not in str(result)):
                    matched_ip = probe_ip
                    break
            except Exception:
                continue
        if matched_ip:
            dev["ip"] = matched_ip
        elif ip:
            dev["ip"] = ip
    elif ip:
        dev["ip"] = ip

    _save(devices)
    return {"id": device_id, "ip": dev["ip"], "key_set": bool(key)}


@app.post("/devices/{device_id}/command")
async def command(device_id: str, body: dict):
    dev = next((d for d in _load() if d["id"] == device_id), None)
    if dev is None:
        raise HTTPException(status_code=404, detail="Device not found")
    try:
        d = tinytuya.OutletDevice(
            dev_id=dev["id"],
            address=dev["ip"],
            local_key=dev["key"],
            version=str(dev.get("version", "3.3")),
        )
        d.set_socketTimeout(3)
        if "dps" in body:
            d.set_multiple_values(body["dps"])
        elif "ac" in body:
            ac = body["ac"]
            dps: dict = {}
            if "power" in ac:
                dps["1"] = ac["power"]
            if "mode" in ac:
                mode_map = {"cool": "cold", "heat": "hot", "fan": "wind", "dry": "wet", "auto": "auto"}
                dps["2"] = mode_map.get(ac["mode"], ac["mode"])
            if "temp" in ac:
                dps["3"] = int(ac["temp"])
            if "fan" in ac:
                dps["4"] = ac["fan"]
            if dps:
                d.set_multiple_values(dps)
        return {"ok": True}
    except Exception as e:
        raise HTTPException(status_code=500, detail=str(e))
