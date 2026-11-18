using System.Diagnostics;
using System.Security.Principal;
using System.Windows.Forms;
using CxPrint.Core;
using CxPrint.Windows;
using Microsoft.Win32;

namespace CxPrint.Win7;

internal sealed class MainForm : Form
{
    private sealed class PrinterRow(PrinterInfo printer, string? media, string? displayName)
    {
        public PrinterInfo Printer { get; } = printer;
        public string? Media { get; set; } = media;
        public string? DisplayName { get; set; } = displayName;
    }

    private readonly PrinterScanner _scanner;
    private readonly Func<ControlRequest, CancellationToken, Task<ControlReply>> _send;
    private readonly TimeSpan _statusTimeout;
    private readonly System.Windows.Forms.Timer _scanTimer = new() { Interval = 500 };
    private PrinterScanSnapshot? _displayedScan;
    private bool _useLocalScan;
    private bool _refreshInProgress;
    private bool _installing;
    private string? _connectionError;
    private DateTimeOffset _nextStatusPoll;
    internal string StatusText => _statusText.Text;
    internal bool ServiceButtonEnabled => _service.Enabled;
    internal int PrinterCount => _printers.Items.Count;
    private readonly ListView _printers = new();
    private readonly ComboBox _media = new();
    private readonly TextBox _displayName = new();
    private readonly Button _refresh = new();
    private readonly Button _service = new();
    private readonly Button _save = new();
    private readonly ToolStripStatusLabel _statusText = new();
    private BridgeStatus? _bridge;
    private bool _updatingDetails;

    public MainForm(PrinterScanner? scanner = null,
        Func<ControlRequest, CancellationToken, Task<ControlReply>>? send = null, TimeSpan? statusTimeout = null)
    {
        _scanner = scanner ?? PrinterProbe.CreateScanner(Path.Combine(Application.StartupPath, "Bridge", "CxPrint.Service.exe"));
        _send = send ?? ControlClient.SendAsync;
        _statusTimeout = statusTimeout ?? TimeSpan.FromSeconds(4);
        Text = "CX Printer Legacy";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        MinimumSize = new Size(720, 470);
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(900, 590);
        BuildLayout();
        Shown += async (_, _) => { _scanTimer.Start(); await RefreshPrintersAsync(); };
        _scanTimer.Tick += async (_, _) =>
        {
            if (_refreshInProgress || _installing) return;
            UpdateLocalScan();
            if (_bridge?.PrintersRefreshing == true && DateTimeOffset.UtcNow >= _nextStatusPoll)
                await RefreshPrintersAsync(false);
        };
        FormClosed += (_, _) => _scanTimer.Stop();
        _refresh.Click += async (_, _) => await RefreshPrintersAsync();
        _service.Click += async (_, _) => await InstallServiceAsync();
        _save.Click += async (_, _) => await SaveSettingsAsync();
        _printers.SelectedIndexChanged += (_, _) => ShowSelection();
        _media.SelectedIndexChanged += (_, _) => UpdateSelectedMedia();
        _displayName.TextChanged += (_, _) => UpdateSelectedName();
    }

    private void BuildLayout()
    {
        var actions = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(8, 5, 8, 4), WrapContents = false };
        _refresh.Text = "刷新打印机";
        _service.Text = "安装 / 修复后台服务";
        _save.Text = "保存共享设置";
        foreach (var button in new[] { _refresh, _service, _save })
        {
            button.AutoSize = true;
            button.Height = 30;
            actions.Controls.Add(button);
        }

        _printers.Dock = DockStyle.Fill;
        _printers.CheckBoxes = true;
        _printers.FullRowSelect = true;
        _printers.HideSelection = false;
        _printers.MultiSelect = false;
        _printers.View = View.Details;
        _printers.Columns.Add("Windows 打印机", 220);
        _printers.Columns.Add("驱动与能力", 190);
        _printers.Columns.Add("共享纸张", 150);

