// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using FileDrift.Core.Cloud;
using Wpf.Ui.Controls;

namespace FileDrift.App;

/// <summary>
/// Browses the tenant for a SharePoint or OneDrive folder, one level at a time: sites, then document
/// libraries, then folders.
/// <para>This exists because a SharePoint path cannot be composed reliably by hand. The site, the
/// library and the folder each have a display name that differs from their URL segment ("Documents"
/// against "Shared Documents" being the standard trap), and only the tenant knows where one ends and
/// the next begins. Typing the URL means guessing; browsing means the tenant answers.</para>
/// </summary>
public partial class CloudPickerWindow : FluentWindow
{
    /// <summary>One row in the listing: the node plus how to draw it.</summary>
    private sealed record Row(CloudNode Node)
    {
        public string Name => Node.Name;

        public SymbolRegular Icon => Node.Kind switch
        {
            CloudNodeKind.Site => SymbolRegular.Globe24,
            CloudNodeKind.Library => SymbolRegular.Library24,
            _ => SymbolRegular.Folder24,
        };
    }

    private readonly CloudBrowser _browser;
    private readonly ObservableCollection<Row> _rows = [];

    /// <summary>The path from the sites root to where we are now. Empty means the sites level.</summary>
    private readonly List<CloudNode> _trail = [];

    private string _search = "*";
    private int _loadGeneration;

    /// <summary>The chosen location URL, set only when the operator pressed Select.</summary>
    public string? SelectedPath { get; private set; }

    public CloudPickerWindow(CloudBrowser browser)
    {
        _browser = browser;
        InitializeComponent();
        ItemList.ItemsSource = _rows;
        Loaded += async (_, _) => await LoadAsync();
    }

    /// <summary>Opens the picker over the main window and returns the chosen path, or null.</summary>
    public static string? Pick(CloudBrowser browser)
    {
        var window = new CloudPickerWindow(browser);
        if (Application.Current?.MainWindow is { } main && main != window) window.Owner = main;
        return window.ShowDialog() == true ? window.SelectedPath : null;
    }

    // -- navigation --

    private CloudNode? Current => _trail.Count == 0 ? null : _trail[^1];

    /// <summary>Loads the listing for wherever the trail currently points.</summary>
    /// <remarks>Each load takes a generation number so a slow reply from a level the operator has
    /// already navigated away from cannot overwrite the list they are now looking at.</remarks>
    private async Task LoadAsync()
    {
        var generation = ++_loadGeneration;
        SetBusy(true);
        ErrorBar.IsOpen = false;

        try
        {
            var current = Current;
            IReadOnlyList<CloudNode> nodes = current switch
            {
                null => await _browser.ListSitesAsync(_search),
                { Kind: CloudNodeKind.Site } site => await _browser.ListLibrariesAsync(site.SiteId!),
                var node => await _browser.ListFoldersAsync(node.SiteId!, node.DriveId!, node.FolderPath),
            };

            if (generation != _loadGeneration) return;

            _rows.Clear();
            foreach (var node in nodes) _rows.Add(new Row(node));
            ShowEmptyNotice(nodes.Count == 0);
        }
        catch (Exception ex) when (generation == _loadGeneration)
        {
            _rows.Clear();
            ShowEmptyNotice(false);
            ShowError(Explain(ex));
        }
        finally
        {
            if (generation == _loadGeneration)
            {
                SetBusy(false);
                UpdateChrome();
            }
        }
    }

    /// <summary>Turns a failure into something the operator can act on.</summary>
    /// <remarks>403 is the case worth naming. An administrator browsing their own tenant will read
    /// "forbidden" as a bug, when it almost always means this account has not been added to the site
    /// -- or has just been added and the grant has not propagated yet.</remarks>
    private static string Explain(Exception ex) => ex switch
    {
        GraphApiException { StatusCode: System.Net.HttpStatusCode.Forbidden } =>
            "This account does not have access to that location. Administrator roles do not grant " +
            "content access on their own, so you may need to add yourself to the site. If you just " +
            "did, the grant can take a few minutes to take effect; retry, or sign out and back in.",
        GraphApiException { StatusCode: System.Net.HttpStatusCode.NotFound } =>
            "That location no longer exists, or this account cannot see it.",
        GraphSignInRequiredException =>
            "Not signed in to Microsoft Graph. Sign in from Settings, then try again.",
        _ => ex.Message,
    };

    private void OnUp(object sender, RoutedEventArgs e)
    {
        if (_trail.Count == 0) return;
        _trail.RemoveAt(_trail.Count - 1);
        _ = LoadAsync();
    }

    private void OnItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemList.SelectedItem is Row row) Open(row.Node);
    }

    private void Open(CloudNode node)
    {
        _trail.Add(node);
        _ = LoadAsync();
    }

    private void OnSearch(object sender, RoutedEventArgs e)
    {
        // Searching is a sites-level operation, so it also returns there.
        _trail.Clear();
        var text = SearchBox.Text?.Trim();
        _search = string.IsNullOrEmpty(text) ? "*" : text;
        _ = LoadAsync();
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OnSearch(sender, e);
    }

    // -- selection --

    private void OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        UpdateSelection();

    /// <summary>The node Select would choose: the highlighted row, or the folder we are inside when
    /// nothing is highlighted. Choosing the current folder matters, because the destination is often
    /// a library root with no sub-folder to click.</summary>
    private CloudNode? Candidate =>
        ItemList.SelectedItem is Row row && row.Node.IsSelectable ? row.Node
        : Current is { IsSelectable: true } current ? current
        : null;

    private void UpdateSelection()
    {
        var candidate = Candidate;
        SelectButton.IsEnabled = candidate is not null;
        SelectionText.Text = candidate is null
            ? _trail.Count == 0
                ? "Open a site to choose a document library."
                : "Open a library to choose a folder."
            : $"Selects: {candidate.WebUrl}";
    }

    private void OnSelect(object sender, RoutedEventArgs e)
    {
        if (Candidate?.WebUrl is not { Length: > 0 } url) return;
        SelectedPath = url;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    // -- chrome --

    private void SetBusy(bool busy)
    {
        Busy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ItemList.IsEnabled = !busy;
        UpButton.IsEnabled = !busy && _trail.Count > 0;
        SearchButton.IsEnabled = !busy;
    }

    private void ShowError(string message)
    {
        ErrorBar.Message = message;
        ErrorBar.IsOpen = true;
    }

    private void ShowEmptyNotice(bool empty)
    {
        EmptyText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        if (!empty) return;

        EmptyText.Text = Current switch
        {
            null => _search == "*"
                ? "No sites are visible to this account. " + CloudBrowser.ScopeCaveat
                : $"No sites matched “{_search}” that this account can access.",
            { Kind: CloudNodeKind.Site } => "This site has no document libraries this account can see.",
            _ => "No sub-folders here. Press Select to choose this folder.",
        };
    }

    private void UpdateChrome()
    {
        UpButton.IsEnabled = _trail.Count > 0;

        // Search only applies to the site index, so it is offered only where it does something.
        var atSites = _trail.Count == 0;
        SearchBox.IsEnabled = atSites;
        SearchButton.IsEnabled = atSites;

        ScopeBar.Message = CloudBrowser.ScopeCaveat;
        ScopeBar.IsOpen = atSites;

        TrailText.Text = _trail.Count == 0
            ? "Sites"
            : "Sites  ›  " + string.Join("  ›  ", _trail.Select(n => n.Name));

        UpdateSelection();
    }
}
