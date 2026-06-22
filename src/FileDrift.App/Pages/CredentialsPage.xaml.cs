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
            foreach (var target in _credentials.ListTargets().OrderBy(t => t))
            {
                string user;
                try { user = _credentials.GetCredential(target)?.UserName ?? ""; }
                catch { user = "(unreadable)"; }
                _rows.Add(new CredRow(CredentialTarget.Display(target), user, target));
            }
            StatusText.Text = $"{_rows.Count} saved credential(s).";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not list credentials: {ex.Message}";
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var share = ShareBox.Text?.Trim();
        var user = UserBox.Text?.Trim();
        var password = PassBox.Password;

        if (string.IsNullOrWhiteSpace(share) || string.IsNullOrWhiteSpace(user))
        {
            StatusText.Text = "Enter at least a share path and username.";
            return;
        }

        var target = CredentialTarget.For(share);
        try
        {
            _credentials.SetCredential(target, new NetworkCredential(user, password));
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
            StatusText.Text = "Select a credential to delete.";
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
