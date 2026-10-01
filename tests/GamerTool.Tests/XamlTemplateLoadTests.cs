using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// Every control template in the shipped theme actually instantiates.
/// <para>
/// This exists because of a specific release. The caption buttons' focus ring was
/// changed from a Trigger to a MultiDataTrigger that also required
/// <c>KeyboardNavigation.ShowKeyboardCues</c>, which is how WPF says whether the
/// last input came from a key. It compiled. It passed every test in this project,
/// including <see cref="XamlResourceTests"/>, which exists to catch missing
/// resources. And then the app refused to start on every single launch with
/// "Provide value on 'System.Windows.StaticResourceExtension' threw an exception",
/// a message that names a StaticResource and is about a Binding, because that is
/// how WPF reports a template it could not parse.
/// </para>
/// <para>
/// The lesson is that checking key names is not the same as loading the XAML.
/// Every resource this project used to check was named correctly and resolved
/// correctly, right up until a template stopped being parseable. So these tests do
/// not read the XAML. They load the compiled theme through a real pack URI into a
/// real <see cref="Application"/>, stand a real instance of every styled control
/// type up, and apply the template to it, which is the exact step that threw.
/// </para>
/// <para>
/// <b>What this does not cover:</b> XAML that lives outside the theme and is not a
/// dictionary, which in practice means the markup in MainWindow.xaml. That file
/// mostly consumes styles rather than defining templates, and loading it in a test
/// would mean running its constructor, which builds the window, wires hotkeys and
/// reaches for the displays. A guard that covered it would have to be a lint
/// rather than a load, and a lint written against one crash tends to grow into a
/// general XAML linter that cries wolf. This is deliberately the load, and it is
/// deliberately the layer that actually broke.
/// </para>
/// <para>
/// No real hardware is touched. Every control constructed here is a stock WPF
/// control or one of the app's own UI primitives, none of which open a display or
/// start an audio engine.
/// </para>
/// </summary>
[Collection("wpf")]
public class XamlTemplateLoadTests
{
    // One Application per AppDomain, and one STA thread for the whole run, because
    // Application.Current is a singleton and WPF objects have thread affinity. All
    // the work below is marshalled onto that thread, so xUnit running this class
    // alongside others cannot tear the theme out from under it. Shared through
    // WpfTestHost rather than kept private, because a second class needing real
    // WPF objects would otherwise build a second Application and abort the run.
    private static T OnSta<T>(Func<T> body) => WpfTestHost.Invoke(body);


    /// <summary>
    /// The theme, loaded the same way App.xaml loads it.
    /// <para>
    /// Straight from the compiled assembly rather than parsed out of the source
    /// file. The loose XAML reader in XamlReader is a different parser with
    /// different rules from the one that reads BAML, and the failure being guarded
    /// against only appears in the compiled path, so a source-based test would
    /// pass straight over the thing it is meant to catch.
    /// </para>
    /// </summary>
    private static ResourceDictionary? _theme;

    /// <summary>
    /// The theme, loaded the same way App.xaml loads it.
    /// <para>
    /// Through <see cref="Application.LoadComponent(Uri)"/> with a pack-absolute
    /// path that names the assembly itself, so it does not depend on
    /// Application.ResourceAssembly. That property is write-once and a test host
    /// has usually already claimed it, and pointing it at the app assembly throws
    /// on the second write and takes the whole test run down with it.
    /// </para>
    /// <para>
    /// Compiled BAML rather than the source file on purpose. The loose reader in
    /// XamlReader is a different parser with different rules from the one that
    /// reads BAML, and the failure being guarded against only shows up in the
    /// compiled path, so a source-based test would pass straight over the thing it
    /// is meant to catch.
    /// </para>
    /// </summary>
    private static ResourceDictionary LoadTheme()
    {
        if (_theme is not null)
        {
            return _theme;
        }

        object loaded = Application.LoadComponent(
            new Uri("/GamerTool;component/UI/Theme.xaml", UriKind.Relative));

        _theme = loaded as ResourceDictionary
            ?? throw new InvalidOperationException(
                "UI/Theme.xaml did not load as a ResourceDictionary but as "
                + loaded?.GetType().FullName);

        Application.Current.Resources.MergedDictionaries.Add(_theme);
        return _theme;
    }


    /// <summary>
    /// The styles in a dictionary that carry a control template, with the x:Key
    /// each was declared under.
    /// <para>
    /// The key is read from the dictionary rather than from
    /// <see cref="Style.Keys"/>, which holds the implicit per-style key the
    /// dictionary uses to look the style up, not the name the XAML gave it. The
    /// name is the only thing a person can act on when this fails.
    /// </para>
    /// <para>
    /// The template is found in the setter collection rather than read off
    /// <see cref="Style"/>, because a style has no Template property. It is just a
    /// setter against Control.TemplateProperty, and asking for it directly does
    /// not compile.
    /// </para>
    /// <para>
    /// Each key is fetched inside its own try, which is not defensive
    /// programming, it is the whole reason this file works. A ResourceDictionary
    /// creates its values lazily, so a template that cannot be parsed throws on
    /// <c>dictionary[key]</c> rather than on ApplyTemplate. Without this catch one
    /// bad style aborts the sweep on the spot, every other style goes unchecked,
    /// and the report is a raw XamlParseException with no indication of which of
    /// sixty styles was the culprit.
    /// </para>
    /// </summary>
    private static List<(string Name, Style Style)> StyledTemplates(ResourceDictionary dictionary, List<string> problems)
    {
        var found = new List<(string, Style)>();

        foreach (object key in dictionary.Keys.Cast<object>().ToList())
        {
            string name = key as string ?? key.GetType().Name;

            Style? style;

            try
            {
                style = dictionary[key] as Style;
            }
            catch (Exception ex)
            {
                problems.Add("  " + name + ": this style cannot even be read out of the"
                    + " dictionary, so its template never parsed." + Environment.NewLine
                    + "      " + Innermost(ex));
                continue;
            }

            if (style is null
                || style.TargetType is not Type target
                || !typeof(Control).IsAssignableFrom(target)
                || !HasTemplate(style))
            {
                continue;
            }

            found.Add((name, style));
        }

        return found;
    }


