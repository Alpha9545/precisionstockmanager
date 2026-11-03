// Field Work Phase 2 (2026-11-03): GPS + up to N live camera photos for Add Customer and Log Visit
// (Pages/FieldWork/_PhotosCapture.cshtml). Photos come ONLY from the live camera (getUserMedia): each is drawn on a canvas,
// shrunk to at most 1280 px and compressed to JPEG, then all are put into the hidden <input name="Photos">. There is no
// file picker. Time is never taken from the phone; the server re-checks every rule.
(function () {
    'use strict';
    var form = document.getElementById('fpForm');
    if (!form) return;

    var maxAccuracy = parseFloat(form.getAttribute('data-max-accuracy')) || 100;
    var maxBytes = parseInt(form.getAttribute('data-max-bytes'), 10) || 2 * 1024 * 1024;
    var maxPhotos = parseInt(form.getAttribute('data-max-photos'), 10);
    if (isNaN(maxPhotos)) maxPhotos = 5;
    var gpsRequired = form.getAttribute('data-gps-required') === 'true';
    var MAX_EDGE = 1280;

    var $ = function (id) { return document.getElementById(id); };
    var video = $('fpVideo'), canvas = $('fpCanvas'), thumbs = $('fpThumbs');
    var btnStart = $('fpStartCamera'), btnTake = $('fpTakePhoto'), btnDone = $('fpDoneCamera');
    var cameraStatus = $('fpCameraStatus'), photoInput = $('fpPhotoInput'), cameraBox = $('fpCamera');
    var lat = $('fpLat'), lng = $('fpLng'), acc = $('fpAcc');
    var gpsBadge = $('fpGpsBadge'), gpsStatus = $('fpGpsStatus'), btnGps = $('fpRetryGps');
    var reasonBox = $('fpReasonBox'), reason = $('fpReason'), btnShowReason = $('fpShowReason');
    var submit = $('fpSubmit');

    var stream = null, files = [], gpsOk = false, gpsPoor = false, watchId = null, gpsTimer = null;

    function showReason() { if (reasonBox) { reasonBox.classList.remove('d-none'); } if (btnShowReason) btnShowReason.classList.add('d-none'); }
    function setCameraStatus(text, cls) { if (cameraStatus) { cameraStatus.textContent = text; cameraStatus.className = 'small mt-1 ' + (cls || ''); } }
    function setGps(text, badgeText, badgeClass) { gpsStatus.textContent = text; gpsBadge.textContent = badgeText; gpsBadge.className = 'badge ' + badgeClass; }

    // ---- photos ------------------------------------------------------------------------------------------------------
    function syncInput() {
        try {
            var dt = new DataTransfer();
            files.forEach(function (f) { dt.items.add(f); });
            photoInput.files = dt.files;
            return true;
        } catch (e) {
            setCameraStatus('This browser cannot attach photos. Update the browser.', 'text-danger');
            return false;
        }
    }

    function renderThumbs() {
        if (!thumbs) return;
        thumbs.innerHTML = '';
        files.forEach(function (f, i) {
            var wrap = document.createElement('div');
            wrap.className = 'position-relative';
            var img = document.createElement('img');
            img.className = 'fw-thumb';
            img.alt = 'Photo ' + (i + 1);
            img.src = URL.createObjectURL(f);
            var del = document.createElement('button');
            del.type = 'button';
            del.className = 'btn btn-sm btn-danger position-absolute top-0 end-0 py-0 px-1';
            del.textContent = '×';
            del.setAttribute('aria-label', 'Remove photo ' + (i + 1));
            del.addEventListener('click', function () { files.splice(i, 1); syncInput(); renderThumbs(); updateButtons(); });
            wrap.appendChild(img);
            wrap.appendChild(del);
            thumbs.appendChild(wrap);
        });
    }

    function updateButtons() {
        var full = files.length >= maxPhotos;
        if (btnStart) btnStart.classList.toggle('d-none', !!stream || full || maxPhotos === 0);
        if (btnTake) btnTake.classList.toggle('d-none', !stream || full);
        if (btnDone) btnDone.classList.toggle('d-none', !stream);
        if (full && stream) stopCamera();
        if (maxPhotos > 0)
            setCameraStatus(files.length + ' of ' + maxPhotos + ' photo(s).' + (full ? ' Maximum reached.' : ''), 'text-muted');
    }

    function stopCamera() {
        if (stream) { stream.getTracks().forEach(function (t) { t.stop(); }); stream = null; }
        if (cameraBox) cameraBox.classList.add('d-none');
        updateButtons();
    }

    function startCamera() {
        // the "take the customer's permission" reminder appears when the camera is opened
        var consent = $('fpConsent');
        if (consent) consent.classList.remove('d-none');
        if (!window.isSecureContext || !navigator.mediaDevices || !navigator.mediaDevices.getUserMedia) {
            setCameraStatus('The camera needs a secure (https://) connection and a current browser.', 'text-danger');
            return;
        }
        navigator.mediaDevices.getUserMedia({
            video: { facingMode: { ideal: 'environment' }, width: { ideal: 1920 }, height: { ideal: 1080 } }, audio: false
        }).then(function (s) {
            stream = s;
            video.srcObject = s;
            if (cameraBox) cameraBox.classList.remove('d-none');
            updateButtons();
        }).catch(function (err) {
            setCameraStatus(err && err.name === 'NotAllowedError'
                ? 'Camera permission was refused. Allow the camera in the browser settings (photos are optional).'
                : 'The camera could not be opened (' + (err && err.name ? err.name : 'error') + '). Photos are optional.', 'text-danger');
        });
    }

    function toBlob(quality) { return new Promise(function (resolve) { canvas.toBlob(resolve, 'image/jpeg', quality); }); }

    function takePhoto() {
        var w = video.videoWidth, h = video.videoHeight;
        if (!w || !h) { setCameraStatus('The camera is not ready yet. Try again.', 'text-warning'); return; }
        var scale = Math.min(1, MAX_EDGE / Math.max(w, h));
        canvas.width = Math.round(w * scale);
        canvas.height = Math.round(h * scale);
        canvas.getContext('2d').drawImage(video, 0, 0, canvas.width, canvas.height);
        var qualities = [0.75, 0.6, 0.45, 0.3];
        (function attempt(i) {
            toBlob(qualities[i]).then(function (blob) {
                if (!blob) { setCameraStatus('The photo could not be created. Try again.', 'text-danger'); return; }
                if (blob.size > maxBytes && i + 1 < qualities.length) { attempt(i + 1); return; }
                if (blob.size > maxBytes) { setCameraStatus('The photo is too large. Try again.', 'text-danger'); return; }
                files.push(new File([blob], 'photo' + (files.length + 1) + '.jpg', { type: 'image/jpeg' }));
                if (!syncInput()) { files.pop(); return; }
                renderThumbs();
                updateButtons();
            });
        })(0);
    }

    if (btnStart) btnStart.addEventListener('click', startCamera);
    if (btnTake) btnTake.addEventListener('click', takePhoto);
    if (btnDone) btnDone.addEventListener('click', stopCamera);
    if (btnShowReason) btnShowReason.addEventListener('click', function () { showReason(); reason.focus(); });

    // ---- GPS (keeps the best fix for up to 30 s) ---------------------------------------------------------------------
    function stopGps() {
        if (watchId !== null) { navigator.geolocation.clearWatch(watchId); watchId = null; }
        if (gpsTimer) { clearTimeout(gpsTimer); gpsTimer = null; }
    }

    function startGps() {
        if (!window.isSecureContext || !('geolocation' in navigator)) {
            setGps('GPS needs a secure (https://) connection.' + (gpsRequired ? ' Give a reason below.' : ''), 'No GPS', 'bg-danger');
            if (gpsRequired) showReason();
            return;
        }
        stopGps();
        gpsOk = false;
        setGps('Getting your location…', 'Waiting for GPS…', 'bg-secondary');
        var best = null;
        watchId = navigator.geolocation.watchPosition(function (pos) {
            var c = pos.coords;
            if (best && c.accuracy >= best.accuracy) return;
            best = c;
            lat.value = c.latitude.toFixed(6);
            lng.value = c.longitude.toFixed(6);
            acc.value = Math.max(0.1, c.accuracy).toFixed(1);
            gpsOk = true;
            gpsPoor = c.accuracy > maxAccuracy;
            if (gpsPoor) {
                setGps('Accuracy ' + Math.round(c.accuracy) + ' m is too poor (at most ' + maxAccuracy + ' m). Move to open sky and tap "Retry GPS", or give a reason.',
                    '± ' + Math.round(c.accuracy) + ' m', 'bg-warning text-dark');
                showReason();
            } else {
                setGps('Location found.', '± ' + Math.round(c.accuracy) + ' m', 'bg-success');
                if (c.accuracy <= 20) stopGps();
            }
        }, function (err) {
            if (gpsOk) return;
            setGps(err.code === 1
                ? 'Location permission was refused. Allow location for this site, then tap "Retry GPS"' + (gpsRequired ? ' -- or give a reason.' : '.')
                : 'Location is not available (' + (err.message || 'timeout') + '). Tap "Retry GPS"' + (gpsRequired ? ', or give a reason.' : '.'),
                'No GPS', 'bg-danger');
            if (gpsRequired) showReason();
            stopGps();
        }, { enableHighAccuracy: true, maximumAge: 0, timeout: 30000 });
        gpsTimer = setTimeout(stopGps, 30000);
    }

    btnGps.addEventListener('click', startGps);

    // ---- submit (the server re-checks every rule) --------------------------------------------------------------------
    form.addEventListener('submit', function (e) {
        var hasReason = reason && reason.value.trim().length > 0;
        var msgs = [];
        if (gpsRequired && !gpsOk && !hasReason) msgs.push('Wait for GPS (or give a reason).');
        if (gpsOk && gpsPoor && !hasReason) msgs.push('GPS accuracy is too poor. Retry GPS (or give a reason).');
        if (msgs.length) {
            e.preventDefault();
            showReason();
            var box = $('fpClientErrors');
            box.innerHTML = '';
            msgs.forEach(function (m) { var li = document.createElement('li'); li.textContent = m; box.appendChild(li); });
            box.parentElement.classList.remove('d-none');
            box.parentElement.scrollIntoView({ behavior: 'smooth', block: 'center' });
            return;
        }
        stopGps();
        if (submit) { submit.disabled = true; submit.textContent = 'Saving…'; }
    });

    window.addEventListener('pagehide', function () { stopCamera(); stopGps(); });

    updateButtons();
    startGps();
    if (reason && reason.value.trim().length > 0) showReason();
})();

