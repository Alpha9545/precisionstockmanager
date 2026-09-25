
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



// Phase 1: supervisor dropdowns narrowed to the chosen Area.
// <select data-supervisor-area-source="#areaOrPoolSelect[, #otherPool]"> whose
// options carry data-areas="1,4" ("*" = every Area) and optionally data-kind.
// The Area comes from the first source select that has a value: its value when
// the source is an Area select (marked data-area-select), otherwise its chosen
// option's data-area-id (no attribute = no known Area = no narrowing). When that
// option carries data-area-kind, only supervisors of that kind are shown;
// kind-tagged options stay hidden until a source is chosen.
// Only narrows what is shown -- the server re-checks the choice on save.
(function () {
    function chosen(select) {
        var sources = document.querySelectorAll(select.dataset.supervisorAreaSource);
        for (var i = 0; i < sources.length; i++) {
            var s = sources[i];
            if (!s.value) continue;
            var opt = s.options ? s.options[s.selectedIndex] : null;
            return {
                areaId: s.hasAttribute("data-area-select") ? s.value : ((opt && opt.dataset.areaId) || null),
                kind: (opt && opt.dataset.areaKind) || null
            };
        }
        return { areaId: null, kind: null };
    }
    function narrow(select) {
        var c = chosen(select);
        Array.prototype.forEach.call(select.options, function (o) {
            if (o.dataset.areas === undefined) return; // placeholder option
            var ok = (!c.areaId || o.dataset.areas === "*" || o.dataset.areas.split(",").indexOf(String(c.areaId)) >= 0)
                  && (o.dataset.kind === undefined || (!!c.kind && o.dataset.kind === c.kind));
            o.hidden = !ok;
            o.disabled = !ok;
        });
        var sel = select.options[select.selectedIndex];
        if (sel && sel.disabled) select.value = "";
    }
    document.querySelectorAll("select[data-supervisor-area-source]").forEach(function (select) {
        document.querySelectorAll(select.dataset.supervisorAreaSource).forEach(function (s) {
            s.addEventListener("change", function () { narrow(select); });
        });
        narrow(select);
    });
})();
