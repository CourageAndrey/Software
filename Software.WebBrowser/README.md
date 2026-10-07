# Web Browser

A WPF browser powered by Microsoft Edge WebView2 (Chromium).

## Run

Requires the .NET 10 SDK and the [Microsoft Edge WebView2 Evergreen Runtime](https://developer.microsoft.com/microsoft-edge/webview2/). The runtime is already installed on most Windows systems.

From the solution directory:

```powershell
dotnet run --project Software.WebBrowser
```

The browser opens with one tab. Use the plus button to add tabs, click a tab to switch pages, or use its close button. The tab strip scrolls when it fills the window; there is no fixed tab limit. Closing the last tab opens a new blank tab instead of closing the window.

The selected tab provides an address/search bar, back/forward history, reload/stop, and home controls. Enter a URL or search terms and press Enter. New-window links and popups open in new tabs.

Keyboard shortcuts: Ctrl+T opens a tab, Ctrl+W closes the current tab, Ctrl+Tab and Ctrl+Shift+Tab cycle tabs, and Ctrl+L selects the address bar.

Tabs retain their pages, page state, and independent navigation histories when switched. They share cookies and site storage. The persistent browser profile is stored in `%LOCALAPPDATA%\Software\WebBrowser\UserData`.

## Tests

```powershell
dotnet test Software.UnitTests --filter FullyQualifiedName~BrowserTests
```

The Chromium smoke test opens a window and verifies independent local HTML content, retained page state when switching tabs, and popup routing to a new tab. It is skipped when the WebView2 Runtime is not installed.