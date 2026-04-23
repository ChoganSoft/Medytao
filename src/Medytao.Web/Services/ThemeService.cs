using Blazored.LocalStorage;
using Microsoft.JSInterop;

namespace Medytao.Web.Services;

public enum Theme { Light, Dark }

/// <summary>
/// Pamięta wybór motywu usera (localStorage) i aplikuje go do DOM przez
/// <c>window.medytaoTheme.apply</c>. Inline script w index.html robi pierwsze
/// nałożenie motywu zanim Blazor w ogóle wystartuje — dzięki temu przy
/// reloadzie ciemny motyw nie miga jasno. Serwis tylko utrzymuje stan
/// po stronie C# i reaguje na klik toggle.
///
/// Używamy synchronicznego wariantu localStorage (ISyncLocalStorageService)
/// — dzięki temu <see cref="Initialize"/> może być sync i dokonać wczytania
/// zanim Blazor wyrenderuje komponent po raz pierwszy. Gdyby init był async,
/// pierwszy render pokazywałby stare _current (Light), a klik przed ukończeniem
/// inicjalizacji robił flip z niewłaściwej bazy — dla zapisanego "dark" dałby
/// "Dark → Dark", czyli wizualnie nic.
/// </summary>
public sealed class ThemeService
{
    // Klucz w localStorage. Ten sam string czyta inline script w index.html
    // — jeśli go ruszysz, zmień też tam.
    private const string StorageKey = "medytao.theme";

    private readonly ISyncLocalStorageService _storage;
    private readonly IJSRuntime _js;
    private Theme _current = Theme.Light;
    private bool _initialized;

    public ThemeService(ISyncLocalStorageService storage, IJSRuntime js)
    {
        _storage = storage;
        _js = js;
    }

    /// <summary>Sygnalizuje zmianę motywu — komponenty UI odświeżają się.</summary>
    public event Action? OnChanged;

    public Theme Current => _current;
    public bool IsDark => _current == Theme.Dark;

    /// <summary>
    /// Odczytuje zapisany wybór i synchronizuje stan C# z DOM. Wywołanie
    /// wielokrotne jest no-opem. Sync, bo musi być gotowe przed pierwszym renderem.
    /// </summary>
    public void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        try
        {
            var stored = _storage.GetItem<string>(StorageKey);
            _current = string.Equals(stored, "dark", StringComparison.OrdinalIgnoreCase)
                ? Theme.Dark
                : Theme.Light;
        }
        catch
        {
            _current = Theme.Light;
        }
    }

    public Task ToggleAsync()
    {
        // Defensywnie — gdyby ktoś kliknął zanim MainLayout zdążył zainicjalizować.
        Initialize();
        return SetAsync(_current == Theme.Dark ? Theme.Light : Theme.Dark);
    }

    public async Task SetAsync(Theme theme)
    {
        Initialize();
        if (theme == _current) return;

        _current = theme;
        var value = theme == Theme.Dark ? "dark" : "light";

        try { _storage.SetItem(StorageKey, value); } catch { }
        try { await _js.InvokeVoidAsync("medytaoTheme.apply", value); } catch { }

        OnChanged?.Invoke();
    }
}
