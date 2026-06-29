// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows.Controls;
using Wpf.Ui.Abstractions;

namespace FileDrift.App;

/// <summary>Supplies pages to the NavigationView. The Verify page is cached as a single instance so a
/// running verify — its background task, activity log, progress, and typed inputs — survives navigating
/// away to Settings/History and back. Other pages are created fresh each time (so e.g. History reloads).</summary>
public sealed class PageProvider : INavigationViewPageProvider
{
    private Pages.VerifyPage? _verify;

    public object? GetPage(Type pageType)
    {
        if (pageType == typeof(Pages.VerifyPage))
            return _verify ??= new Pages.VerifyPage();

        return Activator.CreateInstance(pageType) as Page;
    }
}
