using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PrusaConnect.Core.Models;
using PrusaConnect.Core.PrusaConnect;
using PrusaConnect.Core.PrusaLink;
using PrusaConnect.Core.Storage;
using PrusaConnect.Core.Update;
using PrusaConnect.Widget.Diagnostics;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI;

namespace PrusaConnect.Widget.Settings;

public sealed partial class SettingsWindow : Window
{
    private readonly PrinterStore _printers;
    private readonly ISecretsStore _secrets;
    private readonly ConnectTokenCache _connectTokens;
    private readonly PinnedWidgetStore _pinnedWidgets;
    private readonly RebindRequestStore _rebinds;
    private readonly ConnectAccount _cloudAccount = ConnectAccountDefaults.PrusaConnectCloud;
    private static readonly Color RowBorderColor = Color.FromArgb(40, 255, 255, 255);

    // Farm teams (Connect Farm organizations) + regular Connect teams for the
    // per-tile pickers. Loaded lazily from Prusa Connect; until then empty.
    private List<(string Id, string Name)> _farmOrgs = new();
    private List<(string Id, string Name)> _connectTeams = new();

    // Set when the update check finds a newer release that ships an installer zip.
    private string? _updateInstallerUrl;

    public SettingsWindow()
    {
        InitializeComponent();

        _printers = new PrinterStore(AppPaths.PrintersDirectory);
        _secrets = new DpapiSecretsStore(AppPaths.SecretsDirectory);
        _connectTokens = new ConnectTokenCache(_secrets);
        _pinnedWidgets = new PinnedWidgetStore(AppPaths.PinnedWidgetsDirectory);
        _rebinds = new RebindRequestStore(AppPaths.PinnedWidgetsDirectory);

        RebuildSavedPrintersList();
        RebuildPinnedTilesList();
        // RefreshCloudPrintersAsync loads farm teams on success, so signing in
        // mid-session refreshes the per-tile dropdowns without a restart.
        _ = RefreshCloudPrintersAsync();
        _ = CheckForUpdatesAsync();
    }

    // -- Auto-update check ----------------------------------------------------

    /// <summary>
    /// Ask GitHub whether there's a newer release and, if so, show the update
    /// banner. Best-effort: a failed check is silent. Also fills the footer
    /// version line.
    /// </summary>
    private async Task CheckForUpdatesAsync()
    {
        Version current;
        try
        {
            var pv = Windows.ApplicationModel.Package.Current.Id.Version;
            current = new Version(pv.Major, pv.Minor, pv.Build, pv.Revision);
            VersionText.Text = $"Version {pv.Major}.{pv.Minor}.{pv.Build}";
        }
        catch { return; }   // no package identity (unpackaged run)

        try
        {
            using var checker = new UpdateChecker();
            var latest = await checker.GetLatestAsync();
            if (latest is null || !UpdateChecker.IsNewer(latest.Version, current)) return;

            _updateInstallerUrl = latest.InstallerUrl;
            UpdateBar.Title = $"Update available: {latest.TagName}";
            UpdateBar.Message = string.IsNullOrEmpty(latest.InstallerUrl)
                ? "Open the release page to grab the new installer."
                : "Download and install it now - you'll get a prompt to allow the install.";
            UpdateBar.IsOpen = true;
        }
        catch { /* best-effort; never break settings on a failed check */ }
    }

