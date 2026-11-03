// Field Work (2026-11-03): live meter photo + GPS for Start Day / End Day (Pages/FieldWork/_ReadingCapture.cshtml).
// The photo comes ONLY from the live camera (getUserMedia); it is drawn on a canvas, shrunk to at most 1280 px and
// compressed to JPEG, then put into the hidden file input. There is no file picker, so the gallery cannot be used.
// Time is never taken from the phone: the server stamps every reading. The server re-checks everything.
(function () {
    'use strict';
    var form = document.getElementById('fwReadingForm');
    if (!form) return;

    var maxAccuracy = parseFloat(form.getAttribute('data-max-accuracy')) || 100;
    var maxBytes = parseInt(form.getAttribute('data-max-bytes'), 10) || 2 * 1024 * 1024;
    var MAX_EDGE = 1280;

    var $ = function (id) { return document.getElementById(id); };
    var video = $('fwVideo'), preview = $('fwPreview'), canvas = $('fwCanvas');
    var btnStart = $('fwStartCamera'), btnTake = $('fwTakePhoto'), btnRetake = $('fwRetake');
    var cameraStatus = $('fwCameraStatus'), photoInput = $('fwPhotoInput');
    var lat = $('fwLat'), lng = $('fwLng'), acc = $('fwAcc');
    var gpsBadge = $('fwGpsBadge'), gpsStatus = $('fwGpsStatus'), btnGps = $('fwRetryGps');
    var reasonBox = $('fwReasonBox'), reason = $('fwReason'), btnShowReason = $('fwShowReason');
    var submit = $('fwSubmit');

    var stream = null, hasPhoto = false, gpsOk = false, gpsPoor = false, watchId = null, gpsTimer = null;

    function showReason() { reasonBox.classList.remove('d-none'); btnShowReason.classList.add('d-none'); }
    function setCameraStatus(text, cls) { cameraStatus.textContent = text; cameraStatus.className = 'small mt-1 ' + (cls || ''); }
    function setGps(text, badgeText, badgeClass) {
        gpsStatus.textContent = text;
        gpsBadge.textContent = badgeText;
        gpsBadge.className = 'badge ' + badgeClass;
    }

    if (!window.isSecureContext) {
        setCameraStatus('Camera and GPS need a secure (https://) connection. Ask the administrator.', 'text-danger');
        setGps('GPS needs a secure (https://) connection.', 'No GPS', 'bg-danger');
        showReason();
    }

    // ---- camera ----------------------------------------------------------------------------------------------------
    function stopCamera() {
        if (stream) { stream.getTracks().forEach(function (t) { t.stop(); }); stream = null; }
    }

    function startCamera() {
        if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia) {
            setCameraStatus('This browser cannot open the camera. Give a reason below.', 'text-danger');
            showReason();
            return;
        }
        setCameraStatus('Opening camera…', 'text-muted');
        navigator.mediaDevices.getUserMedia({
            video: { facingMode: { ideal: 'environment' }, width: { ideal: 1920 }, height: { ideal: 1080 } },
            audio: false
        }).then(function (s) {
            stream = s;
            video.srcObject = s;
            video.classList.remove('d-none');
            preview.classList.add('d-none');
            btnStart.classList.add('d-none');
            btnRetake.classList.add('d-none');
            btnTake.classList.remove('d-none');
            setCameraStatus('Point the camera at the meter and tap "Take photo".', 'text-muted');
        }).catch(function (err) {
            var msg = err && err.name === 'NotAllowedError'
                ? 'Camera permission was refused. Allow the camera in the browser settings, or give a reason below.'
                : 'The camera could not be opened (' + (err && err.name ? err.name : 'error') + '). Give a reason below.';
            setCameraStatus(msg, 'text-danger');
            btnStart.classList.remove('d-none');
            btnStart.textContent = 'Try camera again';
            showReason();
        });
    }

    function toBlob(quality) {
        return new Promise(function (resolve) { canvas.toBlob(resolve, 'image/jpeg', quality); });
    }

    function takePhoto() {
        var w = video.videoWidth, h = video.videoHeight;
        if (!w || !h) { setCameraStatus('The camera is not ready yet. Try again.', 'text-warning'); return; }
        var scale = Math.min(1, MAX_EDGE / Math.max(w, h));
        canvas.width = Math.round(w * scale);
        canvas.height = Math.round(h * scale);
        canvas.getContext('2d').drawImage(video, 0, 0, canvas.width, canvas.height);

        // lower the quality until the file fits
        var qualities = [0.75, 0.6, 0.45, 0.3];
        (function attempt(i) {
            toBlob(qualities[i]).then(function (blob) {
                if (!blob) { setCameraStatus('The photo could not be created. Try again.', 'text-danger'); return; }
                if (blob.size > maxBytes && i + 1 < qualities.length) { attempt(i + 1); return; }
                if (blob.size > maxBytes) { setCameraStatus('The photo is too large. Try again.', 'text-danger'); return; }
                var file = new File([blob], 'meter.jpg', { type: 'image/jpeg' });
                try {
                    var dt = new DataTransfer();
                    dt.items.add(file);
                    photoInput.files = dt.files;
                } catch (e) {
                    setCameraStatus('This browser cannot attach the photo. Update the browser, or give a reason below.', 'text-danger');
                    showReason();
                    return;
                }
                hasPhoto = true;
                preview.src = URL.createObjectURL(blob);
                preview.classList.remove('d-none');
                video.classList.add('d-none');
                btnTake.classList.add('d-none');
                btnRetake.classList.remove('d-none');
                stopCamera();
                setCameraStatus('Photo ready (' + Math.round(blob.size / 1024) + ' KB). Check the reading is readable.', 'text-success');
            });
        })(0);
    }

    function retake() {
        hasPhoto = false;
        try { photoInput.value = ''; } catch (e) { /* ignore */ }
        startCamera();
    }

    btnStart.addEventListener('click', startCamera);
    btnTake.addEventListener('click', takePhoto);
    btnRetake.addEventListener('click', retake);
    btnShowReason.addEventListener('click', function () { showReason(); reason.focus(); });

    // ---- GPS (keeps the best fix for up to 30 s) ---------------------------------------------------------------------
    function stopGps() {
        if (watchId !== null) { navigator.geolocation.clearWatch(watchId); watchId = null; }
        if (gpsTimer) { clearTimeout(gpsTimer); gpsTimer = null; }
    }

    function startGps() {
        if (!('geolocation' in navigator)) {
            setGps('This browser has no GPS. Give a reason below.', 'No GPS', 'bg-danger');
            showReason();
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
            } else {
                setGps('Location found.', '± ' + Math.round(c.accuracy) + ' m', 'bg-success');
                if (c.accuracy <= 20) stopGps();
            }
        }, function (err) {
            if (gpsOk) return;
            var msg = err.code === 1
                ? 'Location permission was refused. Allow location for this site in the browser settings, then tap "Retry GPS" -- or give a reason.'
                : 'Location is not available (' + (err.message || 'timeout') + '). Tap "Retry GPS", or give a reason.';
            setGps(msg, 'No GPS', 'bg-danger');
            showReason();
            stopGps();
        }, { enableHighAccuracy: true, maximumAge: 0, timeout: 30000 });
        gpsTimer = setTimeout(stopGps, 30000);
    }

    btnGps.addEventListener('click', startGps);

    // ---- submit (the server re-checks every rule) --------------------------------------------------------------------
    form.addEventListener('submit', function (e) {
        var needReason = !hasPhoto || !gpsOk || gpsPoor;
        var hasReason = reason.value.trim().length > 0;
        var msgs = [];
        if (!hasPhoto && !hasReason) msgs.push('Take the meter photo (or give a reason).');
        if (!gpsOk && !hasReason) msgs.push('Wait for GPS (or give a reason).');
        if (gpsOk && gpsPoor && !hasReason) msgs.push('GPS accuracy is too poor. Retry GPS (or give a reason).');
        if (!form.querySelector('[name="Km"]').value.trim()) msgs.push('Enter the meter reading.');
        if (msgs.length) {
            e.preventDefault();
            if (needReason) showReason();
            var box = $('fwClientErrors');
            box.innerHTML = '';
            msgs.forEach(function (m) { var li = document.createElement('li'); li.textContent = m; box.appendChild(li); });
            box.parentElement.classList.remove('d-none');
            box.parentElement.scrollIntoView({ behavior: 'smooth', block: 'center' });
            return;
        }
        stopGps();
        submit.disabled = true;
        submit.textContent = 'Saving…';
    });

    window.addEventListener('pagehide', function () { stopCamera(); stopGps(); });

    if (window.isSecureContext) {
        startGps();
        startCamera();
    }
    if (reason.value.trim().length > 0) showReason();
})();