    /// <summary>
    /// The deepest exception, described.
    /// <para>
    /// WPF wraps a template it cannot parse several times over and the outermost
    /// message is about a StaticResource, which is almost never what went wrong.
    /// The one that says why is always further in, and naming it is the entire
    /// difference between a report you can act on and one you have to guess at.
    /// </para>
    /// </summary>
    private static string Innermost(Exception ex)
    {
        Exception root = ex;

        while (root.InnerException is not null)
        {
            root = root.InnerException;
        }

        return root.GetType().Name + ": " + root.Message;
    }


    private static bool HasTemplate(Style style) =>
        style.Setters.OfType<Setter>().Any(s => s.Property == Control.TemplateProperty);


    [Fact]
    public void The_theme_loads_from_the_compiled_assembly()
    {
        int count = OnSta(() =>
        {
            ResourceDictionary theme = LoadTheme();
            return theme.Keys.OfType<string>().Count();
        });

        // Guards this file. A pack URI that silently resolves to nothing would
        // leave every test below iterating an empty collection and passing, which
        // is the failure mode the existing key test already had to guard against.
        Assert.True(count > 40, "the theme loaded but declared only " + count + " keys");
    }


    [Fact]
    public void Every_styled_control_template_applies_to_a_real_control()
    {
        List<string> failures = OnSta(() =>
        {
            ResourceDictionary theme = LoadTheme();
            var problems = new List<string>();
            List<(string Name, Style Style)> styles = StyledTemplates(theme, problems);

            // A host so the controls are measured in a real layout pass rather
            // than in a vacuum. The window is never shown; it exists to give the
            // templates a visual tree.
            var host = new Window { Width = 1200, Height = 800, ShowInTaskbar = false, Visibility = Visibility.Hidden };
            var panel = new Grid();
            host.Content = panel;

            foreach ((string name, Style style) in styles)
            {
                Type target = style.TargetType!;
                string label = name;

                if (target.IsAbstract || target.GetConstructor(Type.EmptyTypes) is null)
                {
                    // A style on a type this project never instantiates. Not a
                    // failure, and not something to assert on, so it is skipped
                    // rather than forced.
                    continue;
                }

                Control? control = null;

                try
                {
                    control = (Control)Activator.CreateInstance(target)!;
                    control.Style = style;

                    // The step that throws for a template that parses but cannot
                    // be instantiated, such as one binding to something the
                    // element does not have.
                    control.ApplyTemplate();
                    control.Measure(new Size(1200, 800));
                    control.Arrange(new Rect(0, 0, 1200, 800));
                }
                catch (Exception ex)
                {
                    problems.Add(
                        "  " + label + " (" + target.Name + "): " + Innermost(ex));
                }
                finally
                {
                    if (control is not null)
                    {
                        panel.Children.Remove(control);
                    }
                }
            }

            host.Close();
            return problems;
        });

        Assert.True(
            failures.Count == 0,
            failures.Count + " template(s) in the theme cannot be applied to a real control."
            + " The app will build, pass every other test, and then fail to start on"
            + " every launch:" + Environment.NewLine + string.Join(Environment.NewLine, failures));
    }


    [Fact]
    public void The_template_sweep_actually_reaches_the_shipped_templates()
    {
        // The sweep above is only as good as what it iterates, and "no styles
        // matched" is a pass. Asserted by x:Key rather than by target type,
        // because the names here are the ones a person would go and look at when
        // the sweep fails, and because CaptionButton in particular is the style
        // whose template contained the change that made the app unlaunchable.
        List<string> sweepProblems = new();

        List<string> covered = OnSta(() =>
            StyledTemplates(LoadTheme(), sweepProblems).Select(s => s.Name).ToList());

        Assert.True(
            sweepProblems.Count == 0,
            "the sweep could not read every style:" + Environment.NewLine
            + string.Join(Environment.NewLine, sweepProblems));

        foreach (string expected in new[]
                 {
                     "CaptionButton",   // the template that broke startup
                     "CaptionClose",    // and its near duplicate
                     "OptionRow",       // custom control, template, and a nested Button
                     "InfoHintButton",  // referenced from inside another template
                     "ModernTextBox"
                 })
        {
            Assert.Contains(expected, covered);
        }

        // And a floor, so a theme that somehow loaded one style cannot satisfy
        // the list above and leave most of it unchecked.
        Assert.True(
            covered.Count > 20,
            "the sweep only reached " + covered.Count + " styled templates");
    }


    [Fact]
    public void The_dictionary_app_xaml_merges_exists_on_disk()
    {
        // App.xaml names its theme by relative path, which is a runtime failure if
        // the file is renamed or moved and nothing catches it until startup.
        string root = Path.GetDirectoryName(
            typeof(XamlTemplateLoadTests).Assembly.Location) is string dir
            ? FindRepoRoot(dir)
            : throw new InvalidOperationException("could not locate the repository");

        string appXaml = Path.Combine(root, "app", "App.xaml");
        Assert.True(File.Exists(appXaml), appXaml + " is missing");

        string theme = Path.Combine(root, "app", "UI", "Theme.xaml");
        Assert.True(File.Exists(theme), theme + " is missing");
    }


    private static string FindRepoRoot(string start)
    {
        var dir = new DirectoryInfo(start);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "app", "GamerTool.csproj")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("could not locate the repository");
    }
}
