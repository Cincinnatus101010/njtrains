#!/usr/bin/env python3
"""Build stop graph for trip planning from MTA + NJT GTFS zips."""
from __future__ import annotations

import csv
import json
import math
import zipfile
from collections import defaultdict
from pathlib import Path

MTA_ZIP = Path("/tmp/mta-static.zip")
NJT_ZIP = Path("/tmp/njt-rail-gtfs.zip")
OUT = Path(__file__).resolve().parents[1] / "Data" / "transit-graph.json"

SUBWAY_ROUTES = frozenset(
    list("1234567") + list("ACE") + list("BDFM") + list("G") + list("JZ") + list("L") + list("NQRW") + list("S")
)
NJT_COMMUTER = frozenset({"ATLC", "BNTN", "MNBN", "MNE", "MNEG", "NEC", "NJCL", "PASC", "RARV", "PRIN", "MRL"})


def haversine_m(a: tuple[float, float], b: tuple[float, float]) -> float:
    lat1, lon1 = math.radians(a[0]), math.radians(a[1])
    lat2, lon2 = math.radians(b[0]), math.radians(b[1])
    dlat, dlon = lat2 - lat1, lon2 - lon1
    h = math.sin(dlat / 2) ** 2 + math.cos(lat1) * math.cos(lat2) * math.sin(dlon / 2) ** 2
    return 6371000 * 2 * math.asin(min(1, math.sqrt(h)))


def add_edge(edges: set[tuple[str, str, str]], a: str, b: str, route: str) -> None:
    if a == b:
        return
    edges.add((a, b, route))
    edges.add((b, a, route))


def mta_graph(nodes: dict, edges: set[tuple[str, str, str]]) -> None:
    with zipfile.ZipFile(MTA_ZIP) as zf:
        stops_meta: dict[str, dict] = {}
        with zf.open("stops.txt") as f:
            for row in csv.DictReader(line.decode("utf-8") for line in f):
                sid = row["stop_id"].strip()
                parent = (row.get("parent_station") or "").strip()
                loc = (row.get("location_type") or "").strip()
                anchor = parent if parent else sid
                if loc == "1":
                    anchor = sid
                stops_meta[sid] = {
                    "anchor": anchor,
                    "name": row["stop_name"].strip(),
                    "lat": float(row["stop_lat"]),
                    "lon": float(row["stop_lon"]),
                }

        trip_route: dict[str, str] = {}
        with zf.open("trips.txt") as f:
            for row in csv.DictReader(line.decode("utf-8") for line in f):
                rid = row["route_id"].strip().upper()
                if rid in SUBWAY_ROUTES:
                    trip_route[row["trip_id"]] = rid

        trip_stops: dict[str, list[tuple[int, str]]] = defaultdict(list)
        with zf.open("stop_times.txt") as f:
            for row in csv.DictReader(line.decode("utf-8") for line in f):
                tid = row["trip_id"]
                if tid not in trip_route:
                    continue
                trip_stops[tid].append((int(row["stop_sequence"]), row["stop_id"].strip()))

        for tid, seq in trip_stops.items():
            route = trip_route[tid]
            ordered = [sid for _, sid in sorted(seq)]
            anchors = []
            for sid in ordered:
                meta = stops_meta.get(sid)
                if not meta:
                    continue
                anchors.append(meta["anchor"])
            for i in range(len(anchors) - 1):
                a, b = anchors[i], anchors[i + 1]
                if a != b:
                    add_edge(edges, f"mta:{a}", f"mta:{b}", route)

        for sid, meta in stops_meta.items():
            anchor = meta["anchor"]
            key = f"mta:{anchor}"
            if key not in nodes:
                nodes[key] = {
                    "name": meta["name"] if sid == anchor else stops_meta.get(anchor, meta)["name"],
                    "lat": meta["lat"],
                    "lon": meta["lon"],
                    "network": "mta",
                }

        # Parent station names for anchors
        for sid, meta in stops_meta.items():
            anchor = meta["anchor"]
            key = f"mta:{anchor}"
            if key in nodes and sid == anchor:
                nodes[key]["name"] = meta["name"]
                nodes[key]["lat"] = meta["lat"]
                nodes[key]["lon"] = meta["lon"]

        try:
            with zf.open("transfers.txt") as f:
                for row in csv.DictReader(line.decode("utf-8") for line in f):
                    if row.get("transfer_type", "0") not in ("0", "1", "2"):
                        continue
                    from_sid = stops_meta.get(row["from_stop_id"].strip(), {}).get("anchor")
                    to_sid = stops_meta.get(row["to_stop_id"].strip(), {}).get("anchor")
                    if from_sid and to_sid and from_sid != to_sid:
                        add_edge(edges, f"mta:{from_sid}", f"mta:{to_sid}", "walk")
        except KeyError:
            pass


