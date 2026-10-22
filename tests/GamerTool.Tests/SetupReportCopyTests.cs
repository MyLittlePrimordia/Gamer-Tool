using System.Linq;
using System.Windows;
using System.Windows.Controls;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// The setup check's report is the thing a support thread asks for, so the dialog it
/// appears in has to be able to hand it over. The report is built here and shown by
/// the real <c>ShowSetupReport</c>, because the copy button is made in that method
/// rather than in markup.
/// </summary>
[Collection("wpf")]
public sealed class SetupReportCopyTests
{
    private static SetupCheckReport Report() => new(new[]
    {
        new SetupCheckItem("GAMMA", "the ramp was taken", SetupCheckState.Pass),
        new SetupCheckItem("HDR", "this screen does not report HDR", SetupCheckState.Skip),
        new SetupCheckItem("AUDIO", "the default device did not move", SetupCheckState.Fail),
    });

    [Fact]
    public void The_report_offers_a_copy_and_puts_the_whole_list_on_the_clipboard()
    {
        WindowHarness.Run(MainWindowTabs.None, h =>
        {
            SetupCheckReport report = Report();

            h.Invoke("ShowSetupReport", report);

            // The modal hosts the report's own panel, so the button is one level in.
            var host = h.Read<Panel>("_modalCustom");
            Button copy = Assert.IsType<Button>(
                host.Children.OfType<Panel>().SelectMany(p => p.Children.OfType<Button>()).Single());

            // A word, not an icon, so it is obvious what it does before it is pressed.
            Assert.Equal("COPY", copy.Content);
            Assert.Equal(Visibility.Visible, copy.Visibility);

            WindowHarness.Click(copy);

            // The mark in the dialog, so the press is answered where the eye is.
            Assert.Equal("COPIED", copy.Content);

            // And every line of it, not just the summary: the detail is what makes a
            // pasted report worth reading.
            string text = Clipboard.GetText();
            Assert.Contains(report.Summary, text);
            Assert.Contains("GAMMA", text);
            Assert.Contains("this screen does not report HDR", text);
            Assert.Contains("the default device did not move", text);
            Assert.Contains("AUDIO", text);
        });
    }
}
