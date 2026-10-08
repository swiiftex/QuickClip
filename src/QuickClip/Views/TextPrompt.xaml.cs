using System.Windows;

namespace QuickClip.Views;

/// <summary>Asks for a short piece of text, like a project's name.</summary>
public partial class TextPrompt : Window
{
    private TextPrompt()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            Input.Focus();
            Input.SelectAll();
        };
    }

    /// <summary>The text entered, or null if the user cancelled or left it empty.</summary>
    public static string? Ask(Window? owner, string prompt, string initial, string okText)
    {
        var dialog = new TextPrompt { Owner = owner };
        dialog.PromptText.Text = prompt;
        dialog.Input.Text = initial;
        dialog.OkButton.Content = okText;
        return dialog.ShowDialog() == true && dialog.Input.Text.Trim() is { Length: > 0 } text ? text : null;
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;

#if DEBUG
    /// <summary>Shows the prompt without waiting for an answer, for page renders; close it when done.</summary>
    internal static TextPrompt ShowForRender(Window owner, string prompt, string initial, string okText)
    {
        var dialog = new TextPrompt { Owner = owner };
        dialog.PromptText.Text = prompt;
        dialog.Input.Text = initial;
        dialog.OkButton.Content = okText;
        dialog.Show();
        return dialog;
    }
#endif
}
