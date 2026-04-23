// Prosty helper do przełączania motywu. Motyw siedzi jako atrybut
// data-theme na <html> — dark/brak (jasny to default bez atrybutu).
// Trwały wybór trzymamy w localStorage pod kluczem "medytao.theme".
// Inline script w index.html aplikuje wybór zanim załaduje się CSS,
// dzięki czemu przy reloadzie nie widać "flash of wrong theme".
window.medytaoTheme = {
    apply(theme) {
        if (theme === 'dark') {
            document.documentElement.setAttribute('data-theme', 'dark');
        } else {
            document.documentElement.removeAttribute('data-theme');
        }
    },

    get() {
        return document.documentElement.getAttribute('data-theme') === 'dark'
            ? 'dark'
            : 'light';
    }
};
