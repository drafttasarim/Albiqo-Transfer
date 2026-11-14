using System.Globalization;
using System.Text.Json;

namespace Albiqo.Transfer.Windows;

internal static class WindowsSmokeTest
{
    public static int Run(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var results = new List<object>();
        try
        {
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
                    var name = $"Albiqo-Transfer-{language}-{(minimum ? "minimum" : "default")}.png";
                    using (var bitmap = new Bitmap(form.Width, form.Height))
                    {
                        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                        bitmap.Save(Path.Combine(outputDirectory, name), System.Drawing.Imaging.ImageFormat.Png);
                    }
                    results.Add(new { language, minimum, width = form.Width, height = form.Height, errors, screenshot = name });
                    form.Hide();
                    if (errors.Count > 0) throw new InvalidOperationException(string.Join("; ", errors));
                }
            }
            File.WriteAllText(Path.Combine(outputDirectory, "Windows-UI-Smoke.json"), JsonSerializer.Serialize(new { status = "passed", cases = results }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(outputDirectory, "Windows-UI-Smoke.json"), JsonSerializer.Serialize(new { status = "failed", error = error.ToString(), cases = results }, new JsonSerializerOptions { WriteIndented = true }));
            return 1;
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
