using System.ComponentModel;
using DnaEntropyGraph.Presentation.Services;
using DnaEntropyGraph.Presentation.Viewer;
using DnaEntropyGraph.Presentation.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace DnaEntropyGraph.App.Services;

/// <summary>Reads the installed WebView2 Runtime's version (throws when it is missing; see <see cref="WebViewRuntimeDecision"/>).</summary>
public sealed class WebView2RuntimeProbe : IWebViewRuntimeProbe
{
    public string? GetVersion() => CoreWebView2Environment.GetAvailableBrowserVersionString();
}

/// <summary>
/// Wraps the <see cref="WebView2"/> control for the igv.js viewer (issue #72). Everything with a
/// decision in it (which files, which URLs, runtime missing, bridge events) lives in
/// <see cref="ViewerViewModel"/> / <see cref="IgvLoadPlanner"/>; this class only wires the control to it:
/// <c>viewer.deg</c> -> Assets/viewer, <c>run.deg</c> -> the run folder (remapped on every change of
/// <see cref="ViewerViewModel.MapRunFolder"/>), DevTools off in release builds, web messages on (from the
/// viewer.deg origin only), navigation locked to viewer.deg, profile under %LOCALAPPDATA%\DNAEntropyGraph.
/// <see cref="AttachAsync"/> never throws: a failure to start the control becomes the ViewModel's named-action
/// error, and a page left during the start-up never gets subscribed (<see cref="ViewerAttachGate"/>).
/// </summary>
public sealed class IgvViewerHost
{
    private readonly ViewerAttachGate _gate = new();
    private ViewerViewModel? _viewModel;
    private WebView2? _webView;
    private bool _reloadedAfterCrash;

    public async Task AttachAsync(WebView2 webView, ViewerViewModel viewModel)
    {
        if (!_gate.TryBegin(out var generation))
        {
            return;
        }

        if (!WebViewRuntimeDecision.IsAvailable(CoreWebView2Environment.GetAvailableBrowserVersionString))
        {
            // The ViewModel's OpenRun already shows the named-action error; there is nothing to host.
            return;
        }

        try
        {
            var userData = ViewerUrls.UserDataFolder(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            var environment = await CoreWebView2Environment.CreateWithOptionsAsync(null, userData, new CoreWebView2EnvironmentOptions());
            if (!_gate.IsCurrent(generation))
            {
                return;
            }

            await webView.EnsureCoreWebView2Async(environment);
            if (!_gate.IsCurrent(generation))
            {
                return;
            }

            Configure(webView, viewModel);
        }
        catch (Exception)
        {
            if (_gate.IsCurrent(generation))
            {
                viewModel.OnViewerInitFailed();
            }
        }
    }

    public void Detach()
    {
        _gate.Close();
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.MessageToViewer -= OnMessageToViewer;
        }

        if (_webView is not null)
        {
            _webView.ActualThemeChanged -= OnActualThemeChanged;
        }

        _webView?.Close();
        _webView = null;
        _viewModel = null;
    }

    private void Configure(WebView2 webView, ViewerViewModel viewModel)
    {
        _viewModel = viewModel;
        _webView = webView;
        var core = webView.CoreWebView2;

#if DEBUG
        // #69 will own a real developer-mode switch; until then DevTools exist in Debug builds only.
        core.Settings.AreDevToolsEnabled = true;
#else
        core.Settings.AreDevToolsEnabled = false;
#endif
        core.Settings.IsWebMessageEnabled = true;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDefaultScriptDialogsEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;

        core.SetVirtualHostNameToFolderMapping(
            ViewerUrls.ViewerHost,
            Path.Combine(AppContext.BaseDirectory, "Assets", "viewer"),
            CoreWebView2HostResourceAccessKind.Deny);

        core.NavigationStarting += (_, e) => e.Cancel = !ViewerUrls.IsAllowedNavigation(e.Uri);
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.DownloadStarting += (_, e) => e.Cancel = true;
        core.WebMessageReceived += OnWebMessageReceived;
        core.ProcessFailed += OnProcessFailed;

        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        viewModel.MessageToViewer += OnMessageToViewer;
        webView.ActualThemeChanged += OnActualThemeChanged;
        viewModel.SetDarkMode(webView.ActualTheme == ElementTheme.Dark);

        MapRunFolder(core, viewModel.MapRunFolder);
        core.Navigate(ViewerUrls.StartUrl);
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args) => _viewModel?.SetDarkMode(sender.ActualTheme == ElementTheme.Dark);

    private void OnWebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        // Only the locked viewer.deg bundle may talk to the ViewModel.
        if (ViewerUrls.IsAllowedNavigation(e.Source))
        {
            _viewModel?.OnViewerMessage(e.WebMessageAsJson);
        }
    }

    private void OnProcessFailed(CoreWebView2 sender, CoreWebView2ProcessFailedEventArgs e)
    {
        _viewModel?.OnViewerProcessFailed();

        // One automatic reload per attach: a "ready" from the reloaded page re-sends the load command.
        if (!_reloadedAfterCrash)
        {
            _reloadedAfterCrash = true;
            sender.Reload();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewerViewModel.MapRunFolder) && _webView?.CoreWebView2 is { } core)
        {
            MapRunFolder(core, _viewModel?.MapRunFolder);
        }
    }

    private void OnMessageToViewer(string json) => _webView?.CoreWebView2?.PostWebMessageAsJson(json);

    private static void MapRunFolder(CoreWebView2 core, string? folder)
    {
        core.ClearVirtualHostNameToFolderMapping(ViewerUrls.RunHost);
        if (folder is not null)
        {
            // MEASURED 2026-10-02: DenyCors makes igv.js's fetch of https://run.deg/<name>.fasta from the
            // viewer.deg page fail ("Error accessing resource ... status: 0"). Allow is needed; the only page
            // that can reach run.deg is the navigation-locked viewer.deg bundle.
            core.SetVirtualHostNameToFolderMapping(ViewerUrls.RunHost, folder, CoreWebView2HostResourceAccessKind.Allow);
        }
    }
}