    /// <summary>
    /// Download the release's installer zip, unpack it, and launch install.ps1
    /// (elevated, since it trusts the cert). Falls back to opening the release
    /// page if there's no asset or anything goes wrong.
    /// </summary>
    private async void OnGetUpdateClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_updateInstallerUrl))
        {
            await OpenReleasePageAsync();
            return;
        }
        try
        {
            GetUpdateButton.IsEnabled = false;
            UpdateBar.Message = "Downloading the new installer...";

            string work = Path.Combine(Path.GetTempPath(), "PrusaConnectWidget-update");
            if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
            Directory.CreateDirectory(work);
            string zip = Path.Combine(work, "installer.zip");

            using (var http = new HttpClient())
            using (var src = await http.GetStreamAsync(_updateInstallerUrl))
            using (var fs = File.Create(zip))
            {
                await src.CopyToAsync(fs);
            }

            string extract = Path.Combine(work, "files");
            ZipFile.ExtractToDirectory(zip, extract);
            string? ps1 = Directory.GetFiles(extract, "install.ps1", SearchOption.AllDirectories).FirstOrDefault();
            if (ps1 is null) { await OpenReleasePageAsync(); return; }

            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-ExecutionPolicy Bypass -NoProfile -File \"{ps1}\"",
                UseShellExecute = true,
                Verb = "runas",   // elevation: install.ps1 trusts the cert
            });
            UpdateBar.Message = "Installer launched. Approve the prompt, then reopen the app on the new version.";
        }
        catch (Exception ex)
        {
            Log.Write($"Update install failed: {ex.GetType().Name} {ex.Message}");
            _updateInstallerUrl = null;   // next click opens the page instead
            UpdateBar.Message = "Couldn't run the installer. Click again to open the release page.";
        }
        finally
        {
            GetUpdateButton.IsEnabled = true;
        }
    }

    private static async Task OpenReleasePageAsync()
    {
        try { await Windows.System.Launcher.LaunchUriAsync(new Uri(UpdateChecker.ReleasesPage)); }
        catch { /* nothing more we can do */ }
    }

    /// <summary>
    /// Load the account's farm teams so the per-tile picker can list them, then
    /// rebuild the rows so both the team and printer dropdowns are current (e.g.
    /// right after sign-in).
    /// </summary>
    private async Task LoadFarmOrgsAsync()
    {
        try
        {
            var svc = new PrusaConnect.Core.Aggregator.PrinterStatusService(_secrets, _connectTokens, _cloudAccount);
            // Farm Status/Orders use Connect Farm organizations (GraphQL);
            // Team Status uses regular Connect teams (no Farm required).
            var orgs = await svc.GetFarmOrganizationsAsync(CancellationToken.None);
            if (orgs.Count > 0) _farmOrgs = orgs;
            var teams = await svc.GetConnectTeamsAsync(CancellationToken.None);
            if (teams.Count > 0) _connectTeams = teams;
        }
        catch (Exception ex)
        {
            Log.Write($"LoadFarmOrgs/Teams failed: {ex.GetType().Name} {ex.Message}");
        }
        // Always rebuild: even with no teams, this picks up newly imported
        // printers in the per-tile printer dropdown.
        RebuildPinnedTilesList();
    }

    private void OnRefreshPinnedTilesClick(object sender, RoutedEventArgs e)
    {
        _pinnedWidgets.RefreshIfStale();
        RebuildPinnedTilesList();
    }

    // the definitions our manifest declares. snapshots for anything else are
    // orphans from renamed/removed defs (e.g. the old single-digit prusa.slot.5
    // before the zero-pad) - prune them so dead rows don't linger.
    // the host caps a provider at ~4 pinned tiles, so we ship exactly 4 slots;
    // each one's kind (printer / team / farm status / farm orders) is set here.
    private static readonly System.Collections.Generic.HashSet<string> ValidDefinitionIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "prusa.printer.01", "prusa.printer.02", "prusa.printer.03", "prusa.printer.04",
    };

    private void RebuildPinnedTilesList()
    {
        PinnedTilesHost.Children.Clear();

        // Pick up the COM server's latest writes (cross-process) before reading.
        _pinnedWidgets.RefreshIfStale();

        // Prune orphaned snapshots (definitions our manifest no longer declares).
        foreach (var orphan in _pinnedWidgets.GetAll()
                     .Where(s => !ValidDefinitionIds.Contains(s.DefinitionId)).ToList())
        {
            Log.Write($"Pruning orphan pinned snapshot {orphan.WidgetId} (def={orphan.DefinitionId})");
            _pinnedWidgets.Remove(orphan.WidgetId);
        }

        // one numbered slot = one tile. the snapshot can pile up STALE entries
        // for a def (old widget IDs the host left behind after a reset/reinstall)
        // that show as duplicate rows. keep the freshest per def so the list
        // matches the board.
        var snapshots = _pinnedWidgets.GetAll()
            .GroupBy(s => s.DefinitionId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(s => s.LastSeenAt).First())
            .OrderBy(s => s.DefinitionId, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (snapshots.Count == 0)
        {
            PinnedTilesHost.Children.Add(new TextBlock
            {
                Text = "No tiles pinned yet. Open the Widgets Board (Win+W) → \"+ Add widgets\" → pick a \"Prusa Connect - Tile NN\" entry.",
                Opacity = 0.6,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }
        foreach (var s in snapshots)
        {
            PinnedTilesHost.Children.Add(BuildPinnedTileRow(s));
        }
    }

    private UIElement BuildPinnedTileRow(PinnedWidgetSnapshot snap)
    {
        // generic slot: the user picks what it shows (kind) and, for Printer
        // Status, which printer. both are stored per-tile (snapshot Kind +
        // BoundPrinterId), not fixed by the definition.
        var pending = _rebinds.GetAll().FirstOrDefault(r => r.WidgetId == snap.WidgetId);
        TileKind kind = TileKindWire.FromWire(pending?.NewKind ?? snap.Kind);
        bool isPrinterTile = kind == TileKind.PrinterStatus;
        string currentBoundId = pending?.NewPrinterId ?? snap.BoundPrinterId ?? string.Empty;

        var titleText = new TextBlock
        {
            Text = $"{SlotLabel(snap.DefinitionId)}  ·  {TileKindWire.DisplayName(kind)}",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        };

        var ageText = FormatAge(snap.LastSeenAt);
        var stateText = string.IsNullOrEmpty(snap.LastState) ? "-" : snap.LastState;
        string sourceText = string.IsNullOrEmpty(snap.LastSource) ? "" : $"  •  via {snap.LastSource}";
        var meta = new TextBlock
        {
            Text = isPrinterTile
                ? $"state: {stateText}{sourceText}  •  seen {ageText}"
                : $"seen {ageText}",
            Opacity = 0.6,
            FontSize = 12,
        };
        var leftStack = new StackPanel { Spacing = 2 };
        leftStack.Children.Add(titleText);
        leftStack.Children.Add(meta);

        bool initializing = true;

        // Kind dropdown - what this slot shows.
        var kinds = new[] { TileKind.PrinterStatus, TileKind.TeamStatus, TileKind.FarmStatus, TileKind.FarmOrders };
        var kindCombo = new ComboBox { Width = 150, VerticalAlignment = VerticalAlignment.Center };
        for (int i = 0; i < kinds.Length; i++)
        {
            kindCombo.Items.Add(new ComboBoxItem { Content = TileKindWire.DisplayName(kinds[i]), Tag = TileKindWire.ToWire(kinds[i]) });
            if (kinds[i] == kind) kindCombo.SelectedIndex = i;
        }

        static bool IsFarm(TileKind k) => k == TileKind.FarmStatus || k == TileKind.FarmOrders;
        static bool IsTeam(TileKind k) => k == TileKind.TeamStatus;

        // Printer dropdown - which printer (kind = Printer Status).
        var allPrinters = _printers.GetAll();
        var printerCombo = new ComboBox { Width = 260, VerticalAlignment = VerticalAlignment.Center, PlaceholderText = "Printer…" };
        for (int i = 0; i < allPrinters.Count; i++)
        {
            var p = allPrinters[i];
            printerCombo.Items.Add(new ComboBoxItem { Content = string.IsNullOrEmpty(p.Model) ? p.Name : $"{p.Name} - {p.Model}", Tag = p.Id });
            if (kind == TileKind.PrinterStatus && string.Equals(p.Id, currentBoundId, StringComparison.OrdinalIgnoreCase)) printerCombo.SelectedIndex = i;
        }

        // Farm-org dropdown - which Connect Farm organization (kind = Farm Status / Orders).
        var teamCombo = new ComboBox { Width = 260, VerticalAlignment = VerticalAlignment.Center, PlaceholderText = "Farm team…" };
        for (int i = 0; i < _farmOrgs.Count; i++)
        {
            var o = _farmOrgs[i];
            teamCombo.Items.Add(new ComboBoxItem { Content = o.Name, Tag = o.Id });
            if (IsFarm(kind) && string.Equals(o.Id, currentBoundId, StringComparison.OrdinalIgnoreCase)) teamCombo.SelectedIndex = i;
        }
        if (IsFarm(kind) && teamCombo.SelectedIndex < 0 && _farmOrgs.Count > 0) teamCombo.SelectedIndex = 0;

        // Connect-team dropdown - which regular Prusa Connect team (kind = Team
        // Status; no Connect Farm required). Binding is the team name.
        var connectTeamCombo = new ComboBox { Width = 260, VerticalAlignment = VerticalAlignment.Center, PlaceholderText = "Team…" };
        for (int i = 0; i < _connectTeams.Count; i++)
        {
            var t = _connectTeams[i];
            connectTeamCombo.Items.Add(new ComboBoxItem { Content = t.Name, Tag = t.Id });
            if (IsTeam(kind) && string.Equals(t.Id, currentBoundId, StringComparison.OrdinalIgnoreCase)) connectTeamCombo.SelectedIndex = i;
        }
        if (IsTeam(kind) && connectTeamCombo.SelectedIndex < 0 && _connectTeams.Count > 0) connectTeamCombo.SelectedIndex = 0;

        void UpdateComboVisibility()
        {
            var k = TileKindWire.FromWire((kindCombo.SelectedItem as ComboBoxItem)?.Tag as string);
            printerCombo.Visibility = (k == TileKind.PrinterStatus) ? Visibility.Visible : Visibility.Collapsed;
            teamCombo.Visibility = IsFarm(k) ? Visibility.Visible : Visibility.Collapsed;
            connectTeamCombo.Visibility = IsTeam(k) ? Visibility.Visible : Visibility.Collapsed;
        }

        void EnqueueCurrent()
        {
            if (initializing) return;
            string? kindWire = (kindCombo.SelectedItem as ComboBoxItem)?.Tag as string;
            var curKind = TileKindWire.FromWire(kindWire);
            // Each kind binds to a different thing: a printer, a Connect team, or
            // a Farm org. Pull the id from the combo that matches the kind.
            string boundId =
                IsFarm(curKind) ? ((teamCombo.SelectedItem as ComboBoxItem)?.Tag as string) ?? string.Empty :
                IsTeam(curKind) ? ((connectTeamCombo.SelectedItem as ComboBoxItem)?.Tag as string) ?? string.Empty :
                ((printerCombo.SelectedItem as ComboBoxItem)?.Tag as string) ?? string.Empty;
            try
            {
                _rebinds.Enqueue(snap.WidgetId, boundId, kindWire);
                ShowStatus(InfoBarSeverity.Success, "Tile update queued",
                    "Applies on the tile's next poll (~10-30s). Reopen settings to see the new kind label.");
            }
            catch (Exception ex)
            {
                Log.Error($"Enqueue {snap.WidgetId} threw", ex);
                ShowStatus(InfoBarSeverity.Error, "Update failed", ex.Message);
            }
        }
        kindCombo.SelectionChanged += (s, e) => { UpdateComboVisibility(); EnqueueCurrent(); };
        printerCombo.SelectionChanged += (s, e) => EnqueueCurrent();
        teamCombo.SelectionChanged += (s, e) => EnqueueCurrent();
        connectTeamCombo.SelectionChanged += (s, e) => EnqueueCurrent();
        UpdateComboVisibility();
        initializing = false;

        var rightStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        rightStack.Children.Add(kindCombo);
        rightStack.Children.Add(printerCombo);
        rightStack.Children.Add(teamCombo);
        rightStack.Children.Add(connectTeamCombo);

        var grid = new Grid { Padding = new Thickness(8), ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(leftStack, 0);
        Grid.SetColumn(rightStack, 1);
        grid.Children.Add(leftStack);
        grid.Children.Add(rightStack);

        return new Border
        {
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(RowBorderColor),
            CornerRadius = new CornerRadius(6),
            Child = grid,
        };
    }

    /// <summary>"prusa.printer.03" -> "Prusa Connect - 3".</summary>
    private static string SlotLabel(string definitionId)
    {
        var m = System.Text.RegularExpressions.Regex.Match(definitionId ?? string.Empty, @"(\d+)$");
        return m.Success && int.TryParse(m.Value, out int n) ? $"Prusa Connect - {n}" : "Prusa Connect";
    }

    private static string FormatAge(DateTimeOffset ts)
    {
        var age = DateTimeOffset.UtcNow - ts;
        if (age.TotalSeconds < 60) return "just now";
        if (age.TotalMinutes < 60) return $"{(int)age.TotalMinutes}m ago";
        if (age.TotalHours < 24) return $"{(int)age.TotalHours}h ago";
        return ts.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    }

    // -- Saved printers list --------------------------------------------------

    private void RebuildSavedPrintersList()
    {
        SavedPrintersHost.Children.Clear();
        var printers = _printers.GetAll();
        if (printers.Count == 0)
        {
            SavedPrintersHost.Children.Add(new TextBlock
            {
                Text = "No printers yet - click \"Add new\" or import one from Prusa Connect below.",
                Opacity = 0.6,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        foreach (var p in printers)
        {
            SavedPrintersHost.Children.Add(BuildPrinterExpander(p, isNew: false));
        }
    }

    /// <summary>
    /// One collapsible row: header with summary + Remove, body with that
    /// printer's editor form. Row state is per-instance - no shared edit panel.
    /// </summary>
    private UIElement BuildPrinterExpander(PrinterInfo existing, bool isNew)
    {
        // --- Header ---
        string title = isNew ? "New printer" : (string.IsNullOrWhiteSpace(existing.Name) ? existing.Id : existing.Name);
        var titleText = new TextBlock { Text = title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        var metaText = new TextBlock
        {
            Text = isNew ? "Fill in PrusaLink LAN details below." : FormatSavedMeta(existing),
            Opacity = 0.6,
            FontSize = 12,
        };
        var leftStack = new StackPanel { Spacing = 2 };
        leftStack.Children.Add(titleText);
        leftStack.Children.Add(metaText);

        var removeButton = new Button { Content = "Remove", VerticalAlignment = VerticalAlignment.Center };
        if (isNew) { removeButton.IsEnabled = false; }

        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.HorizontalAlignment = HorizontalAlignment.Stretch;
        Grid.SetColumn(leftStack, 0);
        Grid.SetColumn(removeButton, 1);
        headerGrid.Children.Add(leftStack);
        headerGrid.Children.Add(removeButton);

        // --- Editor body ---
        var nameBox = new TextBox { Header = "Display name", Text = isNew ? string.Empty : existing.Name };
        var hostBox = new TextBox { Header = "Host (IP or DNS)", Text = isNew ? string.Empty : existing.Host, PlaceholderText = "192.168.1.42" };
        var portBox = new NumberBox
        {
            Header = "Port",
            Value = isNew ? 80 : existing.Port,
            Minimum = 1,
            Maximum = 65535,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            Width = 120,
        };
        var hostPortGrid = new Grid { ColumnSpacing = 12 };
        hostPortGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        hostPortGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(hostBox, 0);
        Grid.SetColumn(portBox, 1);
        hostPortGrid.Children.Add(hostBox);
        hostPortGrid.Children.Add(portBox);

        var apiKeyBox = new PasswordBox
        {
            Header = "API key",
            PlaceholderText = "From PrusaLink → Settings → API key",
            Password = isNew ? string.Empty : (_secrets.Get(existing.Id) ?? string.Empty),
        };
        var modelBox = new TextBox { Header = "Model (optional)", Text = isNew ? string.Empty : existing.Model };
        var cameraBox = new TextBox
        {
            Header = "Camera stream URL (optional)",
            PlaceholderText = "rtsp://printer-ip/stream or http://...",
            Text = isNew ? string.Empty : (existing.CameraStreamUrl ?? string.Empty),
        };

        var testButton = new Button { Content = "Test connection" };
        var saveButton = new Button { Content = isNew ? "Add" : "Save", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        var cancelButton = new Button { Content = "Cancel" };
        var buttonRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(0, 4, 0, 0) };
        buttonRow.Children.Add(testButton);
        buttonRow.Children.Add(saveButton);
        buttonRow.Children.Add(cancelButton);

        var body = new StackPanel { Spacing = 14 };
        body.Children.Add(nameBox);
        body.Children.Add(hostPortGrid);
        body.Children.Add(apiKeyBox);
        body.Children.Add(modelBox);
        body.Children.Add(cameraBox);
        body.Children.Add(buttonRow);

        var expander = new Expander
        {
            Header = headerGrid,
            Content = body,
            IsExpanded = isNew,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };

        // --- Wire actions ---
        string? bindingId = isNew ? null : existing.Id;

        removeButton.Click += (s, e) =>
        {
            if (bindingId is null) return;
            try
            {
                _printers.Remove(bindingId);
                _secrets.Remove(bindingId);
                RebuildSavedPrintersList();
                ShowStatus(InfoBarSeverity.Informational, "Removed",
                    "Pinned widgets pointing at it will fall back to another printer within ~30s.");
            }
            catch (Exception ex)
            {
                Log.Error("Remove threw", ex);
                ShowStatus(InfoBarSeverity.Error, "Remove failed", ex.Message);
            }
        };

        testButton.Click += async (s, e) =>
        {
            if (string.IsNullOrWhiteSpace(hostBox.Text) || string.IsNullOrEmpty(apiKeyBox.Password))
            {
                ShowStatus(InfoBarSeverity.Warning, "Cannot test", "Host and API key are required.");
                return;
            }
            testButton.IsEnabled = false;
            ShowStatus(InfoBarSeverity.Informational, "Testing…", $"Contacting {hostBox.Text}:{(int)portBox.Value}…");
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
                using var client = new PrusaLinkClient(hostBox.Text.Trim(), apiKeyBox.Password, (int)portBox.Value);
                var result = await client.TestConnectionAsync(cts.Token);
                switch (result.Kind)
                {
                    case TestResultKind.Ok:
                        ShowStatus(InfoBarSeverity.Success, "Connected", $"PrusaLink at {hostBox.Text}:{(int)portBox.Value} responded.");
                        break;
                    case TestResultKind.Unauthorized:
                        ShowStatus(InfoBarSeverity.Error, "API key rejected", "PrusaLink returned 401.");
                        break;
                    case TestResultKind.Timeout:
                        ShowStatus(InfoBarSeverity.Warning, "Timed out", "No response within 6 seconds.");
                        break;
                    case TestResultKind.Unreachable:
                        ShowStatus(InfoBarSeverity.Error, "Unreachable", result.Detail ?? "Network unreachable.");
                        break;
                    case TestResultKind.HttpError:
                        ShowStatus(InfoBarSeverity.Error, "HTTP error", $"PrusaLink returned HTTP {(int?)result.StatusCode}.");
                        break;
                }
            }
            catch (Exception ex)
            {
                Log.Error("Test threw", ex);
                ShowStatus(InfoBarSeverity.Error, "Unexpected error", ex.Message);
            }
            finally
            {
                testButton.IsEnabled = true;
            }
        };

        saveButton.Click += (s, e) =>
        {
            string name = nameBox.Text?.Trim() ?? string.Empty;
            string host = hostBox.Text?.Trim() ?? string.Empty;
            int port = (int)portBox.Value;
            string apiKey = apiKeyBox.Password ?? string.Empty;
            string model = modelBox.Text?.Trim() ?? string.Empty;
            string? cameraUrl = string.IsNullOrWhiteSpace(cameraBox.Text) ? null : cameraBox.Text.Trim();

            if (string.IsNullOrWhiteSpace(name)) { ShowStatus(InfoBarSeverity.Warning, "Cannot save", "Display name is required."); return; }
            if (string.IsNullOrWhiteSpace(host)) { ShowStatus(InfoBarSeverity.Warning, "Cannot save", "Host is required."); return; }
            if (string.IsNullOrWhiteSpace(apiKey)) { ShowStatus(InfoBarSeverity.Warning, "Cannot save", "API key is required."); return; }
            if (port is < 1 or > 65535) { ShowStatus(InfoBarSeverity.Warning, "Cannot save", "Port must be 1-65535."); return; }

            try
            {
                string id = bindingId ?? Guid.NewGuid().ToString("N");
                var printer = new PrinterInfo
                {
                    Id = id,
                    Name = name,
                    Host = host,
                    Port = port,
                    Model = model,
                    CameraStreamUrl = cameraUrl,
                    // Preserve cloud capability + identity when editing an
                    // imported printer (don't drop the Connect UUID on save).
                    ConnectUuid = isNew ? null : existing.ConnectUuid,
                    Hostname = isNew ? null : existing.Hostname,
                    Serial = isNew ? null : existing.Serial,
                    Source = isNew ? PrinterSource.PrusaLink : existing.Source,
                };
                _printers.Upsert(printer);
                _secrets.Set(id, apiKey);
                RebuildSavedPrintersList();
                RebuildPinnedTilesList(); // surface the new/edited printer in tile dropdowns
                ShowStatus(InfoBarSeverity.Success, isNew ? "Added" : "Saved",
                    "Pinned widgets pick up the change within ~30 seconds.");
            }
            catch (Exception ex)
            {
                Log.Error("Save threw", ex);
                ShowStatus(InfoBarSeverity.Error, "Save failed", ex.Message);
            }
        };

        cancelButton.Click += (s, e) =>
        {
            if (isNew)
            {
                // Collapse + drop the unsaved new-printer row.
                RebuildSavedPrintersList();
            }
            else
            {
                // Reset fields to last-saved values + collapse.
                nameBox.Text = existing.Name;
                hostBox.Text = existing.Host;
                portBox.Value = existing.Port;
                modelBox.Text = existing.Model;
                cameraBox.Text = existing.CameraStreamUrl ?? string.Empty;
                apiKeyBox.Password = _secrets.Get(existing.Id) ?? string.Empty;
                expander.IsExpanded = false;
            }
        };

        return expander;
    }

    private static string FormatSavedMeta(PrinterInfo p)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(p.Model)) parts.Add(p.Model);
        parts.Add($"{p.Host}:{p.Port}");
        parts.Add(p.Source == PrinterSource.PrusaConnect ? "from Connect import" : "manual");
        if (!string.IsNullOrWhiteSpace(p.CameraStreamUrl)) parts.Add("camera ✓");
        return string.Join("  •  ", parts);
    }

    private void OnAddNewClick(object sender, RoutedEventArgs e)
    {
        // Append a fresh expander pre-expanded for entry.
        var placeholder = new PrinterInfo
        {
            Id = string.Empty,
            Name = string.Empty,
            Host = string.Empty,
            Port = 80,
        };
        SavedPrintersHost.Children.Add(BuildPrinterExpander(placeholder, isNew: true));
    }

    private void ShowStatus(InfoBarSeverity severity, string title, string message)
    {
        StatusBar.Severity = severity;
        StatusBar.Title = title;
        StatusBar.Message = message;
        StatusBar.IsOpen = true;
        CopyStatusButton.Visibility = severity is InfoBarSeverity.Error or InfoBarSeverity.Warning
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnCopyStatusClick(object sender, RoutedEventArgs e)
    {
        CopyToClipboard($"{StatusBar.Title}\n{StatusBar.Message}");
        FlashCopied((Button)sender);
    }

    private void OnCopyConnectStatusClick(object sender, RoutedEventArgs e)
    {
        CopyToClipboard($"{ConnectStatusBar.Title}\n{ConnectStatusBar.Message}");
        FlashCopied((Button)sender);
    }

    private static void CopyToClipboard(string text)
    {
        var pkg = new DataPackage();
        pkg.SetText(text);
        Clipboard.SetContent(pkg);
    }

    private static async void FlashCopied(Button button)
    {
        object? original = button.Content;
        button.Content = "Copied ✓";
        await Task.Delay(1200);
        button.Content = original;
    }

    // -- Prusa Connect cloud section ------------------------------------------

    private async void OnRefreshCloudClick(object sender, RoutedEventArgs e)
    {
        await RefreshCloudPrintersAsync();
    }

    private async Task RefreshCloudPrintersAsync()
    {
        RefreshCloudButton.IsEnabled = false;
        CloudPrintersHost.Children.Clear();
        ShowConnectStatus(InfoBarSeverity.Informational, "Checking…",
            "Looking for a PrusaSlicer session in Windows Credential Manager…");

        try
        {
            var probeCred = WindowsCredentialManager.Read(PrusaSlicerTokenReader.TargetName);
            Log.Write($"PrusaSlicer cred lookup: found={probeCred != null} blobLen={probeCred?.Blob.Length ?? 0} user={(probeCred?.UserName?.Length ?? 0)}chars");
            var probeToken = PrusaSlicerTokenReader.TryRead();
            Log.Write($"PrusaSlicer token parse: success={probeToken != null} expiresAt={probeToken?.ExpiresAt:O}");
        }
        catch (Exception ex)
        {
            Log.Error("PrusaSlicer probe threw", ex);
        }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var client = new PrusaConnectClient(_cloudAccount, _connectTokens);
            var printers = await client.ListAppPrintersAsync(cts.Token);

            if (printers.Count == 0)
            {
                ShowConnectStatus(InfoBarSeverity.Informational, "No printers found",
                    "Your Prusa Connect account has no printers attached.");
                return;
            }

            var groups = printers
                .GroupBy(p => string.IsNullOrWhiteSpace(p.TeamName) ? "(Personal)" : p.TeamName!)
                .OrderByDescending(g => string.Equals(g.Key, "(Personal)", StringComparison.Ordinal))
                .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();

            ShowConnectStatus(InfoBarSeverity.Success,
                $"Signed in - {printers.Count} printer{(printers.Count == 1 ? "" : "s")} across {groups.Count} team{(groups.Count == 1 ? "" : "s")}",
                "Expand a team and click Import to pull a printer's LAN IP + API key.");

            foreach (var group in groups)
            {
                CloudPrintersHost.Children.Add(BuildTeamSection(group.Key, group.ToList()));
            }

            // authenticated now - (re)load farm teams and refresh the per-tile
            // dropdowns so tiles are configurable without a restart
            await LoadFarmOrgsAsync();
        }
        catch (PrusaConnectAuthRequiredException)
        {
            ShowConnectStatus(InfoBarSeverity.Warning, "Not signed in",
                "Install PrusaSlicer and sign in to Prusa Connect there. We piggy-back on its session.");
        }
        catch (PrusaConnectUnreachableException ex)
        {
            ShowConnectStatus(InfoBarSeverity.Error, "Network error", ex.Message);
        }
        catch (Exception ex)
        {
            Log.Error("RefreshCloudPrintersAsync threw", ex);
            ShowConnectStatus(InfoBarSeverity.Error, "Failed to load fleet", ex.Message);
        }
        finally
        {
            RefreshCloudButton.IsEnabled = true;
        }
    }

    private UIElement BuildTeamSection(string teamName, List<AppPrinterDto> printers)
    {
        var inner = new StackPanel { Spacing = 4 };
        foreach (var p in printers.OrderBy(BestTitle, StringComparer.OrdinalIgnoreCase))
        {
            inner.Children.Add(BuildCloudPrinterRow(p));
        }

        return new Expander
        {
            Header = $"{teamName}   ({printers.Count} printer{(printers.Count == 1 ? "" : "s")})",
            IsExpanded = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = inner,
            Margin = new Thickness(0, 4, 0, 0),
        };
    }

    private UIElement BuildCloudPrinterRow(AppPrinterDto printer)
    {
        var nameText = new TextBlock { Text = BestTitle(printer), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        var meta = new TextBlock { Text = FormatCloudMeta(printer), Opacity = 0.6, FontSize = 12 };
        var leftStack = new StackPanel { Spacing = 2 };
        leftStack.Children.Add(nameText);
        leftStack.Children.Add(meta);

        var importButton = new Button { Content = _printers.TryGet(printer.Uuid) is not null ? "Update" : "Import", Tag = printer.Uuid, VerticalAlignment = VerticalAlignment.Center };
        importButton.Click += OnImportPrinterClick;

        var grid = new Grid { Padding = new Thickness(8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(leftStack, 0);
        Grid.SetColumn(importButton, 1);
        grid.Children.Add(leftStack);
        grid.Children.Add(importButton);

        return new Border
        {
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(RowBorderColor),
            CornerRadius = new CornerRadius(6),
            Child = grid,
        };
    }

    private static string BestTitle(AppPrinterDto p)
    {
        if (!string.IsNullOrWhiteSpace(p.Name)) return p.Name!;
        if (!string.IsNullOrWhiteSpace(p.PrinterModel))
        {
            return string.IsNullOrWhiteSpace(p.Location) ? p.PrinterModel! : $"{p.PrinterModel} - {p.Location}";
        }
        return "Unnamed printer";
    }

    private static string FormatCloudMeta(AppPrinterDto p)
    {
        var parts = new List<string>();
        bool nameWasSet = !string.IsNullOrWhiteSpace(p.Name);
        if (nameWasSet && !string.IsNullOrWhiteSpace(p.PrinterModel)) parts.Add(p.PrinterModel!);
        if (!string.IsNullOrWhiteSpace(p.Location) && p.Location != p.Name) parts.Add(p.Location!);
        if (!string.IsNullOrEmpty(p.Uuid))
        {
            parts.Add("…" + (p.Uuid.Length >= 8 ? p.Uuid[^8..] : p.Uuid));
        }
        return string.Join("  •  ", parts);
    }

    private async void OnImportPrinterClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string uuid) return;

        button.IsEnabled = false;
        string originalText = button.Content?.ToString() ?? "Import";
        button.Content = "Importing…";

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var client = new PrusaConnectClient(_cloudAccount, _connectTokens);
            var detail = await client.GetPrinterDetailAsync(uuid, cts.Token);

            string? ip = detail.NetworkInfo?.PreferredIp;
            string? apiKey = detail.PrusaLinkApiKey;
            if (string.IsNullOrWhiteSpace(ip) || string.IsNullOrWhiteSpace(apiKey))
            {
                ShowConnectStatus(InfoBarSeverity.Warning, "Cannot import",
                    $"{detail.Name ?? uuid} has no LAN IP or PrusaLink key in the cloud yet. Bring the printer online and refresh.");
                button.Content = originalText;
                button.IsEnabled = true;
                return;
            }

            var printer = new PrinterInfo
            {
                Id = uuid,
                Name = detail.Name ?? detail.PrinterModel ?? uuid,
                Host = ip,
                Port = 80,
                Model = detail.PrinterModel ?? string.Empty,
                Hostname = detail.NetworkInfo?.Hostname,
                Serial = detail.Serial,
                ConnectUuid = uuid,
                Source = PrinterSource.PrusaConnect,
            };
            _printers.Upsert(printer);
            _secrets.Set(uuid, apiKey);

            button.Content = "Imported ✓";
            RebuildSavedPrintersList();
            RebuildPinnedTilesList(); // surface the new printer in tile dropdowns
            ShowConnectStatus(InfoBarSeverity.Success,
                $"Imported {printer.Name}",
                $"LAN: {ip} - pinned widgets pick up the change within ~30 seconds.");
        }
        catch (PrusaConnectAuthRequiredException)
        {
            ShowConnectStatus(InfoBarSeverity.Warning, "Not signed in",
                "Sign in to Prusa Connect in PrusaSlicer first.");
            button.Content = originalText;
            button.IsEnabled = true;
        }
        catch (Exception ex)
        {
            Log.Error($"Import {uuid} threw", ex);
            ShowConnectStatus(InfoBarSeverity.Error, "Import failed", ex.Message);
            button.Content = originalText;
            button.IsEnabled = true;
        }
    }

    private void ShowConnectStatus(InfoBarSeverity severity, string title, string message)
    {
        ConnectStatusBar.Severity = severity;
        ConnectStatusBar.Title = title;
        ConnectStatusBar.Message = message;
        CopyConnectStatusButton.Visibility = severity is InfoBarSeverity.Error or InfoBarSeverity.Warning
            ? Visibility.Visible : Visibility.Collapsed;
    }
}
