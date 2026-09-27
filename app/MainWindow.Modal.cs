using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GamerTool.UI;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using Color = System.Windows.Media.Color;
using Brush = System.Windows.Media.Brush;

namespace GamerTool;

public partial class MainWindow : Window
{
    /// <summary>
    /// Longest a preset name may be. Chosen so the longest built-in still fits
    /// the narrowest dropdown it appears in, which is the screen and sound picker
    /// inside a slot row.
    /// </summary>
    public const int NameLimit = 18;


    private Border _modalLayer = null!;


    private TextBox _modalInput = null!;


    private TextBlock _modalTitle = null!;


    private Action<string>? _modalAction;


    private Action? _modalConfirm;


    private StackPanel _modalWarning = null!;


    private TextBlock _modalWarningText = null!;


    private TextBlock _modalCounter = null!;


    private Button _modalSaveButton = null!;


    private Button _modalCancelButton = null!;


    private void BuildModal()
    {
        _modalInput = new TextBox
        {
            Style = (Style)FindResource("ModernTextBox"),
            FontSize = 13,
            Height = 32,
            Margin = new Thickness(0, 14, 0, 0),

            // A real cap, not a cap applied on save. Paused, the caret sits
            // after the last character, and pasting is trimmed the same way, so
            // there is never a name on screen that is longer than the one saved.
            MaxLength = NameLimit
        };
        _modalInput.TextChanged += (s, e) => UpdateCounter();

        // Shown next to the box so hitting the cap is visible. Without it the
        // limit was silent: you kept typing and the name came out cut short.
        _modalCounter = new TextBlock
        {
            Style = (Style)FindResource("CardSub"),
            FontSize = 10,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 6, 2, 0),
            TextAlignment = TextAlignment.Right
        };

        _modalTitle = new TextBlock
        {
            Style = (Style)FindResource("CardTitle"),
            FontSize = 15
        };

        _modalWarning = new StackPanel
        {
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 14, 0, 0)
        };
        _modalWarning.Children.Add(new Border
        {
            Width = 44,
            Height = 40,
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromArgb(0x33, 0xF5, 0xA6, 0x23)),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M 22 3 L 41 35 L 3 35 Z"),
                Fill = new SolidColorBrush(Color.FromRgb(0xF5, 0xA6, 0x23)),
                Stretch = Stretch.Fill,
                Width = 26,
                Height = 22
            }
        });
        _modalWarningText = new TextBlock
        {
            Style = (Style)FindResource("CardSub"),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 10, 0, 0)
        };
        _modalWarning.Children.Add(_modalWarningText);

        Button save = new()
        {
            Content = "Save",
            Style = (Style)FindResource("PrimaryButton"),
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0),
            MinWidth = 104
        };
        _modalSaveButton = save;
        save.Click += (s, e) =>
        {
            if (_modalConfirm is not null)
            {
                Action confirm = _modalConfirm;
                CloseModal();
                confirm.Invoke();
                return;
            }

            // Preset names are shown as typed. The cap keeps a name short enough
            // to read in full inside the dropdowns, both here and in a slot row.
            string name = _modalInput.Text.Trim();
            if (name.Length == 0)
            {
                return;
            }

            if (name.Length > NameLimit)
            {
                name = name[..NameLimit];
            }

            Action<string>? action = _modalAction;
            CloseModal();
            action?.Invoke(name);
        };

        Button cancel = new()
        {
            Content = "Cancel",
            Style = (Style)FindResource("GhostButton"),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 18, 0, 0),
            MinWidth = 104
        };
        _modalCancelButton = cancel;
        cancel.Click += (s, e) => CloseModal();

        Border card = new()
        {
            Style = (Style)FindResource("Card"),
            Width = 420,
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(22),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = new StackPanel()
            {
                Children =
                {
                    _modalTitle,
                    _modalInput,
                    _modalCounter,
                    _modalWarning,
                    new Grid
                    {
                        Margin = new Thickness(0, 4, 0, 0),
                        Children = { cancel, _modalSaveButton }
                    }
                }
            }
        };

        _modalLayer = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xE0, 0, 0, 0)),
            Visibility = Visibility.Collapsed,
            Child = card
        };

        // Cover the whole shell so the scrim dims the page and the card centres.
        Grid.SetColumn(_modalLayer, 0);
        Grid.SetColumnSpan(_modalLayer, 1);
        Grid.SetRow(_modalLayer, 0);
        Grid.SetRowSpan(_modalLayer, 3);
        ShellGrid.Children.Add(_modalLayer);
    }


    private void ShowModal(string title, string initial, Action<string> action)
    {
        _modalTitle.Text = title;
        _modalInput.Text = initial;
        _modalInput.Visibility = Visibility.Visible;
        _modalCounter.Visibility = Visibility.Visible;
        _modalWarning.Visibility = Visibility.Collapsed;
        _modalCancelButton.Content = "Cancel";
        _modalSaveButton.Content = "Save";
        _modalSaveButton.Style = (Style)FindResource("PrimaryButton");
        _modalSaveButton.IsEnabled = true;
        _modalAction = action;
        _modalConfirm = null;
        _modalLayer.Visibility = Visibility.Visible;
        UpdateCounter();
        Dispatcher.BeginInvoke(new Action(() => { _modalInput.Focus(); _modalInput.SelectAll(); }));
    }


    /// <summary>
    /// "7/18" under the box, turning amber and reading "18 of 18" the moment the
    /// cap is reached, so the limit is something you see rather than something
    /// that quietly happens to you. Counted raw, exactly as the box counts it, so
    /// the number never disagrees with where typing actually stops.
    /// </summary>
    private void UpdateCounter()
    {
        int used = _modalInput.Text.Length;
        bool full = used >= NameLimit;

        _modalCounter.Text = full
            ? NameLimit + " of " + NameLimit + " - limit reached"
            : used + "/" + NameLimit;
        _modalCounter.Foreground = full
            ? new SolidColorBrush(Color.FromRgb(0xF5, 0xA6, 0x23))
            : (Brush)FindResource("TextLow");
    }


    /// <summary>
    /// Destructive confirm. Centred amber warning triangle instead of a Windows
    /// message box so it matches the rest of the app. The second button is
    /// "Cancel" for anything the user can simply back out of, and "Later" for the
    /// FxSound prompt, where declining is a normal choice rather than a cancel.
    /// </summary>
    private void ShowConfirmModal(string title, string warning, string confirmText, Action action, string? declineText = null)
    {
        _modalTitle.Text = title;
        _modalInput.Visibility = Visibility.Collapsed;
        _modalCounter.Visibility = Visibility.Collapsed;
        _modalWarning.Visibility = Visibility.Visible;
        _modalWarningText.Text = warning;
        _modalSaveButton.Content = confirmText;
        _modalCancelButton.Content = declineText ?? "Cancel";
        _modalAction = null;
        _modalConfirm = action;
        _modalLayer.Visibility = Visibility.Visible;
    }


    private void CloseModal()
    {
        _modalLayer.Visibility = Visibility.Collapsed;
        _modalAction = null;
        _modalConfirm = null;
        _modalSaveButton.Content = "Save";
    }

}
