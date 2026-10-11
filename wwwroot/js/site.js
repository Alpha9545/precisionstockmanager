
let menuItems = document.querySelectorAll(".icon-link > a, .arrow");

menuItems.forEach(item => {
    item.addEventListener("click", (e) => {
        e.preventDefault(); // Prevent default only for anchor links with '#'
        let parentMenu = item.closest(".icon-link").parentElement; // Selects the <li> parent
        parentMenu.classList.toggle("showMenu");
    });
});




let sidebar = document.querySelector(".sidebar");
let sidebarBtn = document.querySelector(".menu-icon");
let mainContent = document.querySelector(".main-content");
let header = document.querySelector(".top-header");
let footer = document.querySelector(".footer");
let overlay = document.querySelector(".overlay");

// Sidebar toggle button (menu icon)
sidebarBtn.addEventListener("click", () => {
    if (window.innerWidth <= 768) {
        // On mobile, show full sidebar
        sidebar.classList.remove("close");
        sidebar.classList.add("mobile-open");
        overlay.classList.add("active");
    } else {
        // On desktop, toggle compact/expanded sidebar
        sidebar.classList.toggle("close");

        if (sidebar.classList.contains("close")) {
            mainContent.style.marginLeft = "78px";
            header.style.left = "78px";
            header.style.width = "calc(100% - 78px)";
            footer.style.left = "78px";
            footer.style.width = "calc(100% - 78px)";
        } else {
            mainContent.style.marginLeft = "260px";
            header.style.left = "260px";
            header.style.width = "calc(100% - 260px)";
            footer.style.left = "260px";
            footer.style.width = "calc(100% - 260px)";
        }
    }
});

// When header is clicked (mobile only), open full sidebar
header.addEventListener("click", (e) => {
    if (window.innerWidth <= 768) {
        // Avoid triggering on logout button click
        if (!e.target.closest("button") && !e.target.closest("form")) {
            sidebar.classList.remove("close");
            sidebar.classList.add("mobile-open");
            overlay.classList.add("active");
        }
    }
});

// Clicking outside sidebar closes it
overlay.addEventListener("click", () => {
    sidebar.classList.remove("mobile-open");
    overlay.classList.remove("active");
});

// Auto-close mobile sidebar on screen resize
window.addEventListener("resize", () => {
    if (window.innerWidth > 768) {
        sidebar.classList.remove("mobile-open");
        overlay.classList.remove("active");
    }
});


// Searchable dropdowns: <select data-searchable> gets a small "type to
// filter" box above it (long variety / stock lists). No external library.
(function () {
    function enhance(select) {
        if (select.dataset.searchReady) return;
        select.dataset.searchReady = "1";
        const box = document.createElement("input");
        box.type = "search";
        box.className = "form-control form-control-sm mb-1";
        box.placeholder = "Type to filter the list...";
        box.setAttribute("aria-label", "Filter list");
        select.parentNode.insertBefore(box, select);
        box.addEventListener("input", function () {
            const term = box.value.trim().toLowerCase();
            let firstMatch = null;
            Array.from(select.options).forEach(function (o) {
                const show = !o.value || !term || o.text.toLowerCase().indexOf(term) >= 0;
                o.hidden = !show;
                if (show && o.value && !firstMatch) firstMatch = o;
            });
            if (term && firstMatch && (select.selectedIndex < 0 || select.options[select.selectedIndex].hidden)) {
                select.value = firstMatch.value;
                select.dispatchEvent(new Event("change", { bubbles: true }));
            }
        });
    }
    document.querySelectorAll("select[data-searchable]").forEach(enhance);
    // Exposed so a page that clones a row with a data-searchable select at
    // runtime (e.g. "Add another item") can enhance the new one too.
    window.enhanceSearchable = enhance;
})();

// Quantity display rule, the browser-side twin of Services/QuantityFormat.Qty:
// no trailing ".00" (100.00 -> "100"), real fractions kept (100.5 -> "100.5",
// 12.25 -> "12.25"), thousands grouped the way the app's scripts already do
// ('en-IN'). Display only -- never feed the result back into a calculation or
// a number <input>, and never use it for money, rates or percentages.
window.formatQuantity = function (value, maxDecimals) {
    if (value === null || value === undefined || value === '') return '';
    const n = Number(value);
    if (isNaN(n)) return '';
    return n.toLocaleString('en-IN', { minimumFractionDigits: 0, maximumFractionDigits: maxDecimals === undefined ? 2 : maxDecimals });
};