        var editor = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), ColumnCount = 1, RowCount = 6 };
        editor.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        editor.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        editor.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        editor.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        editor.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        editor.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        editor.Controls.Add(new Label { Text = "Apple 设备上显示的名称", AutoSize = true, Margin = new Padding(0, 0, 0, 5) }, 0, 0);
        _displayName.Dock = DockStyle.Top;
        editor.Controls.Add(_displayName, 0, 1);
        editor.Controls.Add(new Label { Text = "默认纸张", AutoSize = true, Margin = new Padding(0, 18, 0, 5) }, 0, 2);
        _media.Dock = DockStyle.Top;
        _media.DropDownStyle = ComboBoxStyle.DropDownList;
        editor.Controls.Add(_media, 0, 3);
        editor.Controls.Add(new Label
        {
            Text = "勾选要共享的打印机。纸张和其他打印选项以 Windows 驱动支持的能力为准。",
            AutoSize = true,
            MaximumSize = new Size(260, 0),
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 18, 0, 0)
        }, 0, 4);

        // Size the container before applying panel minima: its default width is only 150 pixels.
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Size = ClientSize,
            Panel1MinSize = 340,
            Panel2MinSize = 250,
            SplitterDistance = 560
        };
        split.Panel1.Controls.Add(_printers);
        split.Panel2.Controls.Add(editor);

        var strip = new StatusStrip { ShowItemToolTips = true };
        _statusText.Spring = true;
        _statusText.TextAlign = ContentAlignment.MiddleLeft;
        strip.Items.Add(_statusText);
        Controls.Add(split);
        Controls.Add(actions);
        Controls.Add(strip);
    }

    internal async Task RefreshPrintersAsync(bool startScan = true)
    {
        if (_refreshInProgress || IsDisposed) return;
        _refreshInProgress = true;
        _refresh.Enabled = false;
        _save.Enabled = false;
        _statusText.Text = "正在连接打印服务（最多 4 秒）…";
        try
        {
            using var timeout = new CancellationTokenSource(_statusTimeout);
            try
            {
                // Bound the wait even if a pipe operation ignores cancellation on an older OS.
                var reply = await _send(new ControlRequest("status"), timeout.Token).WaitAsync(_statusTimeout);
                _bridge = reply.Success ? reply.Status : null;
                _connectionError = reply.Success ? null : reply.Error;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _bridge = null;
                _connectionError = ex is TimeoutException or OperationCanceledException ? "连接超时" : ex.Message;
            }
            if (IsDisposed) return;
            _useLocalScan = _bridge is null || (_bridge.PrintersRefreshing && _bridge.Printers.Count == 0);
            if (_useLocalScan && startScan)
            {
                // Native printer drivers may never return. Do not await the whole scan on the UI.
                _ = _scanner.RefreshAsync();
                _displayedScan = null;
            }
            PopulatePrinters();
            UpdateStatus();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!IsDisposed) _statusText.Text = ex.Message;
        }
        finally
        {
            _refreshInProgress = false;
            _nextStatusPoll = DateTimeOffset.UtcNow.AddSeconds(3);
            if (!IsDisposed) SetBusy(_installing);
        }
    }

    internal void UpdateLocalScan()
    {
        if (!_useLocalScan || IsDisposed) return;
        var snapshot = _scanner.Snapshot;
        if (!ReferenceEquals(snapshot, _displayedScan))
        {
            PopulatePrinters();
            _displayedScan = snapshot;
        }
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        var connection = _bridge is null
            ? "后台服务未连接" + (string.IsNullOrWhiteSpace(_connectionError) ? "" : $"（{_connectionError}）") + "；可点击安装 / 修复后台服务。"
            : $"后台服务运行中 · IPP 端口 {_bridge.Port}。";
        var scanMessage = _useLocalScan ? _scanner.Snapshot.Message : _bridge?.PrinterScanStatus;
        _statusText.Text = connection + " " + (scanMessage ?? $"已读取 {_printers.Items.Count} 台打印机。");
        _statusText.ToolTipText = _statusText.Text;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _scanTimer.Dispose();
        base.Dispose(disposing);
    }

    private void PopulatePrinters()
    {
        var previous = _printers.Items.Cast<ListViewItem>()
            .Select(item => item.Tag as PrinterRow)
            .Where(row => row is not null)
            .ToDictionary(row => row!.Printer.Id, row => row!);
        var checkedIds = _printers.Items.Cast<ListViewItem>().Where(item => item.Checked)
            .Select(item => ((PrinterRow)item.Tag!).Printer.Id).ToHashSet();
        var selectedId = SelectedRow()?.Printer.Id;
        var saved = _bridge?.SharedPrinters.ToDictionary(printer => printer.Id);
        var printers = _useLocalScan ? _scanner.Snapshot.Printers : _bridge?.Printers ?? _scanner.Snapshot.Printers;

        _printers.BeginUpdate();
        _printers.Items.Clear();
        foreach (var printer in printers.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            string? media = printer.DefaultMedia;
            string? displayName = null;
            var shared = false;
            if (previous.TryGetValue(printer.Id, out var old))
            {
                media = old.Media ?? media;
                displayName = old.DisplayName;
                shared = checkedIds.Contains(printer.Id);
            }
            else if (saved is not null && saved.TryGetValue(printer.Id, out var existing))
            {
                shared = true;
                media = existing.DefaultMedia ?? media;
                displayName = existing.DisplayName;
            }
            var row = new PrinterRow(printer, media, displayName);
            var item = new ListViewItem(printer.Name) { Checked = shared, Tag = row };
            item.SubItems.Add(printer.Summary);
            item.SubItems.Add(SelectedMediaName(row));
            _printers.Items.Add(item);
            if (printer.Id == selectedId) item.Selected = true;
        }
        _printers.EndUpdate();
        if (_printers.Items.Count > 0 && _printers.SelectedItems.Count == 0) _printers.Items[0].Selected = true;
        else ShowSelection();
    }

    private void ShowSelection()
    {
        var row = SelectedRow();
        _updatingDetails = true;
        try
        {
            _displayName.Text = row?.DisplayName ?? "";
            _media.DataSource = null;
            _media.Items.Clear();
            if (row is not null)
            {
                _media.DisplayMember = nameof(MediaSize.Description);
                _media.ValueMember = nameof(MediaSize.Key);
                _media.DataSource = row.Printer.Media.ToList();
                _media.SelectedValue = row.Media ?? row.Printer.DefaultMedia;
                _media.Enabled = row.Printer.Media.Count > 0;
            }
            else _media.Enabled = false;
            _displayName.Enabled = row is not null;
        }
        finally { _updatingDetails = false; }
    }

    private void UpdateSelectedMedia()
    {
        if (_updatingDetails || SelectedRow() is not { } row || _media.SelectedItem is not MediaSize media) return;
        row.Media = media.Key;
        if (_printers.SelectedItems.Count > 0) _printers.SelectedItems[0].SubItems[2].Text = media.DisplayName;
    }

    private void UpdateSelectedName()
    {
        if (!_updatingDetails && SelectedRow() is { } row)
            row.DisplayName = string.IsNullOrWhiteSpace(_displayName.Text) ? null : _displayName.Text.Trim();
    }

    private string SelectedMediaName(PrinterRow row) =>
        row.Printer.Media.FirstOrDefault(item => item.Key == row.Media)?.DisplayName ?? "驱动默认";

    private PrinterRow? SelectedRow() => _printers.SelectedItems.Count == 0
        ? null
        : _printers.SelectedItems[0].Tag as PrinterRow;

    private async Task SaveSettingsAsync()
    {
        if (_bridge is null)
        {
            MessageBox.Show(this, "请先安装或启动后台服务，再保存共享设置。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var shared = _printers.Items.Cast<ListViewItem>()
            .Where(item => item.Checked)
            .Select(item => (PrinterRow)item.Tag!)
            .Select(row => new SharedPrinter(row.Printer.Id, row.Printer.Name, row.Media, row.DisplayName))
            .ToList();
        if (shared.Count == 0)
        {
            MessageBox.Show(this, "至少勾选一台打印机。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        SetBusy(true);
        try
        {
            var settings = new ApplySettings(shared, _bridge.AuthenticationEnabled, _bridge.Username, Enabled: true);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var reply = await _send(new ControlRequest("apply", settings), timeout.Token).WaitAsync(TimeSpan.FromSeconds(20));
            if (!reply.Success) throw new InvalidOperationException(reply.Error ?? "保存失败。");
            _bridge = reply.Status ?? _bridge;
            PopulatePrinters();
            _statusText.Text = "共享设置已保存，打印机现已通过局域网发布。";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            MessageBox.Show(this, ex.Message, "保存失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { SetBusy(false); }
    }

    private async Task InstallServiceAsync()
    {
        var executable = Path.Combine(Application.StartupPath, "Bridge", "CxPrint.Service.exe");
        if (!File.Exists(executable))
        {
            MessageBox.Show(this, "缺少 Bridge\\CxPrint.Service.exe。请重新解压完整安装包。", Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        _installing = true;
        SetBusy(true);
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(executable)!
            };
            start.ArgumentList.Add("--install");
            start.ArgumentList.Add("--owner");
            start.ArgumentList.Add(identity.User!.Value);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动后台安装程序。");
            await process.WaitForExitAsync();
            if (process.ExitCode != 0) throw new InvalidOperationException($"后台服务安装失败，退出代码 {process.ExitCode}。");
            await RefreshPrintersAsync();
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            _statusText.Text = "已取消管理员授权。";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            MessageBox.Show(this, ex.Message, "后台服务安装失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { _installing = false; SetBusy(false); }
    }

    private void SetBusy(bool busy)
    {
        _refresh.Enabled = !busy && !_refreshInProgress;
        _service.Enabled = !busy;
        _save.Enabled = !busy && !_refreshInProgress && _bridge is not null;
    }
}
