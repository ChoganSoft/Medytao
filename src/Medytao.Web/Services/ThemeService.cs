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
/// </summary>
public sealed class ThemeService
{
    // Klucz w localStorage. Ten sam string czyta inline script w index.html
    // — jeśli go ruszysz, zmień też tam.
    private const string StorageKey = "medytao.theme";

    private readonly ILocalStorageService _storage;
    private readonly IJSRuntime _js;
    private Theme _current = Theme.Light;
    private bool _initialized;

    public ThemeService(ILocalStorageService storage, IJSRuntime js)
    {
        _storage = storage;
        _js = js;
    }

    /// <summary>Sygnalizuje zmianę motywu — komponenty UI odświeżają się.</summary>
    public event Action? OnChanged;

    public Theme Current => _current;
    public bool IsDark => _current == Theme.Dark;

    /// <summary>
    /// Odczytuje zapisany wybór i synchronizuje stan C# z tym, co pokazuje DOM.
    /// Nie aplikuje do DOM — to już zrobił inline script w index.html jeszcze
    /// przed startem Blazora.
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;
        try
        {
            var stored = await _storage.GetItemAsync<string>(StorageKey);
            _current = string.Equals(stored, "dark", StringComparison.OrdinalIgnoreCase)
                ? Theme.Dark
                : Theme.Light;
        }
        catch
        {
            // Jeśli storage niedostępne — zostaje Light (domyślny).
            _current = Theme.Light;
        }
    }

    public Task ToggleAsync() =>
        SetAsync(_current == Theme.Dark ? Theme.Light : Theme.Dark);

    public async Task SetAsync(Theme theme)
    {
        if (theme == _current && _initialized) return;

        _current = theme;
        var value = theme == Theme.Dark ? "dark" : "light";

        try { await _storage.SetItemAsync(StorageKey, value); } catch { }
        try { await _js.InvokeVoidAsync("medytaoTheme.apply", value); } catch { }

        OnChanged?.Invoke();
    }
}
