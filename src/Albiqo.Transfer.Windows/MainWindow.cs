using Albiqo.Transfer.Core;
using System.Diagnostics;
using System.Globalization;

namespace Albiqo.Transfer.Windows;

public sealed partial class MainWindow : Form
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
    private readonly Label status = new() { AutoSize = false, Dock = DockStyle.Fill, Height = 48, AutoEllipsis = true };
    private readonly Label hint = new() { AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ProgressBar progress = new() { Dock = DockStyle.Fill, Maximum = 1000, Height = 12 };
    private readonly Label progressText = new() { AutoSize = true };
    private readonly Button save = new() { AutoSize = false, Width = 200, Height = 48, Enabled = false };
    private readonly Button cancel = new() { AutoSize = false, Width = 120, Height = 48, Enabled = false };
    private readonly Button openFolder = new() { AutoSize = true };
    private CancellationTokenSource? operation;
    private IArchiveTransport? transport;
    private ConnectionPairingCode? pairingCode;
    private UsbAttachmentWatch? usbAttachment;
    private CancellationToken connectionLost;
    private CancellationTokenRegistration connectionLostRegistration;
    private Guid connectionGeneration = Guid.NewGuid();
    private string? sessionId;
    private readonly System.Windows.Forms.Timer heartbeat = new() { Interval = 60_000 };
    private int protocolVersion = 1;
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
        disconnect.Click += async (_, _) => await RunAsync(async _ =>
        {
            await DisconnectAsync();
            status.Text = T("Bağlantı kapatıldı. Yeniden bağlanırken kod istenir.", "Disconnected. A code is required to connect again.");
        });
        save.Click += async (_, _) => await RunAsync(SaveAsync);
        cancel.Click += (_, _) => operation?.Cancel();
        usbSetup.Click += async (_, _) => await RunAsync(_ =>
        {
            Process.Start(new ProcessStartInfo("https://apps.microsoft.com/detail/9NP83LWLPZ9K") { UseShellExecute = true });
            status.Text = T("Apple Devices'ı kurduktan sonra kabloyu yeniden takıp Telefonları bul'a bas.", "After installing Apple Devices, reconnect the cable and click Find phones.");
            return Task.CompletedTask;
        });
        albums.SelectionChanged += (_, _) => RefreshEnabled();
        heartbeat.Tick += async (_, _) =>
        {
            if (operation is null && transport is not null && pairingCode?.IsActive == true)
                await RunAsync(token => RefreshConnectionAsync(token, refreshCatalogue: false), showErrors: false);
        };
        chooseFolder.Click += (_, _) => { using var dialog = new FolderBrowserDialog { SelectedPath = folder.Text, Description = T("Albiqo albümlerinin kaydedileceği klasör", "Choose where to save Albiqo albums"), UseDescriptionForTitle = true }; if (dialog.ShowDialog(this) == DialogResult.OK) folder.Text = dialog.SelectedPath; };
        openFolder.Click += async (_, _) => await RunAsync(_ => { Directory.CreateDirectory(folder.Text); Process.Start(new ProcessStartInfo("explorer.exe", folder.Text) { UseShellExecute = true }); return Task.CompletedTask; });
        FormClosing += OnFormClosing;
        UpdateHint();
        status.Text = T("Telefonda aktarılacak albümü seçip PC Aktarımı ekranını açık tut.", "Choose an album on your phone and keep PC Transfer open.");
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
        await RunAsync(async _ =>
        {
            await DisconnectAsync(); devices.Items.Clear(); UpdateHint();
            status.Text = T("Telefonunda aktarılacak albümü seçip telefonu bul.", "Choose an album on your phone, then find your phone.");
        });
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
        if (transport is not null && pairingCode?.IsActive == true)
        {
            await RefreshConnectionAsync(token, refreshCatalogue: true);
            return;
        }
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
        var enteredCode = code.Text;
        await DisconnectAsync();
        status.Text = T("Bağlanıyor…", "Connecting…");
        UsbAttachmentWatch? watch = null;
        IArchiveTransport? candidate = null;
        try
        {
            if (choice.Device is UsbDevice device)
            {
                watch = await new UsbMuxClient().WatchAttachmentAsync(device, token);
                candidate = new UsbArchiveTransport(device);
            }
            else if (choice.Device is NearbyPhone phone)
                candidate = await BluetoothArchiveTransport.ConnectAsync(phone, token);
            else throw new InvalidOperationException("Unsupported phone.");
            var lost = watch?.Disconnected ?? ((BluetoothArchiveTransport)candidate).ConnectionLost;
            using var connectedOperation = CancellationTokenSource.CreateLinkedTokenSource(token, lost);
            var session = await candidate.AuthenticateAsync(enteredCode, connectedOperation.Token);
            await LoadCatalogueAsync(candidate, session, connectedOperation.Token);
            lost.ThrowIfCancellationRequested();
            RememberConnection(candidate, watch, lost, enteredCode, session);
        }
        catch
        {
            if (candidate is not null) await candidate.DisposeAsync();
            if (watch is not null) await watch.DisposeAsync();
            throw;
        }
    }

    private void RememberConnection(IArchiveTransport candidate, UsbAttachmentWatch? watch, CancellationToken lost, string enteredCode, TransferSession session)
    {
        transport = candidate;
        usbAttachment = watch;
        connectionLost = lost;
        pairingCode = new ConnectionPairingCode(enteredCode, lost);
        sessionId = session.SessionId;
        var generation = connectionGeneration = Guid.NewGuid();
        connectionLostRegistration = lost.Register(() =>
        {
            if (closing || IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke((Action)(async () =>
            {
                if (connectionGeneration != generation || closing) return;
                if (operation is not null) { operation.Cancel(); return; }
                await RunAsync(async _ =>
                {
                    await DisconnectAsync();
                    status.Text = T("Telefon bağlantısı kesildi. Yeniden bağlanmak için kodu gir.", "Phone disconnected. Enter the code to connect again.");
                }, showErrors: false);
            })); }
            catch (InvalidOperationException) { }
        });
        code.Clear();
        code.PlaceholderText = T("Eşleşti", "Paired");
        heartbeat.Start();
    }

    private async Task RefreshConnectionAsync(CancellationToken token, bool refreshCatalogue)
    {
        if (transport is null || pairingCode is null || !pairingCode.TryGet(out var pin))
            throw new OperationCanceledException(connectionLost);
        if (transport is BluetoothArchiveTransport bluetoothTransport && refreshCatalogue)
            await bluetoothTransport.RefreshServiceAsync(token);
        var session = await transport.AuthenticateAsync(pin, token);
        // A newly selected album has a new engine, but the same foreground pairing lease.
        if (refreshCatalogue || sessionId != session.SessionId)
            await LoadCatalogueAsync(transport, session, token);
        sessionId = session.SessionId;
    }

    private async Task LoadCatalogueAsync(IArchiveTransport candidate, TransferSession session, CancellationToken token)
    {
        var selectedIds = albums.SelectedRows.Cast<DataGridViewRow>().Select(row => row.Tag switch
        {
            MediaAlbum album => album.Id, ArchiveInfo archive => archive.Id, _ => ""
        }).ToHashSet(StringComparer.Ordinal);
        protocolVersion = session.ProtocolVersion;
        ConfigureColumns(protocolVersion == 2);
        if (protocolVersion == 2 && candidate is IMediaTransport media)
        {
            var catalogue = await media.CatalogueAsync(token);
            albums.Rows.Clear();
            foreach (var album in catalogue.Albums)
            {
                var index = albums.Rows.Add(album.Name, album.AssetCount.ToString("N0"), album.FileCount.ToString("N0"));
                albums.Rows[index].Tag = album;
                if (selectedIds.Count > 0) albums.Rows[index].Selected = selectedIds.Contains(album.Id);
            }
            status.Text = T($"{session.DeviceName} bağlandı · {catalogue.Albums.Length} albüm · kod bu bağlantıda hatırlanır", $"Connected to {session.DeviceName} · {catalogue.Albums.Length} albums · code remembered for this connection");
        }
        else
        {
            var catalogue = await candidate.ListAsync(token);
            albums.Rows.Clear();
            foreach (var archive in catalogue.Archives)
            {
                var index = albums.Rows.Add(archive.AlbumName, SizeText(archive.Size), archive.CreatedAt.LocalDateTime.ToString("g"));
                albums.Rows[index].Tag = archive;
                if (selectedIds.Count > 0) albums.Rows[index].Selected = selectedIds.Contains(archive.Id);
            }
            status.Text = T($"{session.DeviceName} bağlandı · eski sürüm uyumluluğu · {catalogue.Archives.Length} paket", $"Connected to {session.DeviceName} · legacy compatibility · {catalogue.Archives.Length} packages");
        }
    }

    private async Task SaveAsync(CancellationToken token)
    {
        if (transport is null) return;
        if (protocolVersion == 2 && transport is IMediaTransport media) { await SaveMediaAsync(media, token); return; }
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

    private void ConfigureColumns(bool direct)
    {
        albums.Columns[1].HeaderText = direct ? T("Öğe", "Items") : T("Boyut", "Size");
        albums.Columns[2].HeaderText = direct ? T("Dosya", "Files") : T("Hazırlanma", "Prepared");
    }

    // Deterministic presentation fixture for the Windows runner; never discovers or connects a phone.
    internal void ShowDirectTransferSmokeState()
    {
        ConfigureColumns(true); albums.Rows.Clear();
        albums.Rows.Add(T("İstanbul Tatili", "Istanbul Holiday"), "24", "27");
        status.Text = T("Kaydediliyor: IMG_2021.HEIC · 256 KB", "Saving: IMG_2021.HEIC · 256 KB");
        progress.Value = 370;
        progressText.Text = T("10/27 dosya · 25,6 MB", "10/27 files · 25.6 MB");
    }

    private async Task SaveMediaAsync(IMediaTransport media, CancellationToken token)
    {
        var selected = albums.SelectedRows.Cast<DataGridViewRow>().OrderBy(row => row.Index).Select(row => row.Tag).OfType<MediaAlbum>().ToArray();
        if (selected.Length == 0) return;
        var downloader = new PCMediaDownloader();
        var reporter = new Progress<MediaTransferProgress>(p =>
        {
            if (IsDisposed || closing) return;
            progress.Value = p.TotalFiles == 0 ? 0 : (int)Math.Clamp(p.CompletedFiles * 1000.0 / p.TotalFiles, 0, 1000);
            progressText.Text = T($"{p.CompletedFiles}/{p.TotalFiles} dosya · {SizeText(p.BytesTransferred)}", $"{p.CompletedFiles}/{p.TotalFiles} files · {SizeText(p.BytesTransferred)}");
            status.Text = p.Stage == "checking"
                ? T($"Kontrol ediliyor: {p.FileName}", $"Checking: {p.FileName}")
                : p.Stage is "complete" ? T("Dosyalar doğrulandı.", "Files verified.")
                : T($"Kaydediliyor: {p.FileName} · {SizeText(p.CurrentFileBytes)}", $"Saving: {p.FileName} · {SizeText(p.CurrentFileBytes)}");
        });
        var saved = 0; var skipped = 0;
        foreach (var album in selected)
        {
            var result = await downloader.DownloadAlbumAsync(media, album, folder.Text, reporter, token);
            saved += result.SavedFiles; skipped += result.SkippedFiles;
        }
        status.Text = T($"{saved} dosya kaydedildi · {skipped} dosya zaten tamamlanmış. Telefonda hiçbir öğe silinmedi.", $"Saved {saved} files · {skipped} already complete. Nothing was deleted from your phone.");
        progress.Value = 1000; progressText.Text = T("Tamamlandı", "Complete");
    }

    private async Task DisconnectAsync()
    {
        connectionGeneration = Guid.NewGuid(); heartbeat.Stop();
        connectionLostRegistration.Dispose();
        pairingCode?.Dispose(); pairingCode = null;
        var watch = usbAttachment; usbAttachment = null;
        var previous = transport; transport = null; protocolVersion = 1; ConfigureColumns(false);
        sessionId = null; connectionLost = CancellationToken.None;
        code.Clear(); code.PlaceholderText = "••••••";
        if (previous is not null) await previous.DisposeAsync();
        if (watch is not null) await watch.DisposeAsync();
        albums.Rows.Clear(); RefreshEnabled();
    }

    private async Task RunAsync(Func<CancellationToken, Task> action, bool showErrors = true)
    {
        if (operation is not null || closing) return;
        operation = CancellationTokenSource.CreateLinkedTokenSource(connectionLost); RefreshEnabled();
        try { await action(operation.Token); }
        catch (OperationCanceledException)
        {
            if (transport is not null && pairingCode?.IsActive == true)
                status.Text = T("Durduruldu. Bağlantın açık; kod girmeden devam edebilirsin.", "Stopped. Still connected; resume without entering the code again.");
            else
            {
                await DisconnectAsync();
                status.Text = T("Bağlantı kapandı. Telefonu bulup kodla yeniden bağlan.", "Connection closed. Find your phone and enter its code again.");
            }
        }
        catch (PairingCodeRejectedException error)
        {
            await DisconnectAsync();
            status.Text = T("Telefon yeni bir eşleştirme istiyor. Ekranındaki güncel kodu gir.", "Your phone needs a new pairing. Enter its current code.");
            if (showErrors && !closing) MessageBox.Show(this, error.Message, "Albiqo Transfer", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception error)
        {
            status.Text = pairingCode?.IsActive == true
                ? T("Telefonda albümün aktarım ekranını açıp Albümleri yenile'ye bas. Eşleştirmen korunuyor.", "Open the album's transfer screen on your phone, then refresh albums. Your pairing is kept.")
                : T("İşlem tamamlanamadı.", "Could not complete the transfer.");
            if (showErrors && !closing) MessageBox.Show(this, ErrorText(error), "Albiqo Transfer", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        finally
        {
            operation.Dispose(); operation = null;
            if (closing) { await DisconnectAsync(); closeReady = true; Close(); }
            else
            {
                if (transport is not null && pairingCode?.IsActive != true)
                {
                    await DisconnectAsync();
                    status.Text = T("Telefon bağlantısı kesildi. Yeniden bağlanmak için kodu gir.", "Phone disconnected. Enter the code to connect again.");
                }
                RefreshEnabled();
            }
        }
    }

    private void RefreshEnabled()
    {
        if (IsDisposed) return;
        var idle = operation is null && !closing;
        scan.Enabled = idle; connect.Enabled = idle && transport is null;
        scan.Text = transport is null ? T("Telefonları bul", "Find phones") : T("Albümleri yenile", "Refresh albums");
        connect.Text = transport is null ? T("Bağlan", "Connect") : T("Bağlandı", "Connected");
        disconnect.Enabled = idle && transport is not null;
        usb.Enabled = idle; bluetooth.Enabled = idle; devices.Enabled = idle && transport is null; code.Enabled = idle && transport is null;
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
        e.Cancel = true; closing = true; heartbeat.Stop();
        if (operation is not null) { operation.Cancel(); return; }
        await DisconnectAsync(); closeReady = true; Close();
    }

    private static string SizeText(long size) => size >= 1024L * 1024 * 1024 ? $"{size / (1024.0 * 1024 * 1024):0.##} GB" : $"{size / (1024.0 * 1024):0.##} MB";
    private sealed record DeviceChoice(string Name, object Device) { public override string ToString() => Name; }
}
