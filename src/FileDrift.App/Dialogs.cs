// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Wpf.Ui.Controls;

namespace FileDrift.App;

/// <summary>Themed (WPF-UI) dialogs so every prompt matches the app's Fluent look instead of the
/// OS message box. All are modal over the main window and return a typed result.</summary>
public static class Dialogs
{
    /// <summary>A single-button informational dialog. Content may be a string or a built control.</summary>
    public static async Task InfoAsync(string title, object content)
    {
        var box = new Wpf.Ui.Controls.MessageBox
        {
            Title = title,
            Content = content,
            CloseButtonText = "OK",
        };
        SetOwner(box);
        await box.ShowDialogAsync();
    }

    /// <summary>A two-button confirmation. Returns true if the user chose the primary action.</summary>
    public static async Task<bool> ConfirmAsync(string title, object content, string confirmText,
        bool danger = false, string cancelText = "Cancel")
    {
        var box = new Wpf.Ui.Controls.MessageBox
        {
            Title = title,
            Content = content,
            PrimaryButtonText = confirmText,
            PrimaryButtonAppearance = danger ? ControlAppearance.Danger : ControlAppearance.Primary,
            CloseButtonText = cancelText,
        };
        SetOwner(box);
        return await box.ShowDialogAsync() == Wpf.Ui.Controls.MessageBoxResult.Primary;
    }

    /// <summary>A three-button choice. Returns Primary, Secondary, or None (close/escape).</summary>
    public static async Task<Wpf.Ui.Controls.MessageBoxResult> ChoiceAsync(string title, object content,
        string primaryText, string secondaryText, string closeText, bool primaryDanger = false)
    {
        var box = new Wpf.Ui.Controls.MessageBox
        {
            Title = title,
            Content = content,
            PrimaryButtonText = primaryText,
            PrimaryButtonAppearance = primaryDanger ? ControlAppearance.Danger : ControlAppearance.Primary,
            SecondaryButtonText = secondaryText,
            CloseButtonText = closeText,
        };
        SetOwner(box);
        return await box.ShowDialogAsync();
    }

    /// <summary>Builds a stacked content block: a wrapped question followed by bold-labelled choice lines
    /// (label – detail), matching the on-screen choices to the dialog buttons.</summary>
    public static StackPanel ChoiceContent(string question, params (string Label, string Detail)[] lines)
    {
        var panel = new StackPanel { MaxWidth = 380 };
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = question, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
        foreach (var (label, detail) in lines)
        {
            var tb = new System.Windows.Controls.TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2) };
            tb.Inlines.Add(new Run(label) { FontWeight = FontWeights.SemiBold });
            tb.Inlines.Add(new Run(" – " + detail));
            panel.Children.Add(tb);
        }
        return panel;
    }

    private static void SetOwner(Window box)
    {
        if (Application.Current?.MainWindow is { } main) box.Owner = main;
    }
}
