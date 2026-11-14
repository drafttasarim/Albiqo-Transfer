using Albiqo.Transfer.Core;
using System.Diagnostics;
using System.Globalization;

namespace Albiqo.Transfer.Windows;

public sealed class MainWindow : Form
{
    private readonly bool turkish = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "tr";
    private static readonly Color Ink = Color.FromArgb(19, 17, 29), Panel = Color.FromArgb(35, 31, 48), Teal = Color.FromArgb(48, 210, 196), Gold = Color.FromArgb(255, 200, 96);
    private readonly RadioButton usb = new() { Text = "USB", Checked = true, Appearance = Appearance.Button, TextAlign = ContentAlignment.MiddleCenter, AutoSize = false, Width = 130, Height = 42 };
    private readonly RadioButton bluetooth = new() { Text = "Bluetooth", Appearance = Appearance.Button, TextAlign = ContentAlignment.MiddleCenter, AutoSize = false, Width = 150, Height = 42 };
    private readonly Button usbSetup = new() { Name = "UsbSetup", AutoSize = false, Width = 230, Height = 42 };
    private readonly Button scan = new() { AutoSize = true };
    private readonly ComboBox devices = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 265 };
    private readonly TextBox code = new() { MaxLength = 6, Width = 110, UseSystemPasswordChar = true, PlaceholderText = "••••••" };
    private readonly Button connect = new() { AutoSize = true };
    private readonly Button disconnect = new() { AutoSize = true, Enabled = false };
    private readonly TextBox folder = new() { ReadOnly = true, Dock = DockStyle.Fill };
    private readonly Button chooseFolder = new() { Text = "…", Width = 44, Height = 32 };
    private readonly DataGridView albums = new() { Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false, ReadOnly = true, MultiSelect = true, SelectionMode = DataGridViewSelectionMode.FullRowSelect, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, RowHeadersVisible = false, BorderStyle = BorderStyle.None };
    private readonly Label status = new() { AutoSize = false, Dock = DockStyle.Fill, Height = 48 };
    private readonly Label hint = new() { AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ProgressBar progress = new() { Dock = DockStyle.Fill, Maximum = 1000, Height = 12 };
    private readonly Label progressText = new() { AutoSize = true };
    private readonly Button save = new() { AutoSize = false, Width = 200, Height = 48, Enabled = false };
    private readonly Button cancel = new() { AutoSize = false, Width = 120, Height = 48, Enabled = false };
    private readonly Button openFolder = new() { AutoSize = true };
    private CancellationTokenSource? operation;
    private IArchiveTransport? transport;
    private bool closing;
    private bool closeReady;

    private string T(string tr, string en) => turkish ? tr : en;

    public MainWindow()
    {
        Text = "Albiqo Transfer";
        Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        MinimumSize = new Size(800, 660); Size = new Size(1040, 800);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Ink; ForeColor = Color.WhiteSmoke;
        Font = new Font("Segoe UI", 10); AutoScaleMode = AutoScaleMode.Dpi;
        folder.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Albiqo");
        scan.Text = T("Telefonları bul", "Find phones");
        connect.Text = T("Bağlan", "Connect"); disconnect.Text = T("Bağlantıyı kapat", "Disconnect");
        save.Text = T("Seçileni PC'ye kaydet", "Save selected to PC"); cancel.Text = T("Durdur", "Stop");
        openFolder.Text = T("Klasörü aç", "Open folder");
        usbSetup.Text = T("Apple Devices'ı kur", "Install Apple Devices");
        usbSetup.AccessibleDescription = T("USB için gereken Apple sürücüsünü Microsoft Store'dan kur. iTunes gerekmez.", "Install Apple's required USB driver from Microsoft Store. iTunes is not required.");
        save.BackColor = Teal; save.ForeColor = Ink;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(28), RowCount = 9, ColumnCount = 1 };
        layout.ColumnStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.Absolute, 78));
        layout.RowStyles.Add(new(SizeType.Absolute, 50));
        layout.RowStyles.Add(new(SizeType.Absolute, 56));
        layout.RowStyles.Add(new(SizeType.Absolute, 96));
        layout.RowStyles.Add(new(SizeType.Absolute, 48));
        layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.Absolute, 42));
        layout.RowStyles.Add(new(SizeType.Absolute, 28));
        layout.RowStyles.Add(new(SizeType.Absolute, 58));
        var brand = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        brand.Controls.Add(new Label { Text = "Albiqo Transfer", AutoSize = true, Font = new Font("Segoe UI", 24, FontStyle.Bold), ForeColor = Teal });
        brand.Controls.Add(new Label { Text = T("Albümlerin, bilgisayarında.", "Your albums. On your PC."), AutoSize = true, ForeColor = Gold });
        layout.Controls.Add(brand, 0, 0);
        var transportRow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        transportRow.Controls.AddRange([usb, bluetooth, usbSetup]);
        layout.Controls.Add(transportRow, 0, 1);
        layout.Controls.Add(hint, 0, 2);
        var pairing = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
        // Native auto-sized buttons are37px at the supported96dpi/font baseline.
        // Reserve their full height plus container margins instead of clipping their lower edge.
        pairing.RowStyles.Add(new(SizeType.Absolute, 48)); pairing.RowStyles.Add(new(SizeType.Absolute, 48));
        var findRow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        findRow.Controls.AddRange([scan, devices]);
        var codeRow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        codeRow.Controls.AddRange([new Label { Text = T("Telefon kodu", "Phone code"), AutoSize = true, Padding = new Padding(0, 5, 8, 0) }, code, connect, disconnect]);
        pairing.Controls.Add(findRow, 0, 0); pairing.Controls.Add(codeRow, 0, 1);
        layout.Controls.Add(pairing, 0, 3);
        var destination = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4 };
        destination.ColumnStyles.Add(new(SizeType.AutoSize)); destination.ColumnStyles.Add(new(SizeType.Percent, 100)); destination.ColumnStyles.Add(new(SizeType.Absolute, 50)); destination.ColumnStyles.Add(new(SizeType.AutoSize));
        destination.Controls.Add(new Label { Text = T("Kayıt yeri", "Save to"), AutoSize = true, Padding = new Padding(0, 5, 8, 0) }, 0, 0);
        destination.Controls.Add(folder, 1, 0); destination.Controls.Add(chooseFolder, 2, 0); destination.Controls.Add(openFolder, 3, 0);
        layout.Controls.Add(destination, 0, 4);
        albums.BackgroundColor = Panel; albums.DefaultCellStyle.BackColor = Panel; albums.DefaultCellStyle.ForeColor = Color.WhiteSmoke;
        albums.DefaultCellStyle.SelectionBackColor = Color.FromArgb(40, 101, 101); albums.DefaultCellStyle.SelectionForeColor = Color.White;
        albums.ColumnHeadersDefaultCellStyle.BackColor = Ink; albums.ColumnHeadersDefaultCellStyle.ForeColor = Gold;
        albums.EnableHeadersVisualStyles = false; albums.GridColor = Ink; albums.RowTemplate.Height = 44;
        albums.Columns.Add("album", T("Albüm", "Album")); albums.Columns.Add("size", T("Boyut", "Size")); albums.Columns.Add("created", T("Hazırlanma", "Prepared"));
        albums.Columns[0].FillWeight = 60; albums.Columns[1].FillWeight = 18; albums.Columns[2].FillWeight = 22;
        layout.Controls.Add(albums, 0, 5);
        status.ForeColor = Gold; status.Padding = new Padding(0, 10, 0, 0); layout.Controls.Add(status, 0, 6);
        var progressRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        progressRow.ColumnStyles.Add(new(SizeType.Percent, 100)); progressRow.ColumnStyles.Add(new(SizeType.AutoSize));
        progressRow.Controls.Add(progress, 0, 0); progressRow.Controls.Add(progressText, 1, 0); layout.Controls.Add(progressRow, 0, 7);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        actions.Controls.AddRange([save, cancel]); layout.Controls.Add(actions, 0, 8);
        Controls.Add(layout);
        ApplyButtonStyle(layout);
        usb.CheckedChanged += (_, _) => { if (usb.Checked) ChangeTransport(); };
        bluetooth.CheckedChanged += (_, _) => { if (bluetooth.Checked) ChangeTransport(); };
        scan.Click += async (_, _) => await RunAsync(ScanAsync);
        connect.Click += async (_, _) => await RunAsync(ConnectAsync);
        disconnect.Click += async (_, _) => await RunAsync(async _ => await DisconnectAsync());
        save.Click += async (_, _) => await RunAsync(SaveAsync);
        cancel.Click += (_, _) => operation?.Cancel();
        usbSetup.Click += async (_, _) => await RunAsync(_ =>
        {
            Process.Start(new ProcessStartInfo("https://apps.microsoft.com/detail/9NP83LWLPZ9K") { UseShellExecute = true });
            status.Text = T("Apple Devices'ı kurduktan sonra kabloyu yeniden takıp Telefonları bul'a bas.", "After installing Apple Devices, reconnect the cable and click Find phones.");
            return Task.CompletedTask;
        });
        albums.SelectionChanged += (_, _) => RefreshEnabled();
        chooseFolder.Click += (_, _) => { using var dialog = new FolderBrowserDialog { SelectedPath = folder.Text, Description = T("Albiqo albümlerinin kaydedileceği klasör", "Choose where to save Albiqo albums"), UseDescriptionForTitle = true }; if (dialog.ShowDialog(this) == DialogResult.OK) folder.Text = dialog.SelectedPath; };
        openFolder.Click += async (_, _) => await RunAsync(_ => { Directory.CreateDirectory(folder.Text); Process.Start(new ProcessStartInfo("explorer.exe", folder.Text) { UseShellExecute = true }); return Task.CompletedTask; });
        FormClosing += OnFormClosing;
        UpdateHint();
        status.Text = T("Telefonda albümü hazırlayıp PC Aktarımı ekranını açık tut.", "Prepare an album on your phone and keep PC Transfer open.");
    }

    private static void ApplyButtonStyle(Control parent)
    {
        foreach (Control control in parent.Controls)
        {
            if (control is ButtonBase button)
            {
                button.FlatStyle = FlatStyle.Flat;
                button.FlatAppearance.BorderColor = Color.FromArgb(70, 66, 85);
                button.FlatAppearance.BorderSize = 1;
                if (button.BackColor != Teal) button.BackColor = Panel;
                button.Padding = new Padding(8, 3, 8, 3);
            }
            ApplyButtonStyle(control);
        }
    }

    private async void ChangeTransport()
    {
        if (operation is not null) return;
        await DisconnectAsync(); devices.Items.Clear(); UpdateHint();
    }

    private void UpdateHint()
    {
        usbSetup.Visible = usb.Checked;
        hint.Text = usb.Checked
            ? T("İlk USB kullanımı: Apple Devices'ı kur (iTunes gerekmez). Veri kablosunu bağla, iPhone'un kilidini aç ve Güven'e dokun. Aktarımı Albiqo yapar.", "First USB use: install Apple Devices (no iTunes needed). Connect a data cable, unlock your iPhone and tap Trust. Albiqo handles the transfer.")
            : T("Bluetooth'u aç. Telefonda Albiqo PC Aktarımı açık kalsın. Büyük albümler için USB daha hızlıdır.", "Turn Bluetooth on and keep Albiqo PC Transfer open. USB is faster for large albums.");
    }

    private async Task ScanAsync(CancellationToken token)
    {
        await DisconnectAsync(); devices.Items.Clear(); albums.Rows.Clear();
        status.Text = T("Telefon aranıyor…", "Looking for your phone…");
        if (usb.Checked)
        {
            foreach (var device in await new UsbMuxClient().ListAsync(token)) devices.Items.Add(new DeviceChoice(device.Name, device));
        }
        else
        {
            foreach (var phone in await BluetoothArchiveTransport.FindAsync(token)) devices.Items.Add(new DeviceChoice(phone.Name, phone));
        }
        if (devices.Items.Count > 0) devices.SelectedIndex = 0;
        status.Text = devices.Items.Count == 0
            ? usb.Checked
                ? T("USB'de telefon yok. Apple Devices'ta göründüğünü ve veri kablosunu kontrol et; sonra yeniden ara.", "No USB phone found. Check the data cable and whether Apple Devices sees your phone, then scan again.")
                : T("Telefon bulunamadı. Telefonda Bluetooth ve PC Aktarımı ekranını açıp tekrar dene.", "No phone found. Enable Bluetooth and open PC Transfer on your phone, then retry.")
            : T("Telefonundaki altı haneli kodu girip bağlan.", "Enter the six-digit code from your phone and connect.");
    }

    private async Task ConnectAsync(CancellationToken token)
    {
        if (devices.SelectedItem is not DeviceChoice choice) throw new InvalidOperationException(T("Önce telefonu bul.", "Find your phone first."));
        if (code.Text.Length != 6 || code.Text.Any(c => c is < '0' or > '9')) throw new InvalidOperationException(T("Telefonundaki altı haneli kodu gir.", "Enter the six-digit code from your phone."));
        await DisconnectAsync();
        status.Text = T("Bağlanıyor…", "Connecting…");
        var candidate = choice.Device switch
        {
            UsbDevice device => (IArchiveTransport)new UsbArchiveTransport(device),
            NearbyPhone phone => await BluetoothArchiveTransport.ConnectAsync(phone, token),
            _ => throw new InvalidOperationException("Unsupported phone.")
        };
        try
        {
            var session = await candidate.AuthenticateAsync(code.Text, token);
            var catalogue = await candidate.ListAsync(token);
            transport = candidate;
            albums.Rows.Clear();
            foreach (var archive in catalogue.Archives)
            {
                var index = albums.Rows.Add(archive.AlbumName, SizeText(archive.Size), archive.CreatedAt.LocalDateTime.ToString("g"));
                albums.Rows[index].Tag = archive;
            }
            code.Clear();
            status.Text = T($"{session.DeviceName} bağlandı · {catalogue.Archives.Length} albüm hazır", $"Connected to {session.DeviceName} · {catalogue.Archives.Length} albums ready");
        }
        catch { await candidate.DisposeAsync(); throw; }
    }

    private async Task SaveAsync(CancellationToken token)
    {
        if (transport is null) return;
        var selected = albums.SelectedRows.Cast<DataGridViewRow>().OrderBy(r => r.Index).Select(r => r.Tag).OfType<ArchiveInfo>().ToArray();
        if (selected.Length == 0) return;
        var reporter = new Progress<TransferProgress>(p =>
        {
            if (IsDisposed || closing) return;
            progress.Value = p.Total == 0 ? 0 : (int)Math.Clamp(p.Completed * 1000.0 / p.Total, 0, 1000);
            progressText.Text = p.Stage == "verify" ? T("Doğrulanıyor…", "Verifying…") : $"{SizeText(p.Completed)} / {SizeText(p.Total)}";
        });
        var downloader = new ArchiveDownloader();
        foreach (var archive in selected)
        {
            status.Text = T($"{archive.AlbumName} kaydediliyor…", $"Saving {archive.AlbumName}…");
            await downloader.DownloadAsync(transport, archive, folder.Text, reporter, token);
        }
        status.Text = T($"{selected.Length} albüm PC'ye kaydedildi. Telefonda hiçbir öğe silinmedi.", $"Saved {selected.Length} albums to your PC. Nothing was deleted from your phone.");
        progressText.Text = T("Tamamlandı", "Complete");
    }

    private async Task DisconnectAsync()
    {
        var previous = transport; transport = null;
        if (previous is not null) await previous.DisposeAsync();
        albums.Rows.Clear(); RefreshEnabled();
    }

    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (operation is not null || closing) return;
        operation = new CancellationTokenSource(); RefreshEnabled();
        try { await action(operation.Token); }
        catch (OperationCanceledException)
        {
            await DisconnectAsync();
            status.Text = T("Durduruldu. Yeniden bağlanınca eksik dosyadan devam edebilirsin.", "Stopped. Reconnect to resume the partial download.");
        }
        catch (Exception error)
        {
            status.Text = T("İşlem tamamlanamadı.", "Could not complete the transfer.");
            if (!closing) MessageBox.Show(this, ErrorText(error), "Albiqo Transfer", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        finally
        {
            operation.Dispose(); operation = null;
            if (closing) { await DisconnectAsync(); closeReady = true; Close(); }
            else RefreshEnabled();
        }
    }

    private void RefreshEnabled()
    {
        if (IsDisposed) return;
        var idle = operation is null && !closing;
        scan.Enabled = idle; connect.Enabled = idle && transport is null;
        disconnect.Enabled = idle && transport is not null;
        usb.Enabled = idle; bluetooth.Enabled = idle; devices.Enabled = idle; code.Enabled = idle;
        chooseFolder.Enabled = idle; save.Enabled = idle && transport is not null && albums.SelectedRows.Count > 0;
        usbSetup.Enabled = idle;
        cancel.Enabled = operation is not null && !closing;
    }

    private string ErrorText(Exception error)
    {
        // HttpClient can wrap a failed USB connection in HttpRequestException.
        UsbMuxException? usbError = null;
        for (Exception? cause = error; cause is not null; cause = cause.InnerException)
            if (cause is UsbMuxException failure) { usbError = failure; break; }
        return usbError is not null ? usbError.Failure switch
        {
        UsbMuxFailure.ServiceUnavailable => T("Apple USB servisine ulaşılamıyor. Yukarıdaki Apple Devices'ı kur düğmesiyle kurulumu tamamla. Kuruluysa Apple Devices'ı açıp telefonu kontrol et; kabloyu yeniden tak ve tekrar ara. iTunes gerekmez.", "Apple's USB service is unavailable. Use Install Apple Devices above. If it is already installed, open Apple Devices to check your phone, reconnect the cable and scan again. iTunes is not required."),
        UsbMuxFailure.ResponseTimeout => T("Apple USB servisi zamanında yanıt vermedi. Telefonun kilidini açık tut, kabloyu yeniden tak ve Telefonları bul'a bas. Apple Devices içinde de görünmüyorsa USB bağlantısını kontrol et.", "Apple's USB service did not respond in time. Keep your phone unlocked, reconnect the cable and click Find phones. Check the USB connection if Apple Devices cannot see it either."),
        UsbMuxFailure.UnsupportedProtocol => T("Bu Apple USB servisiyle bağlantı kurulamadı. Apple Devices'ı güncelle ve yeniden dene.", "This Apple USB service could not be used. Update Apple Devices and try again."),
        _ => error.Message
        } : error.Message;
    }

    private async void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (closeReady) return;
        e.Cancel = true; closing = true;
        if (operation is not null) { operation.Cancel(); return; }
        await DisconnectAsync(); closeReady = true; Close();
    }

    private static string SizeText(long size) => size >= 1024L * 1024 * 1024 ? $"{size / (1024.0 * 1024 * 1024):0.##} GB" : $"{size / (1024.0 * 1024):0.##} MB";
    private sealed record DeviceChoice(string Name, object Device) { public override string ToString() => Name; }
}
