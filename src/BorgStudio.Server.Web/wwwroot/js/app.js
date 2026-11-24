(() => {
    const secure = () => (location.protocol === "https:" ? "; secure" : "");
    const setCookie = (name, value, maxAgeSeconds) => {
        document.cookie = `${name}=${encodeURIComponent(value)}; path=/; max-age=${maxAgeSeconds}; samesite=lax${secure()}`;
    };

    window.borgStudio = {
        // Remembers the color scheme for a year, so the next server render uses it right away.
        setThemePreference(name, value) {
            setCookie(name, value, 31536000);
        },

        // The clipboard API needs HTTPS (or localhost); plain HTTP in a LAN falls back to the old way.
        async copyText(text) {
            try {
                if (navigator.clipboard && window.isSecureContext) {
                    await navigator.clipboard.writeText(text);
                    return true;
                }
            } catch {
                // fall through
            }

            const area = document.createElement("textarea");
            area.value = text;
            area.setAttribute("readonly", "");
            area.style.position = "fixed";
            area.style.opacity = "0";
            document.body.appendChild(area);
            area.select();
            let copied = false;
            try {
                copied = document.execCommand("copy");
            } catch {
                copied = false;
            }

            area.remove();
            return copied;
        },
    };

    // The browser's own light/dark setting, so "use system setting" renders right on the server too.
    const dark = window.matchMedia("(prefers-color-scheme: dark)");
    const rememberSystemTheme = () => setCookie("borgstudio.system-dark", dark.matches ? "1" : "0", 31536000);
    rememberSystemTheme();
    dark.addEventListener("change", rememberSystemTheme);

    // Sessions without "stay signed in" end after a while without activity. Interactive pages do not
    // send requests on their own, so activity is reported now and then to keep the sign-in alive.
    const idleMinutes = Number(document.body.dataset.idleMinutes);
    if (!idleMinutes) {
        return;
    }

    const keepAliveEvery = 5 * 60 * 1000;
    let lastActivity = Date.now();
    let lastKeepAlive = Date.now();

    const onActivity = () => {
        lastActivity = Date.now();
        if (lastActivity - lastKeepAlive > keepAliveEvery) {
            lastKeepAlive = lastActivity;
            fetch("account/keepalive", { credentials: "same-origin" }).catch(() => { });
        }
    };

    for (const type of ["pointerdown", "keydown", "wheel", "touchstart"]) {
        window.addEventListener(type, onActivity, { passive: true });
    }

    setInterval(() => {
        if (Date.now() - lastActivity > idleMinutes * 60 * 1000) {
            location.href = "account/logout-idle";
        }
    }, 30 * 1000);
})();
