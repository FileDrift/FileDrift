// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using FileDrift.Core.Interfaces;
using FileDrift.Core.Persistence;

namespace FileDrift.App.Pages;

public partial class CompliancePage : Page
{
    private readonly IRunRepository _repository = AppServices.Repository;
    private readonly ObservableCollection<CertCheck> _rows = new();

    public CompliancePage()
    {
        InitializeComponent();
        ResultsGrid.ItemsSource = _rows;
    }

    private async void OnVerifyFile(object sender, RoutedEventArgs e)
    {
        var status = await ComplianceActions.VerifyCertificateAsync(_repository);
        if (status is not null) StatusText.Text = status;
    }

    private async void OnVerifyFolder(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Win32.OpenFolderDialog { Title = "Select a folder of certificates" };
        if (picker.ShowDialog() != true) return;

        StatusText.Text = "Scanning…";
        var rows = await ComplianceActions.VerifyFolderAsync(_repository, picker.FolderName);

        _rows.Clear();
        foreach (var r in rows) _rows.Add(r);
        ClearButton.IsEnabled = _rows.Count > 0;

        if (_rows.Count == 0)
        {
            StatusText.Text = $"No .html files found under {picker.FolderName}.";
            return;
        }

        int intact = rows.Count(r => r.Result == "Intact");
        int altered = rows.Count(r => r.Result == "ALTERED");
        int notCert = rows.Count(r => r.Result == "Not a certificate");
        StatusText.Text =
            $"{rows.Count} file(s): {intact} intact, {altered} altered, {notCert} not certificates" +
            (altered > 0 ? "  —  review the altered certificates above." : ".");
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        _rows.Clear();
        ClearButton.IsEnabled = false;
        StatusText.Text = "";
    }
}
