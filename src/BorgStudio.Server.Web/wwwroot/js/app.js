window.borgStudio = {
    // Remembers the color scheme for a year, so the next server render uses it right away.
    setThemePreference(name, value) {
        const secure = location.protocol === "https:" ? "; secure" : "";
        document.cookie = `${name}=${encodeURIComponent(value)}; path=/; max-age=31536000; samesite=lax${secure}`;
    },
};