// ---- visit items: "Add item" rows -------------------------------------------------------------------------------------
(function () {
    'use strict';
    var list = document.getElementById('fpItems'), add = document.getElementById('fpAddItem'), tpl = document.getElementById('fpItemTemplate');
    if (!list || !add || !tpl) return;
    var max = parseInt(list.getAttribute('data-max-items'), 10) || 20;
    function wire(row) {
        var del = row.querySelector('[data-remove-item]');
        if (del) del.addEventListener('click', function () { row.remove(); add.disabled = false; });
    }
    Array.prototype.forEach.call(list.children, wire);
    add.addEventListener('click', function () {
        if (list.children.length >= max) { add.disabled = true; return; }
        var row = tpl.content.firstElementChild.cloneNode(true);
        list.appendChild(row);
        wire(row);
        var first = row.querySelector('input');
        if (first) first.focus();
        if (list.children.length >= max) add.disabled = true;
    });
})();

// ---- duplicate phone warning while typing (Add / Edit customer; the server re-checks on save) ------------------------
(function () {
    'use strict';
    var phone = document.getElementById('fcPhone'), alt = document.getElementById('fcAltPhone'), box = document.getElementById('fcDupWarning');
    if (!phone || !box) return;
    var url = box.getAttribute('data-check-url');
    if (!url) return;   // a form with its own duplicate check (New Visit: fieldwork-newvisit.js)
    var timer = null;
    function check() {
        var p = phone.value.trim(), a = alt ? alt.value.trim() : '';
        if (p.replace(/\D/g, '').length < 10 && a.replace(/\D/g, '').length < 10) { box.classList.add('d-none'); return; }
        var q = url + (url.indexOf('?') >= 0 ? '&' : '?') + 'phone=' + encodeURIComponent(p) + '&alt=' + encodeURIComponent(a);
        fetch(q, { credentials: 'same-origin', headers: { 'Accept': 'application/json' } })
            .then(function (r) { return r.ok ? r.json() : null; })
            .then(function (j) {
                if (j && j.warning) { box.querySelector('[data-dup-text]').textContent = j.warning; box.classList.remove('d-none'); }
                else box.classList.add('d-none');
            })
            .catch(function () { /* the server re-checks on save */ });
    }
    function later() { clearTimeout(timer); timer = setTimeout(check, 500); }
    phone.addEventListener('input', later);
    phone.addEventListener('blur', check);
    if (alt) { alt.addEventListener('input', later); alt.addEventListener('blur', check); }
})();
