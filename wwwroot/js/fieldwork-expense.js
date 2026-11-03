// Field Work Phase 4 (2026-11-03): ONE live bill photo for Add Expense (Pages/FieldWork/Expenses/Create.cshtml). The photo
// comes ONLY from the live camera (getUserMedia): drawn on a canvas, shrunk to at most 1600 px and compressed to JPEG, then
// put into the hidden <input name="Photo">. No file picker. The "photograph only the bill" reminder appears when the camera
// is opened. The server re-checks every rule.
(function () {
    'use strict';
    var form = document.getElementById('efForm');
    if (!form) return;

    var maxBytes = parseInt(form.getAttribute('data-max-bytes'), 10) || 2 * 1024 * 1024;
    var MAX_EDGE = 1600;   // bills have small print: a little larger than visit photos

    var $ = function (id) { return document.getElementById(id); };
    var video = $('efVideo'), canvas = $('efCanvas'), cameraBox = $('efCamera'), preview = $('efPreview');
    var btnStart = $('efStartCamera'), btnTake = $('efTakePhoto'), btnRetake = $('efRetake');
    var status = $('efCameraStatus'), input = $('efPhotoInput'), submit = $('efSubmit');
    var category = $('efCategory'), note = $('efNote'), amount = $('efAmount');
    var errorBox = $('efErrorBox'), errorList = $('efErrors');
    var stream = null, photo = null, previewUrl = null, sending = false;

    function setStatus(text, cls) { status.textContent = text; status.className = 'small mt-1 ' + (cls || ''); }
    function show(el, on) { if (el) el.classList.toggle('d-none', !on); }

    function stopCamera() {
        if (stream) { stream.getTracks().forEach(function (t) { t.stop(); }); stream = null; }
        show(cameraBox, false);
    }

    function render() {
        show(btnStart, !stream && !photo);
        show(btnTake, !!stream);
        show(btnRetake, !!photo && !stream);
        show(preview, !!photo && !stream);
    }

    function attach(file) {
        try {
            var dt = new DataTransfer();
            if (file) dt.items.add(file);
            input.files = dt.files;
            return true;
        } catch (e) {
            setStatus('This browser cannot attach the photo. Update the browser.', 'text-danger');
            return false;
        }
    }

    function startCamera() {
        show($('efReminder'), true);
        if (!window.isSecureContext || !navigator.mediaDevices || !navigator.mediaDevices.getUserMedia) {
            setStatus('The camera needs a secure (https://) connection and a current browser.', 'text-danger');
            return;
        }
        navigator.mediaDevices.getUserMedia({
            video: { facingMode: { ideal: 'environment' }, width: { ideal: 1920 }, height: { ideal: 1080 } }, audio: false
        }).then(function (s) {
            stream = s;
            video.srcObject = s;
            show(cameraBox, true);
            setStatus('Point the camera at the bill and tap "Take photo".', 'text-muted');
            render();
        }).catch(function (err) {
            setStatus(err && err.name === 'NotAllowedError'
                ? 'Camera permission was refused. Allow the camera in the browser settings: the bill photo is required.'
                : 'The camera could not be opened (' + (err && err.name ? err.name : 'error') + ').', 'text-danger');
        });
    }

    function toBlob(quality) { return new Promise(function (resolve) { canvas.toBlob(resolve, 'image/jpeg', quality); }); }

    function takePhoto() {
        var w = video.videoWidth, h = video.videoHeight;
        if (!w || !h) { setStatus('The camera is not ready yet. Try again.', 'text-warning'); return; }
        var scale = Math.min(1, MAX_EDGE / Math.max(w, h));
        canvas.width = Math.round(w * scale);
        canvas.height = Math.round(h * scale);
        canvas.getContext('2d').drawImage(video, 0, 0, canvas.width, canvas.height);
        var qualities = [0.8, 0.65, 0.5, 0.35];
        (function attempt(i) {
            toBlob(qualities[i]).then(function (blob) {
                if (!blob) { setStatus('The photo could not be created. Try again.', 'text-danger'); return; }
                if (blob.size > maxBytes && i + 1 < qualities.length) { attempt(i + 1); return; }
                if (blob.size > maxBytes) { setStatus('The photo is too large. Try again.', 'text-danger'); return; }
                var file = new File([blob], 'bill.jpg', { type: 'image/jpeg' });
                if (!attach(file)) return;
                photo = file;
                if (previewUrl) URL.revokeObjectURL(previewUrl);
                previewUrl = URL.createObjectURL(file);
                preview.src = previewUrl;
                stopCamera();
                setStatus('Bill photo taken. Check that it is readable, or retake it.', 'text-success');
                render();
            });
        })(0);
    }

    function retake() {
        photo = null;
        attach(null);
        startCamera();
        render();
    }

    function clientErrors() {
        var errors = [];
        if (!category.value) errors.push('Choose the category.');
        var a = (amount.value || '').replace(/,/g, '').trim();
        if (!a) errors.push('Enter the amount.');
        else if (!/^\d+(\.\d{1,2})?$/.test(a) || parseFloat(a) <= 0) errors.push('The amount must be a number above zero (for example 250 or 250.50).');
        if (category.value === 'Other' && !(note.value || '').trim()) errors.push('Write a note for an "Other" expense.');
        if (!photo) errors.push('Take a photo of the bill.');
        return errors;
    }

    btnStart.addEventListener('click', startCamera);
    btnTake.addEventListener('click', takePhoto);
    btnRetake.addEventListener('click', retake);
    window.addEventListener('pagehide', stopCamera);

    form.addEventListener('submit', function (ev) {
        if (sending) { ev.preventDefault(); return; }
        var errors = clientErrors();
        if (errors.length) {
            ev.preventDefault();
            errorList.textContent = '';
            errors.forEach(function (e) { var li = document.createElement('li'); li.textContent = e; errorList.appendChild(li); });
            show(errorBox, true);
            errorBox.scrollIntoView({ behavior: 'smooth', block: 'start' });
            return;
        }
        sending = true;
        submit.disabled = true;
        submit.textContent = 'Saving…';
    });

    render();
})();
