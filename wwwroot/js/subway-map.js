const SUBWAY_MAP_STYLE = "https://tiles.openfreemap.org/styles/dark";
const TRACKS_URLS = ["/data/subway-tracks.geojson", "/data/nj-rail-tracks.geojson"];
const STOPS_URLS = ["/data/subway-stops.geojson", "/data/nj-rail-stops.geojson"];
const MAP_CENTER = [-74.02, 40.72];
const MAP_ZOOM = 9.28;
const MAP_PITCH = 58;
const MAP_BEARING = -14;
const MAP_PADDING = { top: 28, bottom: 56, left: 220, right: 32 };
const FOCUS_PITCH = 50;
const FOCUS_ZOOM = 13.8;
const TRACK_ANIM_MIN_MS = 5000;
const TRACK_ANIM_MAX_MS = 12000;
const GLIDE_MIN_GAP_M = 40;
const MAX_GLIDE_M = 2800;
const STOP_CONFIRM_POLLS = 2;
const REMOVE_AFTER_MISSED_POLLS = 3;

window.subwayMap = {
    map: null,
    markersById: new Map(),
    markerStates: new Map(),
    routeFilter: null,
    animFrameId: null,
    tracksInstalled: false,
    stopsInstalled: false,
    stopsGeojson: null,
    stopPopup: null,
    trackEngine: createTrackEngine(),
    lastFrameTime: 0,

    async init(mapElementId, routeIds) {
        await ensureMapLibreLoaded();

        if (this.map) {
            this.stopAnimationLoop();
            this.map.remove();
            this.map = null;
        }

        this.markersById.clear();
        this.markerStates.clear();
        this.routeFilter = null;
        this.tracksInstalled = false;
        this.stopsInstalled = false;
        this.stopsGeojson = null;
        this._stopHandlersBound = false;
        if (this.stopPopup) {
            this.stopPopup.remove();
            this.stopPopup = null;
        }

        this.map = new maplibregl.Map({
            container: mapElementId,
            style: SUBWAY_MAP_STYLE,
            center: MAP_CENTER,
            zoom: MAP_ZOOM,
            pitch: MAP_PITCH,
            bearing: MAP_BEARING,
            padding: MAP_PADDING,
            maxPitch: 65,
            antialias: true,
            attributionControl: true,
        });

        this.map.addControl(new maplibregl.NavigationControl({ showCompass: false }), "bottom-right");
        this.map.on("load", () => {
            this.installTrackLayers();
            this.installStopLayers();
        });

        await Promise.all([this.trackEngine.loadMany(TRACKS_URLS), this.loadStopsFromUrls(STOPS_URLS)]);
        if (this.map.isStyleLoaded()) {
            this.installTrackLayers();
            this.installStopLayers();
        }

        if (routeIds !== undefined) {
            this.setRouteFilter(routeIds);
        }

    },

    isMobile() {
        return typeof window !== "undefined" && window.matchMedia("(max-width: 640px)").matches;
    },

    installTrackLayers() {
        if (!this.map || this.tracksInstalled || !this.trackEngine.ready) {
            return;
        }

        const data = this.trackEngine.geojson;
        if (!data) {
            return;
        }

        if (!this.map.getSource("subway-tracks")) {
            this.map.addSource("subway-tracks", { type: "geojson", data });
            this.map.addLayer({
                id: "subway-tracks-casing",
                type: "line",
                source: "subway-tracks",
                layout: { "line-join": "round", "line-cap": "round" },
                paint: {
                    "line-color": "#050505",
                    "line-width": 7,
                    "line-opacity": 0.65,
                },
            });
            this.map.addLayer({
                id: "subway-tracks-line",
                type: "line",
                source: "subway-tracks",
                layout: { "line-join": "round", "line-cap": "round" },
                paint: {
                    "line-color": ["get", "color"],
                    "line-width": 3.5,
                    "line-opacity": 0.92,
                },
            });
            this.map.addLayer({
                id: "subway-tracks-glow",
                type: "line",
                source: "subway-tracks",
                layout: { "line-join": "round", "line-cap": "round" },
                paint: {
                    "line-color": ["get", "color"],
                    "line-width": 1.5,
                    "line-opacity": 0.35,
                    "line-blur": 2,
                },
            });
        }

        this.tracksInstalled = true;
        this.applyTrackRouteFilter();
    },

    async loadStopsFromUrls(urls) {
        const collections = await Promise.all(urls.map((url) => fetch(url).then((r) => r.json())));
        this.stopsGeojson = {
            type: "FeatureCollection",
            features: collections.flatMap((c) => c.features ?? []),
        };
    },

    installStopLayers() {
        if (!this.map || this.stopsInstalled || !this.stopsGeojson) {
            return;
        }

        if (!this.map.getSource("subway-stops")) {
            this.map.addSource("subway-stops", { type: "geojson", data: this.stopsGeojson });
            this.map.addLayer({
                id: "subway-stops-circle",
                type: "circle",
                source: "subway-stops",
                paint: {
                    "circle-radius": ["interpolate", ["linear"], ["zoom"], 10, 2.2, 13, 4, 15, 5.5],
                    "circle-color": "#cbd5e1",
                    "circle-stroke-color": "#475569",
                    "circle-stroke-width": 1,
                    "circle-opacity": 0.9,
                },
            });
            this.map.addLayer({
                id: "subway-stops-label",
                type: "symbol",
                source: "subway-stops",
                minzoom: 13.5,
                layout: {
                    "text-field": ["get", "name"],
                    "text-size": 10,
                    "text-offset": [0, 1.1],
                    "text-anchor": "top",
                    "text-max-width": 8,
                    "text-letter-spacing": 0.02,
                },
                paint: {
                    "text-color": "#94a3b8",
                    "text-halo-color": "#0d1117",
                    "text-halo-width": 1.25,
                },
            });
            this.bindStopInteractions();
        }

        this.stopsInstalled = true;
        this.applyStopRouteFilter();
    },

    bindStopInteractions() {
        if (!this.map || this._stopHandlersBound) {
            return;
        }
        this._stopHandlersBound = true;

        this.stopPopup = new maplibregl.Popup({
            closeButton: true,
            maxWidth: "260px",
            className: "train-map-popup",
            offset: 12,
        });

        this.map.on("click", "subway-stops-circle", (e) => {
            const feature = e.features?.[0];
            if (!feature) {
                return;
            }
            const coords = feature.geometry.coordinates.slice();
            const props = feature.properties ?? {};
            this.stopPopup
                .setLngLat(coords)
                .setHTML(this.buildStopPopupHtml(props))
                .addTo(this.map);
        });

        this.map.on("mouseenter", "subway-stops-circle", () => {
            this.map.getCanvas().style.cursor = "pointer";
        });
        this.map.on("mouseleave", "subway-stops-circle", () => {
            this.map.getCanvas().style.cursor = "";
        });
    },

    buildStopPopupHtml(props) {
        const name = props.name ?? "Station";
        const routes = parseRoutesProperty(props.routes);
        const routeTags = routes
            .map((r) => `<span class="stop-popup-route">${escapeHtml(r)}</span>`)
            .join("");
        return `
          <div class="train-popup stop-popup">
            <div class="stop-popup-name"><strong>${escapeHtml(name)}</strong></div>
            ${routeTags ? `<div class="stop-popup-routes">${routeTags}</div>` : ""}
          </div>
        `;
    },

    applyStopRouteFilter() {
        if (!this.map || !this.stopsInstalled) {
            return;
        }

        const filter = this.buildStopFilter();
        for (const layerId of ["subway-stops-circle", "subway-stops-label"]) {
            if (this.map.getLayer(layerId)) {
                this.map.setFilter(layerId, filter);
            }
        }
    },

    buildStopFilter() {
        if (this.routeFilter === null) {
            return null;
        }
        if (this.routeFilter.size === 0) {
            return ["literal", false];
        }
        const routes = [...this.routeFilter];
        return ["any", ...routes.map((r) => ["in", r, ["get", "routes"]])];
    },

    applyTrackRouteFilter() {
        if (!this.map || !this.tracksInstalled) {
            return;
        }

        const filter = this.buildTrackFilter();
        for (const layerId of ["subway-tracks-casing", "subway-tracks-line", "subway-tracks-glow"]) {
            if (this.map.getLayer(layerId)) {
                this.map.setFilter(layerId, filter);
            }
        }
    },

    buildTrackFilter() {
        if (!this.routeFilter) {
            return null;
        }
        return ["in", ["get", "route"], ["literal", [...this.routeFilter]]];
    },

    setRouteFilter(routes) {
        const list = normalizeRouteList(routes);
        if (list == null) {
            this.routeFilter = null;
        } else {
            this.routeFilter = new Set(list.map((r) => String(r).toUpperCase()));
        }
        this.applyRouteVisibility();
        this.applyTrackRouteFilter();
        this.applyStopRouteFilter();
    },

    applyRouteVisibility() {
        if (!this.map) {
            return;
        }

        for (const state of this.markerStates.values()) {
            const visible = this.isRouteVisible(state.data.route);
            if (!state.marker) {
                continue;
            }

            if (visible && !state.onMap) {
                state.marker.addTo(this.map);
                state.onMap = true;
            } else if (!visible && state.onMap) {
                state.marker.remove();
                state.onMap = false;
            }
        }
    },

    isRouteVisible(route) {
        if (!this.routeFilter) {
            return true;
        }
        const r = route && route !== "?" ? String(route).toUpperCase() : "?";
        return this.routeFilter.has(r);
    },

    updateMarkers(markers) {
        if (!this.map) {
            return;
        }

        if (!this.map.isStyleLoaded()) {
            this.map.once("load", () => this.updateMarkers(markers));
            return;
        }

        const now = performance.now();
        const seen = new Set();

        for (const m of markers) {
            seen.add(m.id);
            let state = this.markerStates.get(m.id);
            if (state) {
                state.missedPolls = 0;
            }

            if (!state) {
                const pos = this.resolvePosition(m);
                const marker = this.createMarker(m, pos.lat, pos.lon);
                state = {
                    data: m,
                    dataKey: markerDataKey(m),
                    anchorStopId: motionAnchor(m),
                    pendingAnchorStopId: null,
                    pendingAnchorCount: 0,
                    marker,
                    onMap: false,
                    trackDist: pos.trackDist,
                    targetTrackDist: pos.trackDist,
                    trackFromDist: pos.trackDist,
                    animEndDist: pos.trackDist,
                    animStart: 0,
                    animDuration: 0,
                    displayLat: pos.lat,
                    displayLon: pos.lon,
                    missedPolls: 0,
                };
                this.markerStates.set(m.id, state);
                this.markersById.set(m.id, marker);
                if (this.isRouteVisible(m.route)) {
                    marker.addTo(this.map);
                    state.onMap = true;
                }
                continue;
            }

            const dataKey = markerDataKey(m);
            if (dataKey !== state.dataKey) {
                state.data = m;
                state.dataKey = dataKey;
                this.refreshMarkerVisual(state);
            }

            const anchor = motionAnchor(m);
            const anchorChanged = anchor !== String(state.anchorStopId ?? "");
            if (!anchorChanged) {
                state.pendingAnchorStopId = null;
                state.pendingAnchorCount = 0;
                continue;
            }

            if (!anchor) {
                continue;
            }

            if (anchor === state.pendingAnchorStopId) {
                state.pendingAnchorCount += 1;
            } else {
                state.pendingAnchorStopId = anchor;
                state.pendingAnchorCount = 1;
            }

            if (state.pendingAnchorCount < STOP_CONFIRM_POLLS) {
                continue;
            }

            state.pendingAnchorStopId = null;
            state.pendingAnchorCount = 0;
            state.anchorStopId = anchor;

            state.data = m;
            state.dataKey = dataKey;

            const target = this.resolvePosition(m);
            if (target.trackDist == null) {
                state.trackDist = null;
                state.targetTrackDist = null;
                state.animDuration = 0;
                this.setMarkerLngLat(state, target.lon, target.lat);
                continue;
            }

            this.scheduleGlide(state, target, now);
        }

        for (const [id, state] of this.markerStates) {
            if (seen.has(id)) {
                continue;
            }
            state.missedPolls = (state.missedPolls ?? 0) + 1;
            if (state.missedPolls < REMOVE_AFTER_MISSED_POLLS) {
                continue;
            }
            state.marker.remove();
            this.markerStates.delete(id);
            this.markersById.delete(id);
        }

        this.applyRouteVisibility();
        if (this.shouldRunAnimationLoop()) {
            this.startAnimationLoop();
        }
    },

    resolvePosition(m) {
        const trackPos = this.trackEngine.project(m.route, m.latitude, m.longitude);
        if (trackPos) {
            return trackPos;
        }
        return { lat: m.latitude, lon: m.longitude, trackDist: null };
    },

    scheduleGlide(state, target, now) {
        const track = this.trackEngine.getRoute(state.data.route);
        const currentDist =
            state.trackDist != null && state.animDuration > 0
                ? state.trackDist
                : state.trackDist ?? target.trackDist;
        const { endDist, gapM } = shortestTrackGap(track, currentDist, target.trackDist);
        state.targetTrackDist = target.trackDist;

        if (gapM < GLIDE_MIN_GAP_M) {
            state.trackDist = target.trackDist;
            state.animEndDist = target.trackDist;
            state.animDuration = 0;
            this.setMarkerLngLat(state, target.lon, target.lat);
            return;
        }

        if (gapM > MAX_GLIDE_M) {
            state.trackDist = target.trackDist;
            state.animEndDist = target.trackDist;
            state.animDuration = 0;
            this.setMarkerLngLat(state, target.lon, target.lat);
            return;
        }

        state.trackFromDist = currentDist;
        state.animEndDist = endDist;
        state.animStart = now;
        state.animDuration = animationDurationForGap(gapM);
    },

    shouldRunAnimationLoop() {
        for (const state of this.markerStates.values()) {
            if (state.onMap && state.animDuration > 0) {
                return true;
            }
        }
        return false;
    },

    createMarker(m, lat, lon) {
        const el = this.buildMarkerElement(m);
        const popup = new maplibregl.Popup({
            offset: 20,
            closeButton: true,
            maxWidth: "280px",
            className: "train-map-popup",
        }).setHTML(this.buildPopupHtml(m));

        return new maplibregl.Marker({ element: el, anchor: "center" })
            .setLngLat([lon, lat])
            .setPopup(popup);
    },

    setMarkerLngLat(state, lon, lat) {
        state.displayLon = lon;
        state.displayLat = lat;
        state.marker.setLngLat([lon, lat]);
    },

    refreshMarkerVisual(state) {
        state.marker.getElement().innerHTML = this.buildMarkerInnerHtml(state.data);
        const popup = state.marker.getPopup();
        if (popup) {
            popup.setHTML(this.buildPopupHtml(state.data));
        }
    },

    buildMarkerElement(m) {
        const el = document.createElement("div");
        el.className = "train-marker-shell";
        el.innerHTML = this.buildMarkerInnerHtml(m);
        return el;
    },

    buildMarkerInnerHtml(m) {
        const route = m.route && m.route !== "?" ? m.route : "•";
        const textColor = isLightRoute(route) ? "#111" : "#fff";
        const motionClass = m.inMotion ? " train-marker--moving" : "";
        const compactClass = route.length > 2 ? " train-marker--compact" : "";
        const trainNo = m.trainNumber ? String(m.trainNumber) : "";
        const title = trainNo
            ? `Train ${trainNo}${m.label ? ` · ${m.label}` : ""}`
            : m.label || route;
        const numberTag = trainNo
            ? `<span class="train-marker-number">${escapeHtml(trainNo)}</span>`
            : "";
        return `
              <div class="train-marker-wrap">
                <div class="train-marker${motionClass}${compactClass}" style="background:${m.color};color:${textColor}" title="${escapeHtml(title)}">
                  <span class="train-marker-route">${escapeHtml(route)}</span>
                </div>
                ${numberTag}
              </div>
              <div class="train-marker-pulse${m.inMotion ? " train-marker-pulse--active" : ""}" style="border-color:${m.color}"></div>
            `;
    },

    buildPopupHtml(m) {
        const route = m.route && m.route !== "?" ? m.route : "•";
        const textColor = isLightRoute(route) ? "#111" : "#fff";
        const trainLine = m.trainNumber
            ? `<div class="train-popup-train">Train <strong>${escapeHtml(m.trainNumber)}</strong></div>`
            : "";
        return `
          <div class="train-popup">
            <div class="train-popup-route" style="background:${m.color};color:${textColor}">${escapeHtml(route)}</div>
            ${trainLine}
            <div><strong>${escapeHtml(m.status || "En route")}</strong></div>
            ${m.label ? `<div class="train-popup-meta">${escapeHtml(m.label)}</div>` : ""}
          </div>
        `;
    },

    startAnimationLoop() {
        if (this.animFrameId != null) {
            return;
        }
        this.lastFrameTime = performance.now();

        const tick = (now) => {
            this.lastFrameTime = now;
            const stillAnimating = this.stepAnimations(now);
            if (stillAnimating) {
                this.animFrameId = requestAnimationFrame(tick);
            } else {
                this.animFrameId = null;
            }
        };

        this.animFrameId = requestAnimationFrame(tick);
    },

    stopAnimationLoop() {
        if (this.animFrameId != null) {
            cancelAnimationFrame(this.animFrameId);
            this.animFrameId = null;
        }
    },

    stepAnimations(now) {
        let anyActive = false;

        for (const state of this.markerStates.values()) {
            if (!state.marker || !state.onMap || state.animDuration <= 0) {
                continue;
            }

            const route = state.data.route;
            const track = this.trackEngine.getRoute(route);

            if (track && state.trackDist != null && state.targetTrackDist != null) {
                const elapsed = now - state.animStart;
                const t = Math.min(1, elapsed / state.animDuration);
                if (t >= 1) {
                    state.animDuration = 0;
                    state.trackDist = state.targetTrackDist;
                } else {
                    anyActive = true;
                    const eased = easeInOutCubic(t);
                    const raw = state.trackFromDist + (state.animEndDist - state.trackFromDist) * eased;
                    state.trackDist = wrapTrackDist(track, raw);
                }

                const pt = this.trackEngine.pointAtDist(route, state.trackDist);
                if (pt) {
                    this.setMarkerLngLat(state, pt.lon, pt.lat);
                }
            }
        }

        return anyActive;
    },

    flyToStation(lat, lon) {
        if (!this.map) {
            return;
        }

        this.map.flyTo({
            center: [lon, lat],
            zoom: 14.2,
            pitch: FOCUS_PITCH,
            bearing: MAP_BEARING,
            padding: this.map.getPadding(),
            duration: 900,
            essential: true,
        });
    },

    flyToTrain(id) {
        const marker = this.markersById.get(id);
        if (!marker || !this.map) {
            return;
        }

        const { lng, lat } = marker.getLngLat();
        this.map.flyTo({
            center: [lng, lat],
            zoom: FOCUS_ZOOM,
            pitch: FOCUS_PITCH,
            bearing: MAP_BEARING,
            padding: this.map.getPadding(),
            duration: 850,
            essential: true,
        });
        marker.togglePopup();
    },

    setLayoutPadding(panelOpen) {
        if (!this.map) {
            return;
        }

        this.map.setPadding(layoutPadding(panelOpen));
        this.map.resize();
    },

    invalidateSize() {
        if (this.map) {
            this.map.resize();
        }
    },

    resetView() {
        if (!this.map) {
            return;
        }

        this.clearPlannedRoute();
        this.map.flyTo({
            center: MAP_CENTER,
            zoom: MAP_ZOOM,
            pitch: MAP_PITCH,
            bearing: MAP_BEARING,
            padding: this.map.getPadding(),
            duration: 900,
            essential: true,
        });
    },

    setPlannedRoute(coordinates) {
        if (!this.map || !Array.isArray(coordinates) || coordinates.length < 2) {
            return;
        }

        const line = {
            type: "Feature",
            geometry: { type: "LineString", coordinates },
        };

        this.ensurePlannedRouteLayer();
        this.map.getSource("planned-route").setData(line);

        const lons = coordinates.map((c) => c[0]);
        const lats = coordinates.map((c) => c[1]);
        const bounds = [
            [Math.min(...lons), Math.min(...lats)],
            [Math.max(...lons), Math.max(...lats)],
        ];
        this.map.fitBounds(bounds, {
            padding: { top: 80, bottom: 80, left: 240, right: 60 },
            duration: 950,
            pitch: FOCUS_PITCH,
            bearing: MAP_BEARING,
            essential: true,
        });
    },

    clearPlannedRoute() {
        if (!this.map || !this.map.getSource("planned-route")) {
            return;
        }

        this.map.getSource("planned-route").setData({
            type: "Feature",
            geometry: { type: "LineString", coordinates: [] },
        });
    },

    ensurePlannedRouteLayer() {
        if (!this.map || this.map.getSource("planned-route")) {
            return;
        }

        this.map.addSource("planned-route", {
            type: "geojson",
            data: { type: "Feature", geometry: { type: "LineString", coordinates: [] } },
        });

        const beforeStops = this.map.getLayer("subway-stops-circle") ? "subway-stops-circle" : undefined;
        this.map.addLayer(
            {
                id: "planned-route-casing",
                type: "line",
                source: "planned-route",
                layout: { "line-join": "round", "line-cap": "round" },
                paint: {
                    "line-color": "#fccc0a",
                    "line-width": 10,
                    "line-opacity": 0.85,
                },
            },
            beforeStops
        );
        this.map.addLayer(
            {
                id: "planned-route-line",
                type: "line",
                source: "planned-route",
                layout: { "line-join": "round", "line-cap": "round" },
                paint: {
                    "line-color": "#ffffff",
                    "line-width": 4,
                    "line-opacity": 0.95,
                },
            },
            beforeStops
        );
    },
};

