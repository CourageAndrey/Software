using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;

namespace Software.WebBrowser
{
	public partial class BrowserTab : UserControl, IDisposable
	{
		private const string HomeAddress = "about:blank";
		private static Task<CoreWebView2Environment>? environmentTask;
		private readonly bool navigateHome;
		private Task? initializationTask;
		private bool initialized;
		private bool disposed;
		private bool loading;

		public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
			nameof(Title), typeof(string), typeof(BrowserTab), new PropertyMetadata("New tab"));

		public string Title
		{
			get => (string)GetValue(TitleProperty);
			private set => SetValue(TitleProperty, value);
		}

		public CoreWebView2? Core => disposed ? null : Browser.CoreWebView2;

		public event Action<CoreWebView2NewWindowRequestedEventArgs>? NewTabRequested;

		public BrowserTab(bool navigateHome = true)
		{
			this.navigateHome = navigateHome;
			InitializeComponent();
		}

		private async void Tab_Loaded(object sender, RoutedEventArgs eventArgs) => await InitializeAsync();

		public Task InitializeAsync() => initializationTask ??= InitializeBrowserAsync();

		private async void Retry_Click(object sender, RoutedEventArgs eventArgs)
		{
			if (!initialized && initializationTask?.IsCompleted == true)
				initializationTask = null;
			await InitializeAsync();
		}

		private async Task InitializeBrowserAsync()
		{
			if (disposed)
				return;

			RetryButton.Visibility = Visibility.Collapsed;
			StatusText.Text = "Starting browser...";
			try
			{
				string profilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
					"Software", "WebBrowser", "UserData");
				if (environmentTask == null || environmentTask.IsFaulted)
					environmentTask = CoreWebView2Environment.CreateAsync(userDataFolder: profilePath);
				var environment = await environmentTask;
				if (disposed)
					return;

				await Browser.EnsureCoreWebView2Async(environment);
				if (disposed)
					return;

				Browser.CoreWebView2.NavigationStarting += NavigationStarting;
				Browser.CoreWebView2.NavigationCompleted += NavigationCompleted;
				Browser.CoreWebView2.SourceChanged += SourceChanged;
				Browser.CoreWebView2.HistoryChanged += HistoryChanged;
				Browser.CoreWebView2.DocumentTitleChanged += DocumentTitleChanged;
				Browser.CoreWebView2.NewWindowRequested += NewWindowRequested;
				Browser.CoreWebView2.ProcessFailed += ProcessFailed;
				initialized = true;
				AddressBox.IsEnabled = GoButton.IsEnabled = HomeButton.IsEnabled = ReloadButton.IsEnabled = true;
				if (navigateHome)
					Browser.CoreWebView2.Navigate(HomeAddress);
			}
			catch (Exception exception)
			{
				if (!disposed)
				{
					StatusText.Text = exception is WebView2RuntimeNotFoundException
						? "Install the Microsoft Edge WebView2 Runtime, then retry."
						: $"Browser could not start: {exception.Message}";
					RetryButton.Visibility = Visibility.Visible;
				}
			}
		}

		public void FocusAddress()
		{
			AddressBox.Focus();
			AddressBox.SelectAll();
		}

		private void NavigateAddress()
		{
			if (!initialized || disposed)
				return;

			string input = AddressBox.Text.Trim();
			if (input.Length == 0)
				return;

			if (Uri.TryCreate(input, UriKind.Absolute, out var address)
				&& (address.Scheme == Uri.UriSchemeHttp || address.Scheme == Uri.UriSchemeHttps))
			{
				Browser.CoreWebView2.Navigate(address.AbsoluteUri);
			}
			else if (!input.Any(char.IsWhiteSpace)
				&& (input.Contains('.') || input.StartsWith("localhost", StringComparison.OrdinalIgnoreCase))
				&& Uri.TryCreate($"https://{input}", UriKind.Absolute, out address))
			{
				Browser.CoreWebView2.Navigate(address.AbsoluteUri);
			}
			else
			{
				Browser.CoreWebView2.Navigate($"https://www.bing.com/search?q={Uri.EscapeDataString(input)}");
			}
			Browser.Focus();
		}

		private void Address_KeyDown(object sender, KeyEventArgs eventArgs)
		{
			if (eventArgs.Key == Key.Enter)
			{
				NavigateAddress();
				eventArgs.Handled = true;
			}
		}

		private void Go_Click(object sender, RoutedEventArgs eventArgs) => NavigateAddress();

		private void Back_Click(object sender, RoutedEventArgs eventArgs)
		{
			if (Browser.CanGoBack)
				Browser.GoBack();
		}

		private void Forward_Click(object sender, RoutedEventArgs eventArgs)
		{
			if (Browser.CanGoForward)
				Browser.GoForward();
		}

		private void Reload_Click(object sender, RoutedEventArgs eventArgs)
		{
			if (loading)
				Browser.CoreWebView2.Stop();
			else
				Browser.Reload();
		}

		private void Home_Click(object sender, RoutedEventArgs eventArgs) => Browser.CoreWebView2.Navigate(HomeAddress);

		private void NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs eventArgs)
		{
			loading = true;
			ReloadButton.Content = "\uE711";
			ReloadButton.ToolTip = "Stop";
			StatusText.Text = "Loading...";
		}

		private void NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs eventArgs)
		{
			loading = false;
			ReloadButton.Content = "\uE72C";
			ReloadButton.ToolTip = "Reload";
			AddressBox.Text = Browser.CoreWebView2.Source;
			StatusText.Text = eventArgs.IsSuccess ? "Ready" : $"Navigation failed: {eventArgs.WebErrorStatus}";
			UpdateHistory();
		}

		private void SourceChanged(object? sender, CoreWebView2SourceChangedEventArgs eventArgs)
			=> AddressBox.Text = Browser.CoreWebView2.Source;

		private void HistoryChanged(object? sender, object eventArgs) => UpdateHistory();

		private void UpdateHistory()
		{
			BackButton.IsEnabled = Browser.CanGoBack;
			ForwardButton.IsEnabled = Browser.CanGoForward;
		}

		private void DocumentTitleChanged(object? sender, object eventArgs)
		{
			Title = string.IsNullOrWhiteSpace(Browser.CoreWebView2.DocumentTitle)
				? "New tab" : Browser.CoreWebView2.DocumentTitle;
			if (Visibility == Visibility.Visible && Window.GetWindow(this) is Window window)
				window.Title = $"{Title} - Web Browser";
		}

		private void NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs eventArgs)
		{
			eventArgs.Handled = true;
			NewTabRequested?.Invoke(eventArgs);
		}

		private void ProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs eventArgs)
		{
			StatusText.Text = $"Browser process failed: {eventArgs.ProcessFailedKind}. Close this tab and open a new one to recover.";
		}

		public void Dispose()
		{
			if (disposed)
				return;

			disposed = true;
			Browser.Dispose();
			GC.SuppressFinalize(this);
		}
	}
}