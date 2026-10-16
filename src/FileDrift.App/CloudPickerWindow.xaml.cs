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

    /// <summary>Every site the account can see, fetched once. Filtering is then local, which is both
    /// instant and a plain substring match -- the tenant's own site search matches indexed terms, so a
    /// partial word ("Tool") can miss a site ("IT Toolbox") that a substring match finds immediately.
    /// Re-querying per keystroke would be slower AND worse at finding things.</summary>
    private List<CloudNode>? _allSites;

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
            IReadOnlyList<CloudNode> nodes;
            switch (current)
            {
                case null:
                    _allSites ??= [.. await _browser.ListSitesAsync("*")];
                    nodes = _allSites;
                    break;
                case { Kind: CloudNodeKind.Site } site:
                    nodes = await _browser.ListLibrariesAsync(site.SiteId!);
                    break;
                default:
                    nodes = await _browser.ListFoldersAsync(current.SiteId!, current.DriveId!, current.FolderPath);
                    break;
            }

            if (generation != _loadGeneration) return;

            Show(nodes);
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

    /// <summary>Fills the list, applying the sites filter when we are at that level.</summary>
    private void Show(IReadOnlyList<CloudNode> nodes)
    {
        var filtered = _trail.Count == 0 ? ApplyFilter(nodes) : nodes;

        _rows.Clear();
        foreach (var node in filtered) _rows.Add(new Row(node));
        ShowEmptyNotice(filtered.Count == 0);
    }

    private IReadOnlyList<CloudNode> ApplyFilter(IReadOnlyList<CloudNode> nodes)
    {
        var text = SearchBox?.Text?.Trim();
        if (string.IsNullOrEmpty(text)) return nodes;

        // Matches the URL as well as the name, because the two differ in exactly the way that trips
        // people up: a site shown as "Public Works" lives at /sites/PublicWorks.
        return [.. nodes.Where(n =>
            n.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase) ||
            (n.WebUrl?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false))];
    }

    private void OnFilterChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        // Purely local, so this can run on every keystroke with no request and no debounce.
        if (_allSites is null || _trail.Count != 0) return;
        Show(_allSites);
        UpdateSelection();
    }

    /// <summary>Re-asks the tenant for the site list.</summary>
    /// <remarks>Worth having as an explicit action: a newly created site can be missing from the
    /// search index for a while, so "not listed" does not mean "does not exist". The path box stays
    /// editable for exactly that case.</remarks>
    private void OnRefresh(object sender, RoutedEventArgs e)
    {
        _allSites = null;
        _trail.Clear();
        _ = LoadAsync();
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
        RefreshButton.IsEnabled = !busy;
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

        var filter = SearchBox?.Text?.Trim();
        EmptyText.Text = Current switch
        {
            null when !string.IsNullOrEmpty(filter) =>
                $"No listed site matches “{filter}”. " + CloudBrowser.ScopeCaveat +
                " A site created recently may not be in the search index yet - try Refresh, or paste " +
                "its URL into the path box instead.",
            null => "No sites are visible to this account. " + CloudBrowser.ScopeCaveat,
            { Kind: CloudNodeKind.Site } => "This site has no document libraries this account can see.",
            _ => "No sub-folders here. Press Select to choose this folder.",
        };
    }

    private void UpdateChrome()
    {
        UpButton.IsEnabled = _trail.Count > 0;

        // Filtering and refreshing are sites-level actions, so they are offered only where they do
        // something.
        var atSites = _trail.Count == 0;
        SearchBox.IsEnabled = atSites;
        RefreshButton.IsEnabled = atSites;

        ScopeBar.Message = CloudBrowser.ScopeCaveat;
        ScopeBar.IsOpen = atSites;

        TrailText.Text = _trail.Count == 0
            ? "Sites"
            : "Sites  ›  " + string.Join("  ›  ", _trail.Select(n => n.Name));

        UpdateSelection();
    }
}