function createTrackEngine() {
    return {
        ready: false,
        geojson: null,
        routes: new Map(),

        async loadMany(urls) {
            const collections = await Promise.all(urls.map((url) => fetch(url).then((r) => r.json())));
            this.geojson = {
                type: "FeatureCollection",
                features: collections.flatMap((c) => c.features ?? []),
            };
            this.routes.clear();
            for (const feature of this.geojson.features ?? []) {
                const route = feature.properties?.route;
                const coords = feature.geometry?.coordinates;
                if (!route || !coords || coords.length < 2) {
                    continue;
                }
                this.routes.set(String(route).toUpperCase(), buildTrack(coords));
            }
            this.ready = true;
        },

        getRoute(routeId) {
            if (!routeId) {
                return null;
            }
            return this.routes.get(String(routeId).toUpperCase()) ?? null;
        },

        project(routeId, lat, lon) {
            const track = this.getRoute(routeId);
            if (!track) {
                return null;
            }
            const hit = nearestOnTrack(track, lon, lat);
            return { lat: hit.lat, lon: hit.lon, trackDist: hit.distAlong };
        },

        pointAtDist(routeId, distAlong) {
            const track = this.getRoute(routeId);
            if (!track) {
                return null;
            }
            return pointAtDist(track, distAlong);
        },
    };
}

