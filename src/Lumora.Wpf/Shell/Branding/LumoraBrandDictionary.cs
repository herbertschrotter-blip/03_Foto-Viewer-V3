using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Wpf.Ui.Appearance;

namespace Lumora.Shell.Branding;

/// <summary>
/// Markenpinsel je Farbschema als App-Ressourcen (in <c>App.xaml</c> eingehängt): <see cref="LumoraBrand.WordmarkBrushKey"/>
/// und <see cref="LumoraBrand.LensRingBrushKey"/>. Folgt <see cref="ApplicationThemeManager.Changed"/> (Hell/Dunkel/System
/// samt <c>SystemThemeWatcher</c>) und tauscht dann die Einträge aus — Verbraucher binden per <c>DynamicResource</c>
/// bzw. <c>SetResourceReference</c> und färben sich ohne Neustart um.
/// </summary>
public sealed class LumoraBrandDictionary : ResourceDictionary
{
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;

    public LumoraBrandDictionary()
    {
        var theme = ApplicationThemeManager.GetAppTheme();
        Apply(theme);
        ApplicationThemeManager.Changed += OnThemeChanged; // lebt so lange wie die App-Ressourcen
        if (theme == ApplicationTheme.Unknown)
        {
            // Beim Laden von App.xaml ist das Theme-Wörterbuch evtl. noch nicht eingehängt: nach dem Start nachziehen,
            // auch falls danach kein Changed mehr kommt
            _dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => Apply(ApplicationThemeManager.GetAppTheme()));
        }
    }

    /// <summary>Vom Theme-Wechsel abmelden (nur für kurzlebige Instanzen, z. B. in Tests).</summary>
    public void Detach() => ApplicationThemeManager.Changed -= OnThemeChanged;

    /// <summary>Aktuell eingetragene Palette.</summary>
    public LumoraPalette Palette { get; private set; } = LumoraPalette.Light;

    /// <summary>Pinsel für das Farbschema eintragen.</summary>
    public void Apply(ApplicationTheme theme)
    {
        var palette = LumoraPalette.For(theme);
        if (ReferenceEquals(palette, Palette) && Contains(LumoraBrand.WordmarkBrushKey))
        {
            return;
        }

        Palette = palette;
        this[LumoraBrand.WordmarkBrushKey] = palette.CreateWordmarkBrush();
        this[LumoraBrand.LensRingBrushKey] = palette.CreateLensRingBrush();
    }

    private void OnThemeChanged(ApplicationTheme theme, Color accent)
    {
        if (_dispatcher.CheckAccess())
        {
            Apply(theme);
        }
        else
        {
            _dispatcher.BeginInvoke(() => Apply(theme));
        }
    }
}
