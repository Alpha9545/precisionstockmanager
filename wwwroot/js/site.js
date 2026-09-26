
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
