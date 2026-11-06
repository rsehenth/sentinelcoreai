using System.Windows;

namespace SentinelCore.App;

/// <summary>Estado de apresentação do menu lateral: grupo do item e modo recolhido (herdado pelos itens).</summary>
public static class Nav
{
    public static readonly DependencyProperty GroupProperty = DependencyProperty.RegisterAttached(
        "Group", typeof(string), typeof(Nav), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty CollapsedProperty = DependencyProperty.RegisterAttached(
        "Collapsed", typeof(bool), typeof(Nav), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    public static string GetGroup(DependencyObject element) => (string)element.GetValue(GroupProperty);
    public static void SetGroup(DependencyObject element, string value) => element.SetValue(GroupProperty, value);
    public static bool GetCollapsed(DependencyObject element) => (bool)element.GetValue(CollapsedProperty);
    public static void SetCollapsed(DependencyObject element, bool value) => element.SetValue(CollapsedProperty, value);
}
