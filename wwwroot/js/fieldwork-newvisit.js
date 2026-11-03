// Field Work (2026-11-03): the one-step New Visit form (Pages/FieldWork/Visits/New.cshtml) -- "New customer" / "Existing
// customer" switch, the existing-customer search, and the duplicate-phone warning with "Use this existing customer
// instead". Text is inserted with textContent only. The server re-checks everything.
(function () {
    'use strict';
    var form = document.getElementById('fpForm');
    if (!form || !form.getAttribute('data-search-url')) return;

    var $ = function (id) { return document.getElementById(id); };
    var modeNew = $('nvModeNew'), modeExisting = $('nvModeExisting');
    var secNew = $('nvNew'), secExisting = $('nvExisting');
    var customerId = $('nvCustomerId'), selected = $('nvSelected'), selectedName = $('nvSelectedName'), selectedInfo = $('nvSelectedInfo');
    var searchBox = $('nvSearchBox'), search = $('nvSearch'), results = $('nvResults'), change = $('nvChange');
    var dup = $('nvDup'), phone = $('fcPhone'), alt = $('fcAltPhone');
    var searchUrl = form.getAttribute('data-search-url'), checkUrl = form.getAttribute('data-check-url');

    function showMode() {
        var isNew = modeNew.checked;
        secNew.classList.toggle('d-none', !isNew);
        secExisting.classList.toggle('d-none', isNew);
        if (!isNew && !customerId.value && search) search.focus();
    }
    modeNew.addEventListener('change', showMode);
    modeExisting.addEventListener('change', showMode);

    function select(id, name, info) {
        customerId.value = id;
        selectedName.textContent = name;
        selectedInfo.textContent = info || '';
        selected.classList.remove('d-none');
        searchBox.classList.add('d-none');
        results.innerHTML = '';
    }
    function useExisting(id, name, village) {
        modeExisting.checked = true;
        showMode();
        select(id, name, village);
        var server = $('nvServerDup');
        if (server) server.classList.add('d-none');
        if (dup) dup.classList.add('d-none');
        secExisting.scrollIntoView({ behavior: 'smooth', block: 'center' });
    }
    if (change) change.addEventListener('click', function () {
        customerId.value = '';
        selected.classList.add('d-none');
        searchBox.classList.remove('d-none');
        search.value = '';
        search.focus();
    });

    // ---- existing-customer search ------------------------------------------------------------------------------------
    var searchTimer = null;
    function runSearch() {
        var q = search.value.trim();
        fetch(searchUrl + (searchUrl.indexOf('?') >= 0 ? '&' : '?') + 'q=' + encodeURIComponent(q), { credentials: 'same-origin', headers: { 'Accept': 'application/json' } })
            .then(function (r) { return r.ok ? r.json() : []; })
            .then(function (rows) {
                results.innerHTML = '';
                if (!rows.length) {
                    var none = document.createElement('div');
                    none.className = 'list-group-item small text-muted';
                    none.textContent = q ? 'No customer of yours matches. Choose "New customer" to add one.' : 'Type to search your customers.';
                    results.appendChild(none);
                    return;
                }
                rows.forEach(function (c) {
                    var b = document.createElement('button');
                    b.type = 'button';
                    b.className = 'list-group-item list-group-item-action';
                    var t = document.createElement('div'); t.className = 'fw-step-title mb-0'; t.textContent = c.name;
                    var s = document.createElement('div'); s.className = 'small text-muted';
                    s.textContent = c.village + ' · ' + c.status + (c.followUp ? ' · follow-up ' + c.followUp : '');
                    b.appendChild(t); b.appendChild(s);
                    b.addEventListener('click', function () { select(c.id, c.name, c.village + ' · ' + c.status); });
                    results.appendChild(b);
                });
            })
            .catch(function () { /* the server re-checks the customer on save */ });
    }
    if (search) search.addEventListener('input', function () { clearTimeout(searchTimer); searchTimer = setTimeout(runSearch, 300); });

    // ---- duplicate phone warning --------------------------------------------------------------------------------------
    var dupTimer = null;
    function checkPhone() {
        var p = phone.value.trim(), a = alt ? alt.value.trim() : '';
        if (p.replace(/\D/g, '').length < 10 && a.replace(/\D/g, '').length < 10) { dup.classList.add('d-none'); return; }
        fetch(checkUrl + (checkUrl.indexOf('?') >= 0 ? '&' : '?') + 'phone=' + encodeURIComponent(p) + '&alt=' + encodeURIComponent(a),
            { credentials: 'same-origin', headers: { 'Accept': 'application/json' } })
            .then(function (r) { return r.ok ? r.json() : null; })
            .then(function (j) {
                if (!j || !j.warning) { dup.classList.add('d-none'); return; }
                dup.querySelector('[data-dup-text]').textContent = j.warning;
                var actions = dup.querySelector('[data-dup-actions]');
                actions.innerHTML = '';
                (j.matches || []).filter(function (m) { return m.usable; }).forEach(function (m) {
                    var b = document.createElement('button');
                    b.type = 'button';
                    b.className = 'btn btn-sm btn-primary';
                    b.textContent = 'Use this existing customer instead: ' + m.name;
                    b.addEventListener('click', function () { useExisting(m.id, m.name, m.village); });
                    actions.appendChild(b);
                });
                dup.classList.remove('d-none');
            })
            .catch(function () { /* the server re-checks on save */ });
    }
    if (phone && dup) {
        var later = function () { clearTimeout(dupTimer); dupTimer = setTimeout(checkPhone, 500); };
        phone.addEventListener('input', later);
        phone.addEventListener('blur', checkPhone);
        if (alt) { alt.addEventListener('input', later); alt.addEventListener('blur', checkPhone); }
    }

    // server-rendered duplicate warning (after a save attempt)
    document.querySelectorAll('[data-use-customer]').forEach(function (b) {
        b.addEventListener('click', function () { useExisting(b.getAttribute('data-use-customer'), b.getAttribute('data-name'), b.getAttribute('data-village')); });
    });
})();