// 2026-10-09: phone-friendly data tables (CSS: "table-stack" in site.css). Every
// table in the page body with ONE plain header row (no merged header cells), no
// cells spanning rows and at least 4 columns gets class "table-stack", and each
// cell gets data-label = its column's header, so on a phone each row reads as a
// card of "Column: value" lines. Opt out with class "no-stack" (on the table or
// a parent); pages' own mobile layouts (.for-desktop-records) are left alone.
// Rows a page script adds later are labelled too. Display only -- no data changes.
// 2026-10-10 accessibility: a table shown as cards (CSS display:block) loses its table
// semantics in Chrome and Safari, so screen readers stopped announcing rows, columns and
// headers. Every stacked table now carries the explicit ARIA table roles (the same as its
// native ones, so desktop reading is unchanged), and the header row is visually hidden in
// card view instead of display:none, so each cell keeps its column header for assistive
// technology. The visible "Column:" label is decorative (site.css) -- not read twice.
(function () {
    // Column labels from the header -- one row, or two rows with grouped (merged)
    // cells, e.g. "9:00 AM" over "Present" / "Absent" -> "9:00 AM · Present".
    function headerLabels(table) {
        const head = table.tHead;
        if (!head || head.rows.length < 1 || head.rows.length > 2) return null;
        const grid = [];
        Array.from(head.rows).forEach(function (row, r) {
            grid[r] = grid[r] || [];
            let col = 0;
            Array.from(row.cells).forEach(function (cell) {
                while (grid[r][col] !== undefined) col++;
                const text = (cell.innerText || cell.textContent || "").replace(/\s+/g, " ").trim();
                for (let dr = 0; dr < cell.rowSpan; dr++)
                    for (let dc = 0; dc < cell.colSpan; dc++) {
                        grid[r + dr] = grid[r + dr] || [];
                        grid[r + dr][col + dc] = text;
                    }
                col += cell.colSpan;
            });
        });
        const width = Math.max.apply(null, grid.map(function (g) { return g.length; }));
        if (width < 4) return null;
        const labels = [];
        for (let c = 0; c < width; c++) {
            const parts = [];
            grid.forEach(function (g) { const t = g[c]; if (t && parts.indexOf(t) < 0) parts.push(t); });
            labels.push(parts.join(" · "));
        }
        return labels;
    }
    function labelRow(row, labels) {
        let col = 0;
        row.setAttribute("role", "row");
        Array.from(row.cells).forEach(function (cell) {
            if (!cell.hasAttribute("role")) cell.setAttribute("role", cell.tagName === "TH" ? "rowheader" : "cell");
            if (cell.colSpan === 1 && !cell.hasAttribute("data-label") && labels[col]) cell.setAttribute("data-label", labels[col]);
            // A labelled cell with nothing in it is hidden in card view (e.g. the days of a month
            // grid without sowing), so a card shows only what has a value.
            const empty = cell.hasAttribute("data-label") && !(cell.textContent || "").trim()
                && !cell.querySelector("input, select, textarea, button, img, svg, a, i");
            cell.classList.toggle("stack-empty", empty);
            col += cell.colSpan;
        });
    }
    function applyRoles(table) {
        table.setAttribute("role", "table");
        [table.tHead, table.tFoot].concat(Array.from(table.tBodies)).forEach(function (g) { if (g) g.setAttribute("role", "rowgroup"); });
        if (table.tHead) Array.from(table.tHead.rows).forEach(function (r) {
            r.setAttribute("role", "row");
            Array.from(r.cells).forEach(function (c) { c.setAttribute("role", "columnheader"); });
        });
    }
    function labelAll(table, labels) {
        Array.from(table.tBodies).forEach(function (b) { Array.from(b.rows).forEach(function (r) { labelRow(r, labels); }); });
        if (table.tFoot) Array.from(table.tFoot.rows).forEach(function (r) { labelRow(r, labels); });
    }
    function enhance(table) {
        if (table.classList.contains("table-stack") || table.closest(".no-stack, .for-desktop-records") || !table.closest(".main-content")) return;
        if (table.querySelector(":scope > tbody > tr > [rowspan]:not([rowspan='1'])")) return;
        const labels = headerLabels(table);
        if (!labels) return;
        table.classList.add("table-stack");
        applyRoles(table);
        labelAll(table, labels);
        new MutationObserver(function () { labelAll(table, labels); }).observe(table, { childList: true, subtree: true });
    }
    function scan(root) { (root || document).querySelectorAll("table").forEach(enhance); }
    scan(document);
    const main = document.querySelector(".main-content");
    if (main) {
        new MutationObserver(function (mutations) {
            mutations.forEach(function (m) {
                m.addedNodes.forEach(function (n) {
                    if (n.nodeType !== 1) return;
                    if (n.tagName === "TABLE") enhance(n); else if (n.querySelectorAll) scan(n);
                });
            });
        }).observe(main, { childList: true, subtree: true });
    }
    window.enhanceTables = scan;
})();
