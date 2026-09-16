#!/usr/bin/env python3
"""Build simplified subway track GeoJSON from MTA static GTFS (shapes + trips)."""
from __future__ import annotations

import csv
import json
import math
import zipfile
from collections import defaultdict
from pathlib import Path

ZIP_PATH = Path("/tmp/mta-static.zip")
OUT_PATH = Path(__file__).resolve().parents[1] / "wwwroot" / "data" / "subway-tracks.geojson"

ROUTE_COLORS = {
    "1": "#EE352E", "2": "#EE352E", "3": "#EE352E",
    "4": "#00933C", "5": "#00933C", "6": "#00933C",
    "7": "#B933AD",
    "A": "#0039A6", "C": "#0039A6", "E": "#0039A6",
    "B": "#FF6319", "D": "#FF6319", "F": "#FF6319", "M": "#FF6319",
    "G": "#6CBE45",
    "J": "#996633", "Z": "#996633",
    "L": "#A7A9AC",
    "N": "#FCCC0A", "Q": "#FCCC0A", "R": "#FCCC0A", "W": "#FCCC0A",
    "S": "#808183", "SI": "#808183", "GS": "#808183", "FS": "#808183", "H": "#808183",
}


def haversine_m(a: tuple[float, float], b: tuple[float, float]) -> float:
    lat1, lon1 = math.radians(a[0]), math.radians(a[1])
    lat2, lon2 = math.radians(b[0]), math.radians(b[1])
    dlat, dlon = lat2 - lat1, lon2 - lon1
    h = math.sin(dlat / 2) ** 2 + math.cos(lat1) * math.cos(lat2) * math.sin(dlon / 2) ** 2
    return 6371000 * 2 * math.asin(min(1, math.sqrt(h)))


def simplify(points: list[tuple[float, float]], min_step_m: float = 35) -> list[tuple[float, float]]:
    if len(points) < 2:
        return points
    out = [points[0]]
    for p in points[1:]:
        if haversine_m(out[-1], p) >= min_step_m:
            out.append(p)
    if out[-1] != points[-1]:
        out.append(points[-1])
    return out


def main() -> None:
    with zipfile.ZipFile(ZIP_PATH) as zf:
        shapes: dict[str, list[tuple[float, float]]] = defaultdict(list)
        with zf.open("shapes.txt") as f:
            for row in csv.DictReader(line.decode("utf-8") for line in f):
                sid = row["shape_id"]
                lat = float(row["shape_pt_lat"])
                lon = float(row["shape_pt_lon"])
                seq = int(row["shape_pt_sequence"])
                shapes[sid].append((seq, lat, lon))

        route_to_shapes: dict[str, set[str]] = defaultdict(set)
        with zf.open("trips.txt") as f:
            for row in csv.DictReader(line.decode("utf-8") for line in f):
                route_to_shapes[row["route_id"]].add(row["shape_id"])

    features = []
    for route, shape_ids in sorted(route_to_shapes.items()):
        if route not in ROUTE_COLORS:
            continue
        best = max(shape_ids, key=lambda sid: len(shapes.get(sid, [])))
        pts = [(lat, lon) for _, lat, lon in sorted(shapes.get(best, []))]
        if len(pts) < 2:
            continue
        pts = simplify(pts)
        coords = [[lon, lat] for lat, lon in pts]
        features.append(
            {
                "type": "Feature",
                "properties": {"route": route, "color": ROUTE_COLORS[route]},
                "geometry": {"type": "LineString", "coordinates": coords},
            }
        )

    OUT_PATH.parent.mkdir(parents=True, exist_ok=True)
    OUT_PATH.write_text(json.dumps({"type": "FeatureCollection", "features": features}), encoding="utf-8")
    print(f"Wrote {len(features)} routes to {OUT_PATH} ({OUT_PATH.stat().st_size // 1024} KB)")


if __name__ == "__main__":
    main()