function buildTrack(coordsLonLat) {
    const points = coordsLonLat.map(([lon, lat]) => ({ lon, lat }));
    const segDist = [0];
    let total = 0;
    for (let i = 1; i < points.length; i++) {
        total += haversineM(points[i - 1], points[i]);
        segDist.push(total);
    }
    return { points, segDist, totalDist: total };
}

function nearestOnTrack(track, lon, lat) {
    let best = { distAlong: 0, lat: track.points[0].lat, lon: track.points[0].lon, d: Infinity };
    for (let i = 1; i < track.points.length; i++) {
        const a = track.points[i - 1];
        const b = track.points[i];
        const hit = projectOnSegment(a, b, lon, lat);
        const base = track.segDist[i - 1];
        const segLen = track.segDist[i] - base;
        const distAlong = base + hit.t * segLen;
        if (hit.d < best.d) {
            best = { distAlong, lat: hit.lat, lon: hit.lon, d: hit.d };
        }
    }
    return best;
}

function pointAtDist(track, distAlong) {
    distAlong = clamp(distAlong, 0, track.totalDist);
    let i = 1;
    while (i < track.segDist.length && track.segDist[i] < distAlong) {
        i++;
    }
    if (i >= track.points.length) {
        const last = track.points[track.points.length - 1];
        return { lat: last.lat, lon: last.lon };
    }
    const segStart = track.segDist[i - 1];
    const segLen = track.segDist[i] - segStart;
    const t = segLen <= 0 ? 0 : (distAlong - segStart) / segLen;
    const a = track.points[i - 1];
    const b = track.points[i];
    return {
        lat: a.lat + (b.lat - a.lat) * t,
        lon: a.lon + (b.lon - a.lon) * t,
    };
}

