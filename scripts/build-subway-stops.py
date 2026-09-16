#!/usr/bin/env python3
"""Build subway station points (parent stops + routes) from MTA static GTFS."""
from __future__ import annotations

import csv
import json
import zipfile
from collections import defaultdict
from pathlib import Path

ZIP_PATH = Path("/tmp/mta-static.zip")
OUT_PATH = Path(__file__).resolve().parents[1] / "wwwroot" / "data" / "subway-stops.geojson"

SUBWAY_ROUTES = frozenset(
    list("1234567")
    + list("ACE")
    + list("BDFM")
    + list("G")
    + list("JZ")
    + list("L")
    + list("NQRW")
    + list("S")
)


def main() -> None:
    with zipfile.ZipFile(ZIP_PATH) as zf:
        trip_route: dict[str, str] = {}
        with zf.open("trips.txt") as f:
            for row in csv.DictReader(line.decode("utf-8") for line in f):
                rid = row["route_id"].strip().upper()
                if rid in SUBWAY_ROUTES:
                    trip_route[row["trip_id"]] = rid

        stop_routes: dict[str, set[str]] = defaultdict(set)
        with zf.open("stop_times.txt") as f:
            for row in csv.DictReader(line.decode("utf-8") for line in f):
                trip_id = row["trip_id"]
                route = trip_route.get(trip_id)
                if not route:
                    continue
                stop_routes[row["stop_id"].strip()].add(route)

        stops_meta: dict[str, dict] = {}
        with zf.open("stops.txt") as f:
            for row in csv.DictReader(line.decode("utf-8") for line in f):
                sid = row["stop_id"].strip()
                loc_type = (row.get("location_type") or "").strip()
                parent = (row.get("parent_station") or "").strip()
                stops_meta[sid] = {
                    "name": row["stop_name"].strip(),
                    "lat": float(row["stop_lat"]),
                    "lon": float(row["stop_lon"]),
                    "location_type": loc_type,
                    "parent": parent,
                }

    parent_routes: dict[str, set[str]] = defaultdict(set)
    for stop_id, routes in stop_routes.items():
        meta = stops_meta.get(stop_id)
        if not meta:
            continue
        parent_id = meta["parent"] if meta["parent"] else stop_id
        parent_routes[parent_id].update(routes)

    features = []
    seen: set[str] = set()
    for stop_id, meta in stops_meta.items():
        is_parent = meta["location_type"] == "1" or (not meta["parent"] and stop_id in parent_routes)
        if meta["location_type"] == "0" and meta["parent"]:
            continue
        if not is_parent and meta["location_type"] != "1":
            if meta["parent"]:
                continue
        routes = sorted(parent_routes.get(stop_id, set()) & SUBWAY_ROUTES)
        if not routes:
            continue
        if stop_id in seen:
            continue
        seen.add(stop_id)
        features.append(
            {
                "type": "Feature",
                "properties": {
                    "stop_id": stop_id,
                    "name": meta["name"],
                    "routes": routes,
                },
                "geometry": {"type": "Point", "coordinates": [meta["lon"], meta["lat"]]},
            }
        )

    features.sort(key=lambda f: f["properties"]["name"])
    OUT_PATH.parent.mkdir(parents=True, exist_ok=True)
    OUT_PATH.write_text(
        json.dumps({"type": "FeatureCollection", "features": features}),
        encoding="utf-8",
    )
    print(f"Wrote {len(features)} stations to {OUT_PATH} ({OUT_PATH.stat().st_size // 1024} KB)")


if __name__ == "__main__":
    main()
