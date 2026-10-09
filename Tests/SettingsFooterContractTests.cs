using System.Drawing.Imaging;

namespace PlaycastCompanion.Tests;

public static class SettingsFooterContractTests
{
    public static void Run()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { VerifyFooter(); }
            catch (Exception error) { failure = error; }
            finally { Application.Exit(); }
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(30))) throw new TimeoutException("Settings footer UI contract timed out.");
        if (failure != null) throw new InvalidOperationException("Settings footer UI contract failed.", failure);
    }

    private static void VerifyFooter()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using var signal = new EventWaitHandle(false, EventResetMode.AutoReset);
        using var context = new TrayAppContext(false, signal, diagnosticsOnly: true);
        using var form = new MainForm(context) { StartPosition = FormStartPosition.Manual, Location = new(-9000, -9000) };
        form.Show();
        Application.DoEvents();
        var save = Find<Button>(form, "Save changes");
        var discard = Find<Button>(form, "Discard");
        foreach (var size in new[] { new Size(860, 640), new Size(1000, 720) })
        {
            form.Size = size;
            foreach (var page in new[] { "Overview", "Lighting", "Discord", "Session logs", "Settings" })
            {
                Find<Button>(form, page).PerformClick();
                Application.DoEvents();
                AssertRendered(save, form, page + " clean");
                Check(!save.Enabled && !discard.Visible, "Clean settings must show disabled Save and hide Discard.");
            }
            Find<Button>(form, "Lighting").PerformClick();
            var checkbox = Descendants(form).OfType<CheckBox>().First(control => control.Visible && control.Text == "Include");
            var original = checkbox.Checked;
            checkbox.Checked = !original;
            Application.DoEvents();
            Check(save.Enabled, "Editing a lighting preference must enable Save.");
            AssertRendered(discard, form, "edited lighting");
            Capture(form, $"footer-edited-{size.Width}x{size.Height}.png");
            foreach (var page in new[] { "Overview", "Lighting", "Discord", "Session logs", "Settings" })
            {
                Find<Button>(form, page).PerformClick();
                Application.DoEvents();
                AssertRendered(save, form, page + " edited");
                AssertRendered(discard, form, page + " edited");
                Check(save.Enabled, "Pending edits must remain saveable while navigating settings pages.");
            }
            Find<Button>(form, "Analyze logs").PerformClick();
            Application.DoEvents();
            Check(!save.Visible && !discard.Visible, "Analysis must hide settings actions even with pending edits.");
            Find<Button>(form, "Settings").PerformClick();
            Application.DoEvents();
            AssertRendered(save, form, "edited settings");
            AssertRendered(discard, form, "edited settings");
            discard.PerformClick();
            Application.DoEvents();
            Check(checkbox.Checked == original && !save.Enabled && !discard.Visible,
                "Discard must restore the edited setting and return Save to disabled.");
            Capture(form, $"footer-clean-{size.Width}x{size.Height}.png");
        }
        form.Close();
    }

    private static void AssertRendered(Control control, Form form, string context)
    {
        Check(control.Visible && control.Width > 0 && control.Height > 0, context + ": action must be visible with a nonempty size.");
        for (Control? current = control; current?.Parent != null; current = current.Parent)
            Check(current.Parent.ClientRectangle.Contains(current.Bounds), context + ": action must be inside every ancestor's client area.");
        var bounds = form.RectangleToClient(control.RectangleToScreen(control.ClientRectangle));
        foreach (var sibling in form.Controls.Cast<Control>().Where(sibling => sibling.Visible && sibling != control))
        {
            if (IsAncestor(sibling, control)) continue;
            Check(!sibling.Bounds.IntersectsWith(bounds), context + ": main content must not cover the footer action.");
        }
    }

    private static bool IsAncestor(Control ancestor, Control child)
    {
        for (Control? current = child; current != null; current = current.Parent)
            if (current == ancestor) return true;
        return false;
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static T Find<T>(Control root, string text) where T : Control =>
        Descendants(root).OfType<T>().Single(control => control.Text == text);

    private static void Capture(Form form, string name)
    {
        var directory = Environment.GetEnvironmentVariable("PLAYCAST_UI_CONTRACT_SCREENSHOTS");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
        bitmap.Save(Path.Combine(directory, name), ImageFormat.Png);
    }

    private static void Check(bool condition, string message) => SessionLogContractTests.Check(condition, message);
}