function projectOnSegment(a, b, lon, lat) {
    const ax = a.lon;
    const ay = a.lat;
    const bx = b.lon;
    const by = b.lat;
    const px = lon;
    const py = lat;
    const dx = bx - ax;
    const dy = by - ay;
    const len2 = dx * dx + dy * dy;
    let t = len2 === 0 ? 0 : ((px - ax) * dx + (py - ay) * dy) / len2;
    t = clamp(t, 0, 1);
    const qx = ax + dx * t;
    const qy = ay + dy * t;
    const d = haversineM({ lon: px, lat: py }, { lon: qx, lat: qy });
    return { t, lat: qy, lon: qx, d };
}

function haversineM(a, b) {
    const r = 6371000;
    const lat1 = (a.lat * Math.PI) / 180;
    const lat2 = (b.lat * Math.PI) / 180;
    const dLat = lat2 - lat1;
    const dLon = ((b.lon - a.lon) * Math.PI) / 180;
    const h = Math.sin(dLat / 2) ** 2 + Math.cos(lat1) * Math.cos(lat2) * Math.sin(dLon / 2) ** 2;
    return 2 * r * Math.asin(Math.min(1, Math.sqrt(h)));
}

function animationDurationForGap(gapM) {
    const scaled = TRACK_ANIM_MIN_MS + (gapM / 450) * 2500;
    return clamp(scaled, TRACK_ANIM_MIN_MS, TRACK_ANIM_MAX_MS);
}

