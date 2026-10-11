// Growing Material section of the sowing entry pages (Pages/Production/SeedSowing/_GrowingMaterial.cshtml).
// Shows the chosen material's stock in the chosen Polyhouse and what this sowing will use.
// A PREVIEW only: the server recalculates and deducts under lock when the sowing is saved.
window.initGrowingMaterial = function (opts) {
    // opts: url (page URL), area(), polyhouse(), cavity(), quantity(), triggers (selector of inputs that change the figures)
    const $radios = $('.gm-radio');
    let state = { applicable: false, trays: 0, materials: [] };

    function kg(v, unit) {
        return Number(v).toLocaleString('en-IN', { minimumFractionDigits: 2, maximumFractionDigits: 3 }) + ' ' + (unit || 'kg');
    }

    function render() {
        const selectedId = $radios.filter(':checked').val();
        const ready = opts.area() && opts.polyhouse();
        $('#gmNotApplicable').toggleClass('d-none', !ready || state.applicable);
        $('#gmChoices').toggleClass('d-none', ready && !state.applicable);
        $('#gmHint').toggleClass('d-none', ready && (!state.applicable || !!selectedId));
        $radios.prop('required', !ready || state.applicable);
        if (ready && !state.applicable) $radios.prop('checked', false);

        const m = state.materials.find(x => String(x.id) === String(selectedId));
        const $warn = $('#gmWarning').addClass('d-none').text('');
        if (!state.applicable || !m) { $('#gmFigures').addClass('d-none'); return; }

        $('#gmFigures').removeClass('d-none');
        $('#gmName').text(m.name);
        $('#gmAvailable').text(kg(m.available, m.unit));
        $('#gmTrays').text(state.trays > 0 ? state.trays : '-');
        $('#gmPerTray').text(m.perTray === null ? 'not configured' : kg(m.perTray, m.unit) + '/tray');
        $('#gmRequired').text(state.trays > 0 && m.perTray !== null ? kg(m.required, m.unit) : '-');
        $('#gmRemaining').text(state.trays > 0 && m.perTray !== null ? kg(m.remaining, m.unit) : '-');
        const $enough = $('#gmEnough').removeClass('text-success text-danger');
        if (m.perTray === null) {
            $enough.text('-');
            $warn.removeClass('d-none').text('No ' + m.name + ' consumption is configured for this tray size. Ask the administrator to set it in Material Stock > Settings.');
        } else if (state.trays <= 0) {
            $enough.text('-');
        } else if (m.enough) {
            $enough.text('YES').addClass('text-success');
        } else {
            $enough.text('NO').addClass('text-danger');
            $warn.removeClass('d-none').text('Insufficient ' + m.name + ' stock in this Polyhouse. Available: ' + kg(m.available, m.unit) + '. Required: ' + kg(m.required, m.unit) + '. The sowing will be refused.');
        }
    }

    let pending = null;
    function refresh() {
        const area = opts.area(), polyhouse = opts.polyhouse();
        if (!area || !polyhouse) { state = { applicable: false, trays: 0, materials: [] }; render(); return; }
        if (pending) pending.abort();
        pending = $.getJSON(opts.url, {
            handler: 'MaterialPreview', areaId: area, polyhouseId: polyhouse,
            cavityType: opts.cavity() || '', quantity: opts.quantity() || 0
        }, function (r) { state = r; render(); });
    }

    $(document).on('change input', opts.triggers, refresh);
    $radios.on('change', render);
    refresh();
    return { refresh: refresh };
};
