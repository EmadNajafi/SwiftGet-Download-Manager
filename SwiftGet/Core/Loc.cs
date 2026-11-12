using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;

namespace SwiftGet.Core;

/// <summary>Current UI language. Bindings to its indexer update live when the language changes.</summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static Loc I { get; } = new();

    Dictionary<string, string> _table = Strings.En;

    public string Language { get; private set; } = "en";
    public bool IsRtl => Language == "fa";
    public FlowDirection Flow => IsRtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

    public string this[string key] =>
        _table.TryGetValue(key, out var v) ? v :
        Strings.En.TryGetValue(key, out var e) ? e : key;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? LanguageChanged;

    /// <param name="language">"en", "fa", or empty for the Windows language.</param>
    public void SetLanguage(string? language)
    {
        var lang = string.IsNullOrEmpty(language) ? SystemLanguage : language;
        Language = lang == "fa" ? "fa" : "en";
        _table = Language == "fa" ? Strings.Fa : Strings.En;
        foreach (var name in new[] { "Item[]", nameof(Language), nameof(IsRtl), nameof(Flow) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        LanguageChanged?.Invoke();
    }

    public static string SystemLanguage =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fa" ? "fa" : "en";

    public static string T(string key) => I[key];
    public static string F(string key, params object?[] args) => string.Format(I[key], args);
}

/// <summary>XAML: Text="{l:Tr NewDownload}"</summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class TrExtension : MarkupExtension
{
    public TrExtension() { }
    public TrExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{Key}]") { Source = Loc.I, Mode = BindingMode.OneWay }.ProvideValue(serviceProvider);
}
