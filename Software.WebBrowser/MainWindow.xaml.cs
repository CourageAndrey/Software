using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;

namespace Software.WebBrowser
{
	public partial class MainWindow : Window
	{
		private readonly ObservableCollection<BrowserTab> _tabs = [];

		public MainWindow()
		{
			InitializeComponent();
			TabStrip.ItemsSource = _tabs;
			AddTab();
		}

		private void NewTab_Click(object sender, RoutedEventArgs eventArgs) => AddTab();

		private BrowserTab AddTab(bool navigateHome = true)
		{
			var tab = new BrowserTab(navigateHome) { Visibility = Visibility.Collapsed };
			tab.NewTabRequested += OpenNewWindow;
			_tabs.Add(tab);
			TabHost.Children.Add(tab);
			TabStrip.SelectedItem = tab;
			TabStrip.ScrollIntoView(tab);
			return tab;
		}

		private void CloseTab_Click(object sender, RoutedEventArgs eventArgs)
		{
			if (sender is Button { Tag: BrowserTab tab })
				CloseTab(tab);
			eventArgs.Handled = true;
		}

		private void CloseTab(BrowserTab tab)
		{
			int index = _tabs.IndexOf(tab);
			if (index < 0)
				return;

			bool wasSelected = TabStrip.SelectedItem == tab;
			tab.NewTabRequested -= OpenNewWindow;
			TabHost.Children.Remove(tab);
			_tabs.Remove(tab);
			tab.Dispose();

			if (_tabs.Count == 0)
			{
				Close();
				return;
			}

			if (wasSelected)
				TabStrip.SelectedItem = _tabs[Math.Min(index, _tabs.Count - 1)];
		}

		private void TabStrip_SelectionChanged(object sender, SelectionChangedEventArgs eventArgs)
		{
			foreach (var tab in _tabs)
				tab.Visibility = TabStrip.SelectedItem == tab ? Visibility.Visible : Visibility.Collapsed;

			if (TabStrip.SelectedItem is BrowserTab selected)
				Title = $"{selected.Title} - Web Browser";
		}

		private async void OpenNewWindow(CoreWebView2NewWindowRequestedEventArgs eventArgs)
		{
			using var deferral = eventArgs.GetDeferral();
			eventArgs.Handled = true;
			var tab = AddTab(navigateHome: false);
			await tab.InitializeAsync();
			if (tab.Core != null)
				eventArgs.NewWindow = tab.Core;
		}

		private void Window_PreviewKeyDown(object sender, KeyEventArgs eventArgs)
		{
			Key key = eventArgs.Key;
			ModifierKeys modifiers = Keyboard.Modifiers;
			if ((modifiers & ModifierKeys.Control) != 0 && (modifiers & ModifierKeys.Alt) == 0
				&& key is Key.T or Key.W or Key.Tab or Key.L)
			{
				eventArgs.Handled = true;
				Dispatcher.BeginInvoke(() => HandleShortcut(key, modifiers));
			}
		}

		private bool HandleShortcut(Key key, ModifierKeys modifiers)
		{
			if ((modifiers & ModifierKeys.Control) == 0 || (modifiers & ModifierKeys.Alt) != 0)
				return false;

			switch (key)
			{
				case Key.T:
					AddTab();
					return true;
				case Key.W when TabStrip.SelectedItem is BrowserTab selected:
					CloseTab(selected);
					return true;
				case Key.Tab when _tabs.Count > 0:
					int direction = (modifiers & ModifierKeys.Shift) != 0 ? -1 : 1;
					TabStrip.SelectedIndex = (TabStrip.SelectedIndex + direction + _tabs.Count) % _tabs.Count;
					TabStrip.ScrollIntoView(TabStrip.SelectedItem);
					return true;
				case Key.L when TabStrip.SelectedItem is BrowserTab selected:
					selected.FocusAddress();
					return true;
				default:
					return false;
			}
		}

		private void Window_Closed(object? sender, EventArgs eventArgs)
		{
			foreach (var tab in _tabs)
			{
				tab.NewTabRequested -= OpenNewWindow;
				tab.Dispose();
			}
		}
	}
}