using System.Drawing;
using System.Windows.Forms;

namespace GamerTool.Services;

/// <summary>
/// The tray's own palette, taken off the app's theme.
/// <para>
/// A nested type would shadow these names inside the renderer's colour table and
/// make every reference ambiguous, so they live at namespace scope and are named
/// for what they are rather than for where they are used.
/// </para>
/// <para>
/// The surface is #0E0E0E rather than #000000 on purpose. The window is pure
/// black, but this menu floats over the taskbar, and a menu painted in exactly
/// the same black as the thing behind it reads as a hole rather than as a panel.
/// </para>
/// </summary>
internal static class TrayColours
{
    public static readonly Color Surface = ColorTranslator.FromHtml("#0E0E0E");
    public static readonly Color SurfaceHover = ColorTranslator.FromHtml("#1C1C1C");
    public static readonly Color Border = ColorTranslator.FromHtml("#2C2C2C");
    public static readonly Color Text = ColorTranslator.FromHtml("#EDEDED");
    public static readonly Color Accent = ColorTranslator.FromHtml("#34D399");
}

/// <summary>
/// The tray menu, painted in the app's own black.
/// <para>
/// A <see cref="ContextMenuStrip"/> belongs to WinForms, not to WPF, so none of
/// the app's theme reaches it. It renders with whatever the Windows theme says,
/// which on a light-mode machine is a white box hanging off a black app - and it
/// is the first thing anybody sees after the window is closed, since the tray is
/// the surface the user is on at that point.
/// </para>
/// <para>
/// So the menu draws itself. A colour table rather than a whole hand written
/// renderer, because the professional renderer already gets the geometry, the
/// shadow and the rounded corners right at the current DPI. Only the paint needs
/// replacing, and overriding the table is the supported way to do that.
/// </para>
/// </summary>
public sealed class DarkTrayMenuRenderer : ToolStripProfessionalRenderer
{
    public DarkTrayMenuRenderer()
        : base(new TrayPalette())
    {
    }

    private sealed class TrayPalette : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => TrayColours.Surface;
        public override Color MenuBorder => TrayColours.Border;
        public override Color MenuItemBorder => TrayColours.Border;

        public override Color MenuItemSelected => TrayColours.SurfaceHover;
        public override Color MenuItemSelectedGradientBegin => TrayColours.SurfaceHover;
        public override Color MenuItemSelectedGradientEnd => TrayColours.SurfaceHover;

        public override Color MenuItemPressedGradientBegin => TrayColours.SurfaceHover;
        public override Color MenuItemPressedGradientEnd => TrayColours.SurfaceHover;

        // The gutter down the left of every item, where a tick or an icon would
        // sit. It is a separate gradient, and leaving it at the light theme default
        // is what puts a pale stripe down the side of an otherwise black menu.
        public override Color ImageMarginGradientBegin => TrayColours.Surface;
        public override Color ImageMarginGradientMiddle => TrayColours.Surface;
        public override Color ImageMarginGradientEnd => TrayColours.Surface;

        public override Color SeparatorDark => TrayColours.Border;
        public override Color SeparatorLight => TrayColours.Border;

        // The tick beside a checked item, in the settings accent so a check reads
        // as deliberate rather than as part of the system theme.
        public override Color CheckBackground => TrayColours.Accent;
        public override Color CheckSelectedBackground => TrayColours.Accent;
        public override Color CheckPressedBackground => TrayColours.Accent;
    }

    /// <summary>
    /// A menu item that is legible on the black surface.
    /// <para>
    /// The colour table paints the background, the hover and the separator. It
    /// does not paint the text. A <see cref="ToolStripMenuItem"/> takes its
    /// foreground from the system colours unless the item is told otherwise, which
    /// is why a themed menu otherwise comes out as dark grey on near black.
    /// </para>
    /// </summary>
    public static ToolStripMenuItem Item(string text, EventHandler onClick)
    {
        ToolStripMenuItem item = new(text)
        {
            ForeColor = TrayColours.Text,
            BackColor = TrayColours.Surface
        };

        item.Click += onClick;
        return item;
    }

    /// <summary>A separator, on the same surface as everything it divides.</summary>
    public static ToolStripSeparator Separator() => new()
    {
        BackColor = TrayColours.Surface,
        Margin = new Padding(8, 4, 8, 4),
        Width = 1
    };

    /// <summary>
    /// A line that opens a list, with the arrow that says so.
    /// <para>
    /// The arrow needs the image margin on, because that is the gutter it is
    /// drawn in. The drop down the shell builds for the child list is a separate
    /// strip and gets the margin turned on for it explicitly, since the tick
    /// beside a loaded slot is drawn in that same gutter and has nowhere to go
    /// without it.
    /// </para>
    /// </summary>
    public static ToolStripMenuItem Submenu(string text)
    {
        ToolStripMenuItem item = new(text)
        {
            ForeColor = TrayColours.Text,
            BackColor = TrayColours.Surface
        };

        // Touching DropDown is what makes the shell build the child strip. An
        // item with no drop down has no DropDown to configure, and the list
        // inside it is created on first use instead - by which point the margin
        // is already wrong.
        //
        // Cast rather than assuming, because ShowImageMargin is on the concrete
        // ToolStripDropDownMenu and not on the ToolStripDropDown this hands
        // back. The shell does build the concrete one; a plain ToolStripDropDown
        // would simply keep its own defaults.
        if (item.DropDown is ToolStripDropDownMenu child)
        {
            child.ShowImageMargin = true;
            child.BackColor = TrayColours.Surface;
            child.ForeColor = TrayColours.Text;
        }

        return item;
    }

    /// <summary>
    /// A menu line that can be ticked, for the slot that is loaded.
    /// <para>
    /// <c>CheckOnClick</c> is deliberately off. The tick says what is loaded and
    /// is not something to click, and with it on, choosing the slot you are
    /// already on would un-tick it in the menu and load it again. That is the one
    /// line in this menu where the gesture and the meaning do not match.
    /// </para>
    /// <para>
    /// The mark itself is coloured by <see cref="TrayPalette"/>'s CheckBackground,
    /// which is the supported way to do it. There is no per item check colour to
    /// set, and reaching for one is what this method's first draft did.
    /// </para>
    /// </summary>
    public static ToolStripMenuItem CheckableItem(string text, string detail, bool active, EventHandler onClick)
    {
        ToolStripMenuItem item = new(text)
        {
            ForeColor = TrayColours.Text,
            BackColor = TrayColours.Surface,
            Checked = active,
            CheckOnClick = false,
            ToolTipText = detail
        };

        item.Click += onClick;
        return item;
    }

    /// <summary>
    /// A menu strip wired up to this palette: dark surface, dark renderer, and no
    /// system colours anywhere in the tree.
    /// <para>
    /// The image margin is on, for the arrow beside a line that opens the slot
    /// list. It was off while this menu held no checkable items, on the grounds
    /// that a gutter is a column of dead space down the left, and on a dark
    /// surface a dead column is more obvious than it would be on a light one.
    /// The slots are behind an arrow now, so the ticks are on the child list and
    /// this gutter only carries the arrow that says there is one.
    /// </para>
    /// </summary>
    public static ContextMenuStrip Menu() => new()
    {
        BackColor = TrayColours.Surface,
        ForeColor = TrayColours.Text,

        // No RenderMode is set here. Naming Professional would be misleading:
        // assigning a Renderer at all flips the strip to Custom, and the thing
        // that actually matters is which renderer it is.
        Renderer = new DarkTrayMenuRenderer(),
        ShowImageMargin = true
    };
}
