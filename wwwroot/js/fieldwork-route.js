// Field Work Phase 3 (2026-11-03): day route maps (Pages/FieldWork/_RouteMap.cshtml) with Leaflet + map tiles from
// FieldWork:MapTileUrl (OpenStreetMap by default). Numbered markers, green "S" start, red "E" end, a line between the
// points (suspicious legs dashed red), accuracy circles for low-accuracy points. Popup text is built with text nodes only.
(function () {
    'use strict';
    if (typeof L === 'undefined') return;

    function el(tag, cls, text) {
        var e = document.createElement(tag);
        if (cls) e.className = cls;
        if (text !== undefined && text !== null) e.textContent = text;
        return e;
    }

    function icon(p) {
        var label = p.kind === 'Start' ? 'S' : p.kind === 'End' ? 'E' : String(p.n);
        var cls = 'fw-pin ' + (p.kind === 'Start' ? 'fw-pin-start' : p.kind === 'End' ? 'fw-pin-end' : 'fw-pin-visit') + (p.low ? ' fw-pin-low' : '');
        var html = document.createElement('div');
        html.className = cls;
        html.textContent = label;
        return L.divIcon({ html: html.outerHTML, className: 'fw-pin-wrap', iconSize: [30, 30], iconAnchor: [15, 15], popupAnchor: [0, -14] });
    }

    function popup(p) {
        var box = el('div', 'fw-popup');
        var title = p.kind === 'Start' ? 'Start Day' : p.kind === 'End' ? 'End Day' : (p.customer || 'Visit');
        box.appendChild(el('div', 'fw-popup-title', p.n + '. ' + title));
        box.appendChild(el('div', 'fw-popup-time', p.time + (p.low ? ' · low GPS accuracy (±' + Math.round(p.acc) + ' m)' : '')));
        if (p.summary) box.appendChild(el('div', 'fw-popup-text', p.summary));
        if (p.url) {
            var a = el('a', null, 'Open customer');
            a.href = p.url;
            box.appendChild(a);
        }
        return box;
    }

    document.querySelectorAll('.fw-route-map').forEach(function (div) {
        var dataEl = document.getElementById(div.getAttribute('data-json-id'));
        if (!dataEl) return;
        var data;
        try { data = JSON.parse(dataEl.textContent); } catch (e) { return; }
        if (!data.points || !data.points.length) return;

        var map = L.map(div, { scrollWheelZoom: false, tap: true });
        L.tileLayer(div.getAttribute('data-tile-url'), {
            maxZoom: parseInt(div.getAttribute('data-max-zoom'), 10) || 19,
            attribution: div.getAttribute('data-attribution') || ''
        }).addTo(map);

        var byNumber = {};
        var bounds = [];
        data.points.forEach(function (p) {
            byNumber[p.n] = p;
            var ll = [p.lat, p.lng];
            bounds.push(ll);
            L.marker(ll, { icon: icon(p), title: (p.customer || p.kind) + ' ' + p.time, keyboard: true }).addTo(map).bindPopup(popup(p));
            if (p.low) L.circle(ll, { radius: p.acc, color: '#6c757d', weight: 1, fillOpacity: 0.08, dashArray: '4 4' }).addTo(map);
        });
        (data.legs || []).forEach(function (l) {
            var a = byNumber[l.from], b = byNumber[l.to];
            if (!a || !b) return;
            L.polyline([[a.lat, a.lng], [b.lat, b.lng]], l.suspicious
                ? { color: '#dc3545', weight: 4, dashArray: '8 8', opacity: 0.9 }
                : { color: '#0d6efd', weight: 4, opacity: 0.75 }).addTo(map);
        });
        if (bounds.length === 1) map.setView(bounds[0], 15);
        else map.fitBounds(bounds, { padding: [30, 30], maxZoom: 16 });
        // a map inside a collapsed / resized area needs to recompute its size
        window.addEventListener('resize', function () { map.invalidateSize(); });
        setTimeout(function () { map.invalidateSize(); }, 200);
    });
})();
