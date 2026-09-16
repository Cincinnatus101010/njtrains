#!/usr/bin/env python3
"""Build NJ Transit rail track + station GeoJSON from GTFS (commuter rail routes only)."""
from __future__ import annotations

import csv
import json
import math
import zipfile
from collections import defaultdict
from pathlib import Path

ZIP_PATH = Path("/tmp/njt-rail-gtfs.zip")
OUT_TRACKS = Path(__file__).resolve().parents[1] / "wwwroot" / "data" / "nj-rail-tracks.geojson"
OUT_STOPS = Path(__file__).resolve().parents[1] / "wwwroot" / "data" / "nj-rail-stops.geojson"
OUT_STOPS_TXT = Path(__file__).resolve().parents[1] / "Data" / "njt-stops.txt"

# Commuter rail only (skip light rail route_type 0)
COMMUTER = {
    "ATLC", "BNTN", "MNBN", "MNE", "MNEG", "NEC", "NJCL", "PASC", "RARV", "PRIN", "MRL",
}


def haversine_m(a: tuple[float, float], b: tuple[float, float]) -> float:
    lat1, lon1 = math.radians(a[0]), math.radians(a[1])
    lat2, lon2 = math.radians(b[0]), math.radians(b[1])
    dlat, dlon = lat2 - lat1, lon2 - lon1
    h = math.sin(dlat / 2) ** 2 + math.cos(lat1) * math.cos(lat2) * math.sin(dlon / 2) ** 2
    return 6371000 * 2 * math.asin(min(1, math.sqrt(h)))


def simplify(points: list[tuple[float, float]], min_step_m: float = 45) -> list[tuple[float, float]]:
    if len(points) < 2:
        return points
    out = [points[0]]
    for p in points[1:]:
        if haversine_m(out[-1], p) >= min_step_m:
            out.append(p)
    if out[-1] != points[-1]:
        out.append(points[-1])
    return out


def color_hex(raw: str) -> str:
    raw = (raw or "888888").strip().lstrip("#")
    return f"#{raw}" if raw else "#888888"


def main() -> None:
    with zipfile.ZipFile(ZIP_PATH) as zf:
        routes: dict[str, dict] = {}
        with zf.open("routes.txt") as f:
            for row in csv.DictReader(line.decode("utf-8") for line in f):
                short = row["route_short_name"].strip()
                if short not in COMMUTER:
                    continue
                routes[short] = {
                    "color": color_hex(row.get("route_color", "")),
                    "name": row["route_long_name"].strip(),
                }

        shapes: dict[str, list[tuple[float, float]]] = defaultdict(list)
        with zf.open("shapes.txt") as f:
            for row in csv.DictReader(line.decode("utf-8") for line in f):
                sid = row["shape_id"]
                lat = float(row["shape_pt_lat"])
                lon = float(row["shape_pt_lon"])
                seq = int(row["shape_pt_sequence"])
                shapes[sid].append((seq, lat, lon))

        route_id_to_short: dict[str, str] = {}
        with zf.open("routes.txt") as f:
            for row in csv.DictReader(line.decode("utf-8") for line in f):
                short = row["route_short_name"].strip()
                if short in COMMUTER:
                    route_id_to_short[row["route_id"]] = short

        route_to_shapes: dict[str, set[str]] = defaultdict(set)
        with zf.open("trips.txt") as f:
            for row in csv.DictReader(line.decode("utf-8") for line in f):
                short = route_id_to_short.get(row["route_id"])
                if short:
                    route_to_shapes[short].add(row["shape_id"])

        stops_rows = []
        with zf.open("stops.txt") as f:
            for row in csv.DictReader(line.decode("utf-8") for line in f):
                stops_rows.append(row)

        stop_routes: dict[str, set[str]] = defaultdict(set)
        trip_route: dict[str, str] = {}
        with zf.open("trips.txt") as f:
            for row in csv.DictReader(line.decode("utf-8") for line in f):
                short = route_id_to_short.get(row["route_id"])
                if short:
                    trip_route[row["trip_id"]] = short

        with zf.open("stop_times.txt") as f:
            for row in csv.DictReader(line.decode("utf-8") for line in f):
                short = trip_route.get(row["trip_id"])
                if short:
                    stop_routes[row["stop_id"].strip()].add(short)

    track_features = []
    for short, meta in sorted(routes.items()):
        shape_ids = route_to_shapes.get(short, set())
        if not shape_ids:
            continue
        best = max(shape_ids, key=lambda sid: len(shapes.get(sid, [])))
        pts = [(lat, lon) for _, lat, lon in sorted(shapes.get(best, []))]
        if len(pts) < 2:
            continue
        pts = simplify(pts)
        track_features.append(
            {
                "type": "Feature",
                "properties": {"route": short, "color": meta["color"], "name": meta["name"]},
                "geometry": {"type": "LineString", "coordinates": [[lon, lat] for lat, lon in pts]},
            }
        )

    stop_features = []
    seen_names: set[str] = set()
    for row in stops_rows:
        sid = row["stop_id"].strip()
        routes_at = sorted(stop_routes.get(sid, set()) & COMMUTER)
        if not routes_at:
            continue
        name = row["stop_name"].strip()
        lat = float(row["stop_lat"])
        lon = float(row["stop_lon"])
        key = name.lower()
        if key in seen_names:
            continue
        seen_names.add(key)
        stop_features.append(
            {
                "type": "Feature",
                "properties": {"stop_id": sid, "name": name, "routes": routes_at},
                "geometry": {"type": "Point", "coordinates": [lon, lat]},
            }
        )

    OUT_TRACKS.parent.mkdir(parents=True, exist_ok=True)
    OUT_TRACKS.write_text(
        json.dumps({"type": "FeatureCollection", "features": track_features}),
        encoding="utf-8",
    )
    OUT_STOPS.write_text(
        json.dumps({"type": "FeatureCollection", "features": stop_features}),
        encoding="utf-8",
    )
    OUT_STOPS_TXT.parent.mkdir(parents=True, exist_ok=True)
    with OUT_STOPS_TXT.open("w", encoding="utf-8") as f:
        f.write("stop_id,stop_name,stop_lat,stop_lon\n")
        for feat in stop_features:
            props = feat["properties"]
            sid = props["stop_id"]
            name = props["name"]
            lon, lat = feat["geometry"]["coordinates"]
            f.write(f"{sid},{name},{lat},{lon}\n")

    print(f"Tracks: {len(track_features)} routes -> {OUT_TRACKS}")
    print(f"Stops: {len(stop_features)} stations -> {OUT_STOPS}")
    print(f"Stops txt: {OUT_STOPS_TXT}")


if __name__ == "__main__":
    main()
