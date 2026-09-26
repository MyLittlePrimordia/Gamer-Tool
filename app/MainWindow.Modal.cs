using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GamerTool.Models;
using GamerTool.Services;
using GamerTool.UI;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Brush = System.Windows.Media.Brush;
using FontFamily = System.Windows.Media.FontFamily;
using Color = System.Windows.Media.Color;

namespace GamerTool;

public partial class MainWindow : Window
{
    private Border _modalLayer = null!;


    private TextBox _modalInput = null!;


    private TextBlock _modalTitle = null!;


    private Action<string>? _modalAction;


    private Action? _modalConfirm;


    private StackPanel _modalWarning = null!;


    private TextBlock _modalWarningText = null!;


    private Button _modalSaveButton = null!;


    private void BuildModal()
    {
        _modalInput = new TextBox
        {
            Style = (Style)FindResource("ModernTextBox"),
            FontSize = 14,
            Margin = new Thickness(0, 16, 0, 0)
        };

        _modalTitle = new TextBlock
        {
            Style = (Style)FindResource("CardTitle"),
            FontSize = 16
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
            Margin = new Thickness(0, 18, 8, 0),
            MinWidth = 110
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

            string name = _modalInput.Text.Trim().ToUpperInvariant();
            if (name.Length == 0)
            {
                return;
            }

            if (name.Length > 28)
            {
                name = name.Substring(0, 28);
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
            MinWidth = 110
        };
        cancel.Click += (s, e) => CloseModal();

        Border card = new()
        {
            Style = (Style)FindResource("Card"),
            Width = 420,
            Padding = new Thickness(24),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = new StackPanel()
            {
                Children =
                {
                    _modalTitle,
                    _modalInput,
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
            Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x0D, 0x0F, 0x13)),
            Visibility = Visibility.Collapsed,
            Child = card
        };

        // Cover the whole shell so the scrim dims the page and the card centres.
        Grid.SetColumn(_modalLayer, 0);
        Grid.SetColumnSpan(_modalLayer, 1);
        Grid.SetRow(_modalLayer, 0);
        Grid.SetRowSpan(_modalLayer, 4);
        ShellGrid.Children.Add(_modalLayer);
    }


    private void ShowModal(string title, string initial, Action<string> action)
    {
        _modalTitle.Text = title;
        _modalInput.Text = initial;
        _modalInput.Visibility = Visibility.Visible;
        _modalWarning.Visibility = Visibility.Collapsed;
        _modalAction = action;
        _modalLayer.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(new Action(() => { _modalInput.Focus(); _modalInput.SelectAll(); }));
    }


    /// <summary>
    /// Destructive confirm. Centred amber warning triangle instead of a Windows
    /// message box so it matches the rest of the app.
    /// </summary>
    private void ShowConfirmModal(string title, string warning, string confirmText, Action action)
    {
        _modalTitle.Text = title;
        _modalInput.Visibility = Visibility.Collapsed;
        _modalWarning.Visibility = Visibility.Visible;
        _modalWarningText.Text = warning;
        _modalSaveButton.Content = confirmText;
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