function clamp(v, min, max) {
    return Math.max(min, Math.min(max, v));
}

function layoutPadding(panelOpen) {
    const mobile = typeof window !== "undefined" && window.matchMedia("(max-width: 640px)").matches;
    if (mobile) {
        return {
            top: 16,
            bottom: panelOpen ? 340 : 88,
            left: 16,
            right: 16,
        };
    }

    if (panelOpen) {
        return { ...MAP_PADDING };
    }

    return {
        top: MAP_PADDING.top,
        bottom: MAP_PADDING.bottom,
        left: 48,
        right: MAP_PADDING.right,
    };
}

function motionAnchor(m) {
    return String(m.anchorStopId ?? m.stopId ?? "").trim();
}

function markerDataKey(m) {
    return `${m.route}|${m.status}|${m.inMotion}|${m.label}|${m.color}|${m.stopId ?? ""}|${motionAnchor(m)}|${m.trainNumber ?? ""}`;
}

function wrapTrackDist(track, dist) {
    if (!track || track.totalDist <= 0) {
        return dist;
    }
    let d = dist % track.totalDist;
    if (d < 0) {
        d += track.totalDist;
    }
    return d;
}

function shortestTrackGap(track, fromDist, toDist) {
    if (fromDist == null || toDist == null) {
        return { endDist: toDist, gapM: Infinity };
    }
    if (!track || track.totalDist <= 0) {
        return { endDist: toDist, gapM: Math.abs(toDist - fromDist) };
    }

    const direct = toDist - fromDist;
    const forward = direct >= 0 ? direct : direct + track.totalDist;
    const backward = direct <= 0 ? -direct : track.totalDist - direct;
    const useForward = forward <= backward;
    const gapM = useForward ? forward : backward;
    const endDist = useForward
        ? toDist >= fromDist
            ? toDist
            : toDist + track.totalDist
        : toDist <= fromDist
          ? toDist
          : toDist - track.totalDist;
    return { endDist, gapM };
}

