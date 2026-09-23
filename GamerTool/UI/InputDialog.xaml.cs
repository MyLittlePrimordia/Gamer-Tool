using System.Windows;
using System.Windows.Input;

namespace GamerTool.UI;

public partial class InputDialog : Window
{
    public string ResultText { get; private set; } = "";

    public InputDialog(string prompt, string defaultValue = "")
    {
        InitializeComponent();
        PromptText.Text = prompt;
        InputBox.Text = defaultValue;
        Loaded += (_, _) => { InputBox.Focus(); InputBox.SelectAll(); };
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        ResultText = InputBox.Text.Trim();
        DialogResult = !string.IsNullOrWhiteSpace(ResultText);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Ok_Click(sender, e);
        else if (e.Key == Key.Escape) Cancel_Click(sender, e);
    }

    /// <summary>Convenience static so callers don't juggle DialogResult themselves.</summary>
    public static string? Show(Window owner, string prompt, string defaultValue = "")
    {
        var dlg = new InputDialog(prompt, defaultValue) { Owner = owner };
        return dlg.ShowDialog() == true ? dlg.ResultText : null;
    }
}
