// Empty trays available panel of the sowing pages (Pages/Production/SeedSowing/_TrayAvailability.cshtml).
// Asks the page's TrayAvailability handler (server + database) whenever the cavity, the location or the quantity
// changes, lists every offered location's balance for that cavity, shows the chosen location's balance prominently,
// and blocks the submit (setCustomValidity on opts.guard) while the chosen location cannot cover the trays needed.
// A PREVIEW only: the server re-checks and deducts under lock when the entry is saved.
window.initTrayAvailability = function (opts) {
    // opts: url (page URL), params() -> query values for the handler, triggers (inputs that change the figures),
    //       guard() -> the input that must be corrected when the chosen location is short (quantity, or cavity)
    const fmt = n => Number(n).toLocaleString('en-IN');
    let seq = 0, timer = null;

    function setGuard(msg) {
        const el = opts.guard && opts.guard();
        if (el && el.setCustomValidity) el.setCustomValidity(msg || '');
    }

    function render(r) {
        const $body = $('#taTable tbody').empty();
        const $sel = $('#taSelected').addClass('d-none').removeClass('alert-danger alert-success alert-secondary').text('');
        $('#taCavity').text(r.ok ? '(' + r.cavity + ')' : '');
        $('#taMessage').toggleClass('d-none', !r.message).text(r.message || '');
        $('#taTableWrap').toggleClass('d-none', !r.ok || !r.rows.length);
        if (!r.ok) { setGuard(''); return; }
        r.rows.forEach(row => {
            const $tr = $('<tr>').attr('data-key', row.key).toggleClass('table-primary fw-semibold', row.key === r.selectedKey);
            $tr.append($('<td>').text(row.location), $('<td>').text(r.cavity),
                $('<td class="text-end">').text(fmt(row.available)).toggleClass('text-danger', row.available <= 0));
            $body.append($tr);
        });
        if (!r.selectedKey) { setGuard(''); return; }
        const short = r.needed > 0 && !r.enough;
        let text = r.selectedLocation + ': ' + fmt(r.selectedAvailable) + ' empty ' + r.cavity + ' trays available';
        if (r.needed > 0) text += ', ' + fmt(r.needed) + ' needed for this entry';
        text += short ? '. Not enough -- reduce the quantity, or choose another cavity or location.' : '.';
        if (r.selectedAvailable <= 0 && !(r.needed > 0)) text += ' None available here.';
        $sel.removeClass('d-none').addClass(short ? 'alert-danger' : r.needed > 0 ? 'alert-success' : 'alert-secondary').text(text);
        setGuard(short ? 'Only ' + fmt(r.selectedAvailable) + ' empty ' + r.cavity + ' trays at ' + r.selectedLocation + '; ' + fmt(r.needed) + ' needed.' : '');
    }

    function refresh() {
        const mine = ++seq;
        $('#taLoading').prop('hidden', false);
        $.getJSON(opts.url, Object.assign({ handler: 'TrayAvailability' }, opts.params()))
            .done(r => { if (mine === seq) render(r); })
            .fail(() => { if (mine === seq) { setGuard(''); $('#taMessage').removeClass('d-none').text('The empty-tray balances could not be loaded; they are still checked when you save.'); } })
            .always(() => { if (mine === seq) $('#taLoading').prop('hidden', true); });
    }

    $(opts.triggers).on('input change', function () { clearTimeout(timer); timer = setTimeout(refresh, 250); });
    refresh();
    return { refresh: refresh };
};
