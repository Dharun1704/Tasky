using System.Windows;
using System.Windows.Controls;

namespace Tasky;

/// <summary>
/// One card in the settings window, Windows 11 Settings style: an icon, a title and a description,
/// with its control (the content) on the right, or below the text when <see cref="Wide"/>.
/// Drawn by the SettingRow style in SettingsWindow.xaml.
/// </summary>
public class SettingRow : ContentControl
{
    public static readonly DependencyProperty IconProperty =
        DependencyProperty.Register(nameof(Icon), typeof(string), typeof(SettingRow));
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(SettingRow));
    public static readonly DependencyProperty DescriptionProperty =
        DependencyProperty.Register(nameof(Description), typeof(string), typeof(SettingRow));
    public static readonly DependencyProperty WideProperty =
        DependencyProperty.Register(nameof(Wide), typeof(bool), typeof(SettingRow));

    /// <summary>A Segoe Fluent Icons glyph.</summary>
    public string? Icon { get => (string?)GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public string? Title { get => (string?)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string? Description { get => (string?)GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }
    /// <summary>Puts the control under the text, for lists of choices.</summary>
    public bool Wide { get => (bool)GetValue(WideProperty); set => SetValue(WideProperty, value); }
}
