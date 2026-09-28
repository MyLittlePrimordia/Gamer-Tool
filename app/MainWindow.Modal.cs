using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using GamerTool.Services;
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

    /// <summary>Body text for a dialog that explains rather than asks.</summary>
    private TextBlock _modalBody = null!;


    private Action<string>? _modalAction;


    private Action? _modalConfirm;


    private StackPanel _modalWarning = null!;


    private TextBlock _modalWarningText = null!;


    private TextBlock _modalCounter = null!;


    private Button _modalSaveButton = null!;


    private Button _modalCancelButton = null!;


    /// <summary>Second action on the result dialog, where Cancel is the wrong name.</summary>
    private Action? _modalSecondary;


    /// <summary>
    /// When set, the confirm button runs its action without closing the dialog.
    /// That is what lets the FxSound install own the modal for its whole run
    /// instead of the dialog vanishing the instant the work starts.
    /// </summary>
    private bool _modalKeepOpen;


    private TextBlock _modalStatus = null!;


    private Grid _modalBarHost = null!;


    private Border _modalBarTrack = null!;


    private Border _modalBarFill = null!;


    private TranslateTransform _modalBarSlide = null!;


    private bool _modalBarAnimating;


    /// <summary>
    /// The phase being shown, kept so it can be re-applied once the bar has been
    /// measured. The first phase lands before layout has run and would otherwise
    /// be the one phase that never appears.
    /// </summary>
    private SetupStage _modalLastStage = new();


    /// <summary>
    /// Ticks once a second while a run is in progress, to keep the elapsed time on
    /// the phase line current.
    /// </summary>
    private DispatcherTimer? _modalElapsed;


    /// <summary>When the phase on screen last changed, for the elapsed readout.</summary>
    private DateTime _modalStageSince = DateTime.MinValue;


    /// <summary>Named so the result state can drop it and keep just the message.</summary>
    private Border _modalWarningIcon = null!;


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
            FontSize = 15,
            TextAlignment = TextAlignment.Center
        };

        // The body of a dialog that explains rather than asks. Hidden by every
        // other dialog, and left aligned because a numbered list read centred is
        // noticeably harder to follow.
        // <para>
        // Wrapping is what stops it clipping. Without it a paragraph of body text
        // is a single line as wide as it likes, and the card, which is a fixed
        // 420 wide, simply cuts the end off every sentence. Sentences that stop
        // mid-word at the right edge of a dialog are worse than no dialog.
        // </para>
        _modalBody = new TextBlock
        {
            Style = (Style)FindResource("CardSub"),
            FontSize = 11.5,
            TextAlignment = TextAlignment.Left,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 19,
            Margin = new Thickness(0, 12, 0, 0),
            Visibility = Visibility.Collapsed
        };

        _modalWarning = new StackPanel
        {
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 14, 0, 0)
        };
        // The caution glyph, drawn rather than set as text: a colour emoji font
        // would be the one place this app's type depends on a font that may not be
        // there, and the triangle has to sit sharp at any DPI. Just the triangle
        // and its mark, with no plate behind it.
        Grid caution = new()
        {
            Width = 44,
            Height = 40,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        caution.Children.Add(new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M 22 3 L 41 35 L 3 35 Z"),
            Fill = new SolidColorBrush(Color.FromRgb(0xF5, 0xA6, 0x23)),
            Stretch = Stretch.Fill,
            Width = 34,
            Height = 30,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });

        caution.Children.Add(new TextBlock
        {
            Text = "!",
            FontSize = 16,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x1C, 0x14, 0x04)),

            // The triangle's middle of mass sits below the middle of its bounding
            // box, because the point is at the top. Nudged down to match.
            Margin = new Thickness(0, 8, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });

        _modalWarningIcon = new Border
        {
            Width = 44,
            Height = 40,
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = caution
        };
        _modalWarning.Children.Add(_modalWarningIcon);
        _modalWarningText = new TextBlock
        {
            Style = (Style)FindResource("CardSub"),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 10, 0, 0)
        };
        _modalWarning.Children.Add(_modalWarningText);

        // Progress state. The phase line is what turns a silent wait into a
        // legible one, and the bar is there so a long phase still looks like
        // movement rather than a stuck dialog.
        _modalStatus = new TextBlock
        {
            Style = (Style)FindResource("CardSub"),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 16, 0, 0),
            Visibility = Visibility.Collapsed
        };

        _modalBarSlide = new TranslateTransform();
        _modalBarTrack = new Border
        {
            Height = 6,
            CornerRadius = new CornerRadius(3),
            Background = (Brush)FindResource("Groove")
        };
        _modalBarFill = new Border
        {
            Height = 6,
            CornerRadius = new CornerRadius(3),
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = 0,
            Background = (Brush)FindResource("AccentAudio"),
            RenderTransform = _modalBarSlide
        };
        _modalBarHost = new Grid
        {
            Height = 6,
            Margin = new Thickness(0, 12, 0, 0),
            Visibility = Visibility.Collapsed,

            // The sweeping fill is a sibling of the track, not a child of it, so
            // without this it is free to slide straight past the end of the groove
            // and sit out in the card. Clipped to the host, it cannot.
            ClipToBounds = true,
            Children = { _modalBarTrack, _modalBarFill }
        };

        // The first phase always arrives before WPF has measured anything, so the
        // track's width is still zero and the bar has nothing to scale against.
        // Re-applying the last phase once the track has a real size is what stops
        // the opening phase being the one that never shows.
        _modalBarHost.SizeChanged += (s, e) => SetModalProgress(_modalLastStage);

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

                // The install takes the dialog over rather than letting it close,
                // so there is still a scrim and a progress line on screen for the
                // whole run instead of a greyed out page and one corner caption.
                if (!_modalKeepOpen)
                {
                    CloseModal();
                }

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
        cancel.Click += (s, e) =>
        {
            Action? secondary = _modalSecondary;
            CloseModal();
            secondary?.Invoke();
        };

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
                    _modalBody,
                    _modalInput,
                    _modalCounter,
                    _modalWarning,
                    _modalStatus,
                    _modalBarHost,
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
            Child = card,

            // Escape closes it. It is a hand rolled overlay rather than a real
            // dialog, so nothing provides this for free, and a confirmation you
            // cannot back out of with the keyboard is a confirmation some people
            // cannot back out of at all.
            Focusable = true
        };

        _modalLayer.KeyDown += OnModalKeyDown;

        // Cover the whole shell so the scrim dims the page and the card centres.
        Grid.SetColumn(_modalLayer, 0);
        Grid.SetColumnSpan(_modalLayer, 1);
        Grid.SetRow(_modalLayer, 0);
        Grid.SetRowSpan(_modalLayer, 3);
        ShellGrid.Children.Add(_modalLayer);
    }


    private void ShowModal(string title, string initial, Action<string> action)
    {
        StopModalElapsed();
        _modalTitle.Text = title;
        _modalInput.Text = initial;
        _modalBody.Visibility = Visibility.Collapsed;
        _modalInput.Visibility = Visibility.Visible;
        _modalCounter.Visibility = Visibility.Visible;
        _modalWarning.Visibility = Visibility.Collapsed;
        _modalStatus.Visibility = Visibility.Collapsed;
        _modalBarHost.Visibility = Visibility.Collapsed;
        StopModalBar();
        _modalSaveButton.Visibility = Visibility.Visible;
        _modalCancelButton.Visibility = Visibility.Visible;
        _modalCancelButton.Content = "Cancel";
        _modalSaveButton.Content = "Save";
        _modalSaveButton.Style = (Style)FindResource("PrimaryButton");
        _modalSaveButton.IsEnabled = true;
        _modalAction = action;
        _modalConfirm = null;
        _modalSecondary = null;
        _modalKeepOpen = false;
        _modalLayer.Visibility = Visibility.Visible;
        UpdateCounter();
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _modalInput.Focus();
            _modalInput.SelectAll();
            _modalLayer.Focus();
        }));
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
    /// A dialog that only explains. One button, because there is nothing here to
    /// decide, and no input, because there is nothing to type.
    /// <para>
    /// The warning block is there when the explanation carries a caution, and gone
    /// when it does not. An empty triangle above an empty line still reserves its
    /// height, so a dialog that had nothing to warn about used to open with a gap
    /// in the middle of it. Passing an empty string collapses the whole block.
    /// </para>
    /// </summary>
    private void ShowInfoModal(string title, string body, string warning)
    {
        StopModalElapsed();
        _modalTitle.Text = title;
        _modalBody.Text = body;
        _modalBody.Visibility = Visibility.Visible;
        _modalInput.Visibility = Visibility.Collapsed;
        _modalCounter.Visibility = Visibility.Collapsed;

        bool hasWarning = !string.IsNullOrWhiteSpace(warning);
        _modalWarning.Visibility = hasWarning ? Visibility.Visible : Visibility.Collapsed;
        _modalWarningIcon.Visibility = hasWarning ? Visibility.Visible : Visibility.Collapsed;
        _modalWarningText.Text = hasWarning ? warning : string.Empty;
        _modalStatus.Visibility = Visibility.Collapsed;
        _modalBarHost.Visibility = Visibility.Collapsed;
        StopModalBar();
        _modalSaveButton.Visibility = Visibility.Visible;
        _modalSaveButton.Content = "GOT IT";
        _modalSaveButton.Style = (Style)FindResource("PrimaryButton");
        _modalSaveButton.IsEnabled = true;

        // One button, not two. "Cancel" beside "Got it" on a dialog that asks
        // nothing implies the reader chose something.
        _modalCancelButton.Visibility = Visibility.Collapsed;
        _modalAction = null;
        _modalConfirm = CloseModal;
        _modalSecondary = null;
        _modalKeepOpen = false;
        _modalLayer.Visibility = Visibility.Visible;
    }


    /// <summary>
    /// Destructive confirm. Centred amber warning triangle instead of a Windows
    /// message box so it matches the rest of the app. The second button is
    /// "Cancel" for anything the user can simply back out of, and "Later" for the
    /// FxSound prompt, where declining is a normal choice rather than a cancel.
    /// </summary>
    private void ShowConfirmModal(string title, string warning, string confirmText, Action action, string? declineText = null,                                                  bool keepOpen = false)
    {
        StopModalElapsed();
        _modalTitle.Text = title;
        _modalBody.Visibility = Visibility.Collapsed;
        _modalInput.Visibility = Visibility.Collapsed;
        _modalCounter.Visibility = Visibility.Collapsed;
        _modalWarning.Visibility = Visibility.Visible;
        _modalWarningIcon.Visibility = Visibility.Visible;
        _modalWarningText.Text = warning;
        _modalStatus.Visibility = Visibility.Collapsed;
        _modalBarHost.Visibility = Visibility.Collapsed;
        StopModalBar();
        _modalSaveButton.Visibility = Visibility.Visible;
        _modalSaveButton.Content = confirmText;
        _modalSaveButton.Style = (Style)FindResource("PrimaryButton");
        _modalSaveButton.IsEnabled = true;
        _modalCancelButton.Visibility = Visibility.Visible;
        _modalCancelButton.Content = declineText ?? "Cancel";
        _modalCancelButton.IsEnabled = true;
        _modalAction = null;
        _modalConfirm = action;
        _modalSecondary = null;
        _modalKeepOpen = keepOpen;
        _modalLayer.Visibility = Visibility.Visible;
    }


    /// <summary>
    /// Takes the dialog over for a run of work: title, a phase line, a bar, and
    /// no buttons at all. The scrim is already over the whole shell, so with the
    /// dialog up the page behind cannot be clicked and neither can the caption
    /// buttons, which is the point: half an install is worse than a slow one.
    /// </summary>
    private void ShowProgressModal(string title, bool showCaution)
    {
        _modalTitle.Text = title;
        _modalInput.Visibility = Visibility.Collapsed;
        _modalCounter.Visibility = Visibility.Collapsed;

        // The icon is shown on request during a run, but the old prompt copy is
        // cleared with it. Leaving that text behind put the install message and
        // the phase line on screen together, stacked.
        _modalWarning.Visibility = showCaution ? Visibility.Visible : Visibility.Collapsed;
        _modalWarningIcon.Visibility = showCaution ? Visibility.Visible : Visibility.Collapsed;
        if (showCaution)
        {
            _modalWarningText.Text = string.Empty;
        }

        _modalStatus.Visibility = Visibility.Visible;
        _modalStatus.Text = "Starting";
        _modalStatus.Margin = new Thickness(0, 16, 0, 0);
        _modalBarHost.Visibility = Visibility.Visible;
        _modalSaveButton.Visibility = Visibility.Collapsed;
        _modalCancelButton.Visibility = Visibility.Collapsed;
        _modalAction = null;
        _modalConfirm = null;
        _modalSecondary = null;
        _modalKeepOpen = false;
        _modalLayer.Visibility = Visibility.Visible;

        // Last, so the first tick and the first phase both see a started clock.
        StartModalElapsed();
    }


    /// <summary>
    /// The end of a run: what happened, and the one thing worth doing about it.
    /// The warning icon is dropped on success, because an amber triangle next to
    /// a successful install says the opposite of what it means.
    /// </summary>
    private void ShowResultModal(
        string title,
        string body,
        bool failed,
        string primaryText,
        Action primary,
        string secondaryText,
        Action secondary)
    {
        StopModalElapsed();
        _modalTitle.Text = title;
        _modalBody.Visibility = Visibility.Collapsed;
        _modalInput.Visibility = Visibility.Collapsed;
        _modalCounter.Visibility = Visibility.Collapsed;
        _modalWarning.Visibility = Visibility.Visible;
        _modalWarningIcon.Visibility = failed ? Visibility.Visible : Visibility.Collapsed;
        _modalWarningText.Text = body;
        _modalStatus.Visibility = Visibility.Collapsed;
        _modalBarHost.Visibility = Visibility.Collapsed;
        StopModalBar();
        _modalSaveButton.Visibility = Visibility.Visible;
        _modalSaveButton.Content = primaryText;
        _modalSaveButton.Style = (Style)FindResource("PrimaryButton");
        _modalSaveButton.IsEnabled = true;
        _modalCancelButton.Visibility = Visibility.Visible;
        _modalCancelButton.Content = secondaryText;
        _modalCancelButton.IsEnabled = true;
        _modalAction = null;
        _modalConfirm = primary;
        _modalSecondary = secondary;
        _modalKeepOpen = false;
        _modalLayer.Visibility = Visibility.Visible;
    }


    /// <summary>
    /// Turns a service phase into something worth reading, and drives the bar.
    /// A phase flagged indeterminate sweeps instead of filling, because the
    /// percentage it carries is a placeholder and a bar parked at 5% for a minute
    /// looks more broken than no bar at all.
    /// </summary>
    private void SetModalProgress(SetupStage stage)
    {
        // Only a genuinely new phase restarts the clock. This method is also
        // called again from the bar's own SizeChanged, and treating that as a new
        // phase would reset the elapsed time the instant the bar was measured.
        if (!string.Equals(stage.Text, _modalLastStage.Text, StringComparison.Ordinal)
            || stage.Percent != _modalLastStage.Percent
            || stage.Indeterminate != _modalLastStage.Indeterminate)
        {
            _modalStageSince = DateTime.Now;
        }

        _modalLastStage = stage;
        RenderModalStatus();

        double track = _modalBarTrack.ActualWidth;
        if (track <= 0)
        {
            return;
        }

        if (stage.Indeterminate)
        {
            StartModalBar(track);
            return;
        }

        StopModalBar();
        EaseModalBarTo(track * Math.Clamp(stage.Percent, 0, 100) / 100.0);
    }


    /// <summary>
    /// Draws the phase line, with the time spent on it so far.
    /// <para>
    /// winget cannot say how far along it is, so the one number worth showing is
    /// how long it has been going. That turns "is this stuck?" from a guess into
    /// something the user can read, and it is a fact rather than an estimate.
    /// </para>
    /// </summary>
    private void RenderModalStatus()
    {
        if (_modalLastStage.Text.Length == 0)
        {
            return;
        }

        string text = Humanise(_modalLastStage.Text);

        if (_modalElapsed is not null && _modalStageSince != DateTime.MinValue)
        {
            int seconds = (int)(DateTime.Now - _modalStageSince).TotalSeconds;
            if (seconds >= 1)
            {
                text = text + "   " + seconds.ToString(System.Globalization.CultureInfo.InvariantCulture) + "s";
            }
        }

        _modalStatus.Text = text;
    }


    private void StartModalElapsed()
    {
        StopModalElapsed();

        _modalStageSince = DateTime.Now;
        _modalElapsed = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _modalElapsed.Tick += OnModalElapsedTick;
        _modalElapsed.Start();
    }


    private void StopModalElapsed()
    {
        if (_modalElapsed is null)
        {
            return;
        }

        // Unsubscribed by name. Unsubscribing a lambda would build a second,
        // different delegate and match nothing, leaving the timer running with
        // nothing left to hold it.
        _modalElapsed.Tick -= OnModalElapsedTick;
        _modalElapsed.Stop();
        _modalElapsed = null;
    }


    private void OnModalElapsedTick(object? sender, EventArgs e)
    {
        RenderModalStatus();
    }


    /// <summary>
    /// The service speaks in stage names, and half of them are only meaningful to
    /// the code. The winget line no longer claims to be slow: with a live elapsed
    /// count beside it, the number answers that on its own.
    /// </summary>
    private static string Humanise(string text)
    {
        if (text.StartsWith("DOWNLOAD ", StringComparison.Ordinal))
        {
            return "Downloading FxSound, " + text["DOWNLOAD ".Length..];
        }

        return text.ToLowerInvariant() switch
        {
            "winget" => "Installing with winget",
            "checking" => "Checking what is already installed",
            "download" or "downloading" => "Downloading FxSound",
            "installing" => "Running the FxSound installer",
            "finishing" => "Finishing up",
            "ready" => "All done",
            "no winget" => "winget is not available on this machine",
            "retry" => "Not installed. Try again.",
            "stopped" => "Stopped",
            "failed" => "Install failed",
            _ => text
        };
    }


    private void StartModalBar(double track)
    {
        if (_modalBarAnimating)
        {
            return;
        }

        _modalBarAnimating = true;

        double chunk = track * 0.34;
        _modalBarFill.Width = chunk;

        // From fully off the left edge to hard against the right edge, and no
        // further. The end is the track less the fill's own width, not the track:
        // animating to the track put the whole fill outside the groove.
        _modalBarSlide.BeginAnimation(
            TranslateTransform.XProperty,
            new DoubleAnimation(-chunk, track - chunk, TimeSpan.FromSeconds(1.15))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase()
            });
    }


    /// <summary>
    /// Eases the bar toward a known percentage instead of teleporting to it.
    /// <para>
    /// The stage percentages are real milestones rather than a made-up ramp, but
    /// they arrive seconds apart, so a bar that jumps looks broken for exactly as
    /// long as the gap. This one moves the whole way and arrives on time.
    /// </para>
    /// </summary>
    private void EaseModalBarTo(double target)
    {
        double from = _modalBarFill.Width;
        double to = Math.Max(0, Math.Min(target, _modalBarTrack.ActualWidth));

        if (Math.Abs(to - from) < 0.5)
        {
            _modalBarFill.Width = to;
            return;
        }

        _modalBarFill.BeginAnimation(FrameworkElement.WidthProperty, new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(420))
        {
            EasingFunction = new SineEase()
        });
    }


    private void StopModalBar()
    {
        if (!_modalBarAnimating)
        {
            return;
        }

        _modalBarAnimating = false;
        _modalBarSlide.BeginAnimation(TranslateTransform.XProperty, null);
        _modalBarSlide.X = 0;
        _modalBarFill.BeginAnimation(FrameworkElement.WidthProperty, null);
    }


    /// <summary>
    /// Escape backs out of whatever the dialog is offering, and Tab stays inside.
    /// <para>
    /// A run in progress has no way out by design, because half an install is
    /// worse than a slow one, so Escape does nothing while the modal owns a
    /// progress state. Every other state is cancellable, and the cancel button is
    /// whichever one the dialog was opened with.
    /// </para>
    /// <para>
    /// This is a hand rolled overlay rather than a real dialog, so nothing keeps
    /// the keyboard inside it for free. Without this, Tab from the name box walks
    /// straight out of the dialog and into the controls on the page behind the
    /// scrim, which then take focus while looking greyed out and unreachable.
    /// </para>
    /// </summary>
    private void OnModalKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CancelModal();
            return;
        }

        if (e.Key != Key.Tab)
        {
            return;
        }

        bool backwards = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        IInputElement? from = Keyboard.FocusedElement;

        // The dialog is built once and has a known, fixed set of things that can
        // take focus: the name box where there is one, and the two buttons. So
        // "first" and "last" are answered directly rather than by walking a tree
        // that is three elements deep. Tab from the last wraps to the first and
        // Shift Tab from the first wraps to the last; anything in between is left
        // alone, so ordinary tabbing inside the dialog still behaves normally.
        IInputElement? first = _modalInput.Visibility == Visibility.Visible
            ? _modalInput
            : _modalSaveButton;

        IInputElement? last = _modalCancelButton.Visibility == Visibility.Visible
            ? _modalCancelButton
            : _modalSaveButton;

        if (backwards && ReferenceEquals(from, first))
        {
            e.Handled = true;
            last.Focus();
        }
        else if (!backwards && ReferenceEquals(from, last))
        {
            e.Handled = true;
            first.Focus();
        }
    }

    private void CancelModal()
    {
        // A run in progress owns the dialog and cannot be dismissed.
        if (_modalBarHost.Visibility == Visibility.Visible || _modalSaveButton.Visibility != Visibility.Visible)
        {
            return;
        }

        Action? secondary = _modalSecondary;
        CloseModal();
        secondary?.Invoke();
    }

    private void CloseModal()

    {
        StopModalElapsed();
        _modalLayer.Visibility = Visibility.Collapsed;
        _modalAction = null;
        _modalConfirm = null;
        _modalSecondary = null;
        _modalKeepOpen = false;
        _modalSaveButton.Content = "Save";
        StopModalBar();
    }

}
