using System.Collections.ObjectModel;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using FileDrift.Core.Engine;
using FileDrift.Core.Interfaces;

namespace FileDrift.App.Pages;

public partial class CredentialsPage : Page
{
    private readonly ICredentialStore _credentials = new WindowsCredentialStore();
    private readonly ObservableCollection<CredRow> _rows = new();

    public CredentialsPage()
    {
        InitializeComponent();
        CredGrid.ItemsSource = _rows;
        Loaded += (_, _) => Reload();
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => Reload();

    private void Reload()
    {
        _rows.Clear();
        try
        {
            // Default credential lives under its own target; show it separately, not in the per-share list.
            var defaultCred = _credentials.GetCredential(CredentialTarget.DefaultTarget);
            if (defaultCred is not null)
            {
                DefaultStatusText.Text = $"Default set for user '{defaultCred.UserName}'.";
                DefaultUserBox.Text = defaultCred.UserName;
                ClearDefaultButton.Visibility = Visibility.Visible;
            }
            else
            {
                DefaultStatusText.Text = "No default credential set.";
                ClearDefaultButton.Visibility = Visibility.Collapsed;
            }

            foreach (var target in _credentials.ListTargets().OrderBy(t => t))
            {
                if (CredentialTarget.IsDefault(target)) continue;

                string user;
                try { user = _credentials.GetCredential(target)?.UserName ?? ""; }
                catch { user = "(unreadable)"; }
                _rows.Add(new CredRow(CredentialTarget.Display(target), user, target));
            }

            StatusText.Text = $"{_rows.Count} per-share credential(s).";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not list credentials: {ex.Message}";
        }
    }

    private void OnSaveDefault(object sender, RoutedEventArgs e)
    {
        var user = DefaultUserBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(user))
        {
            StatusText.Text = "Enter a username for the default credential.";
            return;
        }

        try
        {
            _credentials.SetCredential(CredentialTarget.DefaultTarget, new NetworkCredential(user, DefaultPassBox.Password));
            DefaultPassBox.Password = "";
            StatusText.Text = $"Saved default credential for '{user}'.";
            Reload();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Save failed: {ex.Message}";
        }
    }

    private void OnClearDefault(object sender, RoutedEventArgs e)
    {
        try
        {
            _credentials.DeleteCredential(CredentialTarget.DefaultTarget);
            DefaultUserBox.Text = "";
            DefaultPassBox.Password = "";
            StatusText.Text = "Default credential cleared.";
            Reload();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Clear failed: {ex.Message}";
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var share = ShareBox.Text?.Trim();
        var user = UserBox.Text?.Trim();

        if (string.IsNullOrWhiteSpace(share) || string.IsNullOrWhiteSpace(user))
        {
            StatusText.Text = "Enter at least a share path and username.";
            return;
        }

        var target = CredentialTarget.For(share);
        try
        {
            _credentials.SetCredential(target, new NetworkCredential(user, PassBox.Password));
            PassBox.Password = "";
            StatusText.Text = $"Saved credential for {CredentialTarget.Display(target)}.";
            Reload();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Save failed: {ex.Message}";
        }
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (CredGrid.SelectedItem is not CredRow row)
        {
            StatusText.Text = "Select a per-share credential to delete.";
            return;
        }

        try
        {
            bool existed = _credentials.DeleteCredential(row.Target);
            StatusText.Text = existed ? $"Deleted {row.Share}." : $"{row.Share} was already gone.";
            Reload();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Delete failed: {ex.Message}";
        }
    }

    private sealed record CredRow(string Share, string User, string Target);
}