def njt_graph(nodes: dict, edges: set[tuple[str, str, str]]) -> None:
    with zipfile.ZipFile(NJT_ZIP) as zf:
        route_id_to_short: dict[str, str] = {}
        with zf.open("routes.txt") as f:
            for row in csv.DictReader(line.decode("utf-8") for line in f):
                short = row["route_short_name"].strip()
                if short in NJT_COMMUTER:
                    route_id_to_short[row["route_id"]] = short

        stops_meta: dict[str, dict] = {}
        with zf.open("stops.txt") as f:
            for row in csv.DictReader(line.decode("utf-8") for line in f):
                sid = row["stop_id"].strip()
                stops_meta[sid] = {
                    "name": row["stop_name"].strip(),
                    "lat": float(row["stop_lat"]),
                    "lon": float(row["stop_lon"]),
                }

        trip_route: dict[str, str] = {}
        with zf.open("trips.txt") as f:
            for row in csv.DictReader(line.decode("utf-8") for line in f):
                short = route_id_to_short.get(row["route_id"])
                if short:
                    trip_route[row["trip_id"]] = short

        trip_stops: dict[str, list[tuple[int, str]]] = defaultdict(list)
        with zf.open("stop_times.txt") as f:
            for row in csv.DictReader(line.decode("utf-8") for line in f):
                tid = row["trip_id"]
                if tid not in trip_route:
                    continue
                trip_stops[tid].append((int(row["stop_sequence"]), row["stop_id"].strip()))

        for tid, seq in trip_stops.items():
            route = trip_route[tid]
            ordered = [sid for _, sid in sorted(seq)]
            for i in range(len(ordered) - 1):
                a, b = ordered[i], ordered[i + 1]
                if a != b:
                    add_edge(edges, f"njt:{a}", f"njt:{b}", route)

        for sid, meta in stops_meta.items():
            key = f"njt:{sid}"
            nodes[key] = {
                "name": meta["name"],
                "lat": meta["lat"],
                "lon": meta["lon"],
                "network": "njt",
            }


def cross_transfers(nodes: dict, edges: set[tuple[str, str, str]], max_m: float = 450) -> None:
    mta_keys = [k for k, v in nodes.items() if v["network"] == "mta"]
    njt_keys = [k for k, v in nodes.items() if v["network"] == "njt"]
    for mk in mta_keys:
        m = nodes[mk]
        for nk in njt_keys:
            n = nodes[nk]
            if haversine_m((m["lat"], m["lon"]), (n["lat"], n["lon"])) <= max_m:
                add_edge(edges, mk, nk, "walk")


def main() -> None:
    if not MTA_ZIP.is_file():
        raise SystemExit(f"Missing {MTA_ZIP} — download MTA static GTFS to /tmp/mta-static.zip")
    if not NJT_ZIP.is_file():
        raise SystemExit(f"Missing {NJT_ZIP} — download NJT rail GTFS to /tmp/njt-rail-gtfs.zip")

    nodes: dict = {}
    edges: set[tuple[str, str, str]] = set()
    mta_graph(nodes, edges)
    njt_graph(nodes, edges)
    cross_transfers(nodes, edges)

    OUT.parent.mkdir(parents=True, exist_ok=True)
    payload = {
        "nodes": nodes,
        "edges": [{"from": a, "to": b, "route": r} for a, b, r in sorted(edges)],
    }
    OUT.write_text(json.dumps(payload, separators=(",", ":")), encoding="utf-8")
    print(f"Nodes: {len(nodes)}, edges: {len(edges)} -> {OUT}")


if __name__ == "__main__":
    main()
