using System.Globalization;
using System.Text.Json;
using System.Runtime.InteropServices;

namespace Albiqo.Transfer.Windows;

internal static class WindowsSmokeTest
{
    public static int Run(string outputDirectory)
    {
        var results = new List<object>();
        var failures = new List<string>();
        try
        {
            Directory.CreateDirectory(outputDirectory);
            foreach (var language in new[] { "tr-TR", "en-US" })
            {
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(language);
                foreach (var minimum in new[] { false, true })
                {
                    using var form = new MainWindow();
                    form.ShowInTaskbar = false;
                    form.StartPosition = FormStartPosition.Manual;
                    form.Location = new Point(-20000, -20000);
                    if (minimum) form.Size = form.MinimumSize;
                    form.Show(); form.PerformLayout(); Application.DoEvents();
                    var errors = new List<string>();
                    CheckBounds(form, errors);
                    var setup = form.Controls.Find("UsbSetup", searchAllChildren: true).SingleOrDefault();
                    if (setup is not Button || !setup.Visible || !setup.Enabled)
                        errors.Add("USB prerequisite installer action must be visible and available before discovery.");
                    var radios = Descendants(form).OfType<RadioButton>().ToArray();
                    radios.Single(x => x.Text == "Bluetooth").Checked = true;
                    Application.DoEvents();
                    if (setup?.Visible != false) errors.Add("Apple USB setup action must be hidden in Bluetooth mode.");
                    CheckBounds(form, errors);
                    radios.Single(x => x.Text == "USB").Checked = true;
                    Application.DoEvents();
                    if (setup?.Visible != true) errors.Add("USB setup action must return when switching back to USB.");
                    var name = $"Albiqo-Transfer-{language}-{(minimum ? "minimum" : "default")}.png";
                    using (var bitmap = new Bitmap(form.Width, form.Height))
                    {
                        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                        bitmap.Save(Path.Combine(outputDirectory, name), System.Drawing.Imaging.ImageFormat.Png);
                    }
                    results.Add(new { language, minimum, width = form.Width, height = form.Height, clientWidth = form.ClientSize.Width, clientHeight = form.ClientSize.Height, dpi = form.DeviceDpi, errors, controls = DescribeControls(form), screenshot = name });
                    form.ShowDirectTransferSmokeState();
                    form.PerformLayout(); Application.DoEvents();
                    CheckBounds(form, errors);
                    var directName = $"Albiqo-Transfer-direct-{language}-{(minimum ? "minimum" : "default")}.png";
                    using (var bitmap = new Bitmap(form.Width, form.Height))
                    {
                        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                        bitmap.Save(Path.Combine(outputDirectory, directName), System.Drawing.Imaging.ImageFormat.Png);
                    }
                    results.Add(new { language, minimum, direct = true, errors, controls = DescribeControls(form), screenshot = directName });
                    if (!minimum)
                    {
                        var pairedName = $"Albiqo-Transfer-paired-{language}-default.png";
                        var pairing = Pump(form.CheckPairingLifecycleSmokeAsync(() =>
                        {
                            form.PerformLayout(); Application.DoEvents();
                            CheckBounds(form, errors);
                            using var bitmap = new Bitmap(form.Width, form.Height);
                            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                            bitmap.Save(Path.Combine(outputDirectory, pairedName), System.Drawing.Imaging.ImageFormat.Png);
                        }));
                        results.Add(new { language, minimum, pairingLifecycle = true, fixture = "production UI controller with an in-memory simulated phone; no hardware connection", pairing, errors, screenshot = pairedName });
                    }
                    form.Hide();
                    failures.AddRange(errors.Select(error => $"{language}/{(minimum ? "minimum" : "default")}: {error}"));
                }
            }
            if (failures.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, failures));
            WriteReport(outputDirectory, "passed", null, results);
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.ToString());
            WriteReport(outputDirectory, "failed", error.ToString(), results);
            return 1;
        }
    }

    // The runner has an STA UI thread but no Application.Run loop. Keep the actual
    // WinForms continuations/buttons/BeginInvoke callbacks moving while awaiting the fixture.
    private static T Pump<T>(Task<T> work)
    {
        var wait = System.Diagnostics.Stopwatch.StartNew();
        while (!work.IsCompleted)
        {
            if (wait.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException("Windows pairing lifecycle smoke check timed out.");
            Application.DoEvents();
            Thread.Sleep(10);
        }
        return work.GetAwaiter().GetResult();
    }

    internal static int RecordStartupFailure(string outputDirectory, Exception error)
    {
        Console.Error.WriteLine(error.ToString());
        try { WriteReport(outputDirectory, "startup_failed", error.ToString(), []); }
        catch (Exception reportError) { Console.Error.WriteLine(reportError.ToString()); }
        return 1;
    }

    private static void WriteReport(string outputDirectory, string status, string? error, List<object> results)
    {
        Directory.CreateDirectory(outputDirectory);
        var report = new { status, error, runtime = RuntimeInformation.FrameworkDescription, os = RuntimeInformation.OSDescription, userInteractive = Environment.UserInteractive, cases = results };
        File.WriteAllText(Path.Combine(outputDirectory, "Windows-UI-Smoke.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        if (error is not null) File.WriteAllText(Path.Combine(outputDirectory, "smoke-failure.txt"), error);
    }

    private static object[] DescribeControls(Control parent) => parent.Controls.Cast<Control>().Select(child => (object)new
    {
        type = child.GetType().Name, text = child.Text, visible = child.Visible, enabled = child.Enabled,
        bounds = new { child.Left, child.Top, child.Width, child.Height },
        client = new { child.ClientSize.Width, child.ClientSize.Height },
        children = DescribeControls(child)
    }).ToArray();

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void CheckBounds(Control parent, List<string> errors)
    {
        foreach (Control child in parent.Controls)
        {
            if (!child.Visible) continue;
            // DataGridView owns internal scroll bars that intentionally extend by a border pixel.
            if (parent is not DataGridView && (child is ButtonBase or TextBox or ComboBox or Label or ProgressBar))
            {
                var bounds = parent.ClientRectangle;
                bounds.Inflate(2, 2);
                if (!bounds.Contains(child.Bounds)) errors.Add($"{parent.GetType().Name}/{child.GetType().Name} '{child.Text}' outside {parent.ClientSize}: {child.Bounds}");
                if (child is ButtonBase && (child.Width < 40 || child.Height < 24)) errors.Add("Action target too small: " + child.Text);
            }
            CheckBounds(child, errors);
        }
    }
}
