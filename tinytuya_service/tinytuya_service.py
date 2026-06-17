from fastapi import FastAPI, HTTPException
from typing import Any
import tinytuya
import json
import os

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
    api_key = body.get("api_key", "")
    api_secret = body.get("api_secret", "")
    region = body.get("region", "us")
    try:
        c = tinytuya.Cloud(apiRegion=region, apiKey=api_key, apiSecret=api_secret)
        cloud_devices = c.getdevices()
        # Scan local network for IPs via UDP broadcast (takes ~5 s)
        local_scan = tinytuya.deviceScan(verbose=False, maxretry=3)
        ip_map = {info.get("gwId", ""): ip for ip, info in local_scan.items()}
        merged = [
            {
                "id": dev.get("id", ""),
                "name": dev.get("name", dev.get("id", "")),
                "ip": dev.get("ip", "") or ip_map.get(dev.get("id", ""), ""),
                "key": dev.get("key", ""),
                "version": str(dev.get("version", "3.3")),
            }
            for dev in cloud_devices
        ]
        _save(merged)
        return merged
    except Exception as e:
        raise HTTPException(status_code=500, detail=str(e))


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
