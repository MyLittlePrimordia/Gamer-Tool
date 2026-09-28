using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;

namespace GamerTool.UI;

/// <summary>
/// The on/off switch, as a control of its own.
/// <para>
/// Two styles each used to carry their own copy of it: a standalone toggle, and
/// the settings list's row, which needs the switch inline in its own five column
/// grid. The duplication itself is unavoidable, because a ControlTemplate trigger
/// cannot read a custom property back off its own templated parent, but the
/// numbers in it are not. They had already started to disagree: the standalone
/// one took its colour from its own Foreground while the row's was hardcoded to
/// one green, which is why the anti-clip switch on the Audio tab is rose and
/// every switch in Settings is green with no way to ask for anything else.
/// </para>
/// <para>
/// So the switch is a type with an <see cref="Accent"/> of its own, and both
/// places use the one template in Theme.xaml. There is a single set of dimensions
/// and a single set of states, and a row can be tinted like anything else.
/// </para>
/// </summary>
public sealed class SwitchToggle : ToggleButton
{
    /// <summary>The colour of the track when the switch is on.</summary>
    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent),
        typeof(Brush),
        typeof(SwitchToggle),
        new FrameworkPropertyMetadata(null));

    public Brush? Accent
    {
        get => (Brush?)GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    public SwitchToggle()
    {
        // The whole control is the switch. Left alone, a ToggleButton draws a
        // check mark in the middle of its content, which would put a stray tick
        // through the middle of the track. The template has no content presenter
        // at all, so this is belt and braces.
        Focusable = true;
    }
}