function parseRoutesProperty(routes) {
    if (Array.isArray(routes)) {
        return routes.map(String);
    }
    if (typeof routes === "string") {
        try {
            const parsed = JSON.parse(routes);
            if (Array.isArray(parsed)) {
                return parsed.map(String);
            }
        } catch {
            return routes
                .split(",")
                .map((s) => s.trim())
                .filter(Boolean);
        }
    }
    return [];
}

function normalizeRouteList(routes) {
    if (routes == null) {
        return null;
    }
    if (Array.isArray(routes)) {
        return routes;
    }
    if (typeof routes === "string") {
        return [routes];
    }
    if (typeof routes === "object") {
        return Object.values(routes);
    }
    return [];
}

function ensureMapLibreLoaded(timeoutMs = 8000) {
    if (typeof maplibregl !== "undefined") {
        return Promise.resolve();
    }

    return new Promise((resolve, reject) => {
        const started = performance.now();
        const timer = setInterval(() => {
            if (typeof maplibregl !== "undefined") {
                clearInterval(timer);
                resolve();
                return;
            }
            if (performance.now() - started > timeoutMs) {
                clearInterval(timer);
                reject(new Error("Map failed to load. Refresh the page."));
            }
        }, 40);
    });
}

function easeInOutCubic(t) {
    return t < 0.5 ? 4 * t * t * t : 1 - Math.pow(-2 * t + 2, 3) / 2;
}

function isLightRoute(route) {
    const r = String(route ?? "").toUpperCase();
    return (
        r === "N" ||
        r === "Q" ||
        r === "R" ||
        r === "W" ||
        r === "L" ||
        r === "MNBN" ||
        r === "MNEG"
    );
}

function escapeHtml(value) {
    return String(value ?? "")
        .replaceAll("&", "&amp;")
        .replaceAll("<", "&lt;")
        .replaceAll(">", "&gt;")
        .replaceAll('"', "&quot;");
}
