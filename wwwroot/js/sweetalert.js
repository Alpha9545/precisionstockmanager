function showCustomAlert(title, message, type, callback) {
    const overlay = document.createElement("div");
    overlay.className = "custom-swal-overlay";

    const swalDiv = document.createElement("div");
    swalDiv.className = "custom-swal";

    // Create SVG icons
    const successIcon = `<svg class="icon-success" fill="currentColor" viewBox="0 0 20 20"><path fill-rule="evenodd" d="M16.707 5.293a1 1 0 010 1.414l-8 8a1 1 0 01-1.414 0l-4-4a1 1 0 011.414-1.414L8 12.586l7.293-7.293a1 1 0 011.414 0z" clip-rule="evenodd"/></svg>`;
    const errorIcon = `<svg class="icon-error" fill="currentColor" viewBox="0 0 20 20"><path fill-rule="evenodd" d="M4.293 4.293a1 1 0 011.414 0L10 8.586l4.293-4.293a1 1 0 111.414 1.414L11.414 10l4.293 4.293a1 1 0 01-1.414 1.414L10 11.414l-4.293 4.293a1 1 0 01-1.414-1.414L8.586 10 4.293 5.707a1 1 0 010-1.414z" clip-rule="evenodd"/></svg>`;

    swalDiv.innerHTML = `
        <div class="swal-icon">
            ${type === "success" ? successIcon : errorIcon}
        </div>
        <div class="swal-header">${title}</div>
        <div class="swal-body">${message}</div>
        <div class="swal-footer">
            <button class="swal-confirm" aria-label="Confirm">OK</button>
        </div>
    `;

    overlay.appendChild(swalDiv);
    document.body.appendChild(overlay);

    // Add accessibility attributes
    swalDiv.setAttribute("role", "dialog");
    swalDiv.setAttribute("aria-labelledby", "swal-header");
    swalDiv.setAttribute("aria-modal", "true");

    // Animation trigger
    requestAnimationFrame(() => {
        overlay.classList.add("active");
        swalDiv.classList.add("active");
    });

    // Handle interactions
    const confirmBtn = swalDiv.querySelector(".swal-confirm");
    confirmBtn.focus();

    const closeModal = () => {
        overlay.classList.remove("active");
        swalDiv.classList.remove("active");
        setTimeout(() => {
            document.body.removeChild(overlay);
        }, 300);
    };

    const handleConfirm = () => {
        closeModal();
        if (callback) callback();
    };

    // Event listeners
    confirmBtn.addEventListener("click", handleConfirm);
    overlay.addEventListener("click", (e) => {
        if (e.target === overlay) closeModal();
    });
    document.addEventListener("keydown", (e) => {
        if (e.key === "Escape") closeModal();
        if (e.key === "Enter") handleConfirm();
    });
}