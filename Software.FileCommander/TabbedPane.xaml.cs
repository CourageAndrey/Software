using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace Software.FileCommander
{
	/// <summary>One side of the commander: a strip of folder tabs, each backed by its own <see cref="FilePane"/>.</summary>
	public partial class TabbedPane : UserControl
	{
		private sealed record FolderTab(TabItem Item, FilePane Pane, TextBlock Title, Button CloseButton);

		private readonly List<FolderTab> _tabs = [];
		private bool _isActive;

		public FilePane ActivePane { get; private set; }
		public IReadOnlyList<FilePane> Panes => _tabs.Select(tab => tab.Pane).ToArray();
		public event Action<TabbedPane>? Activated;
		public event Action<TabbedPane, FileCommand>? CommandRequested;

		public bool IsActive
		{
			get => _isActive;
			set
			{
				_isActive = value;
				foreach (var tab in _tabs)
				{
					tab.Pane.IsActive = value && tab.Pane == ActivePane;
				}
			}
		}

		public TabbedPane()
		{
			InitializeComponent();
			ActivePane = AddTab();
		}

		/// <summary>Opens a new tab next to the current one and navigates it to <paramref name="path"/>, or to the user folder when it is empty.</summary>
		public async Task OpenTabAsync(string path)
		{
			var pane = AddTab();
			await pane.NavigateAsync(path.Length > 0 ? path : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
			if (pane == ActivePane)
			{
				pane.FocusList();
			}
		}

		/// <summary>Closes the tab of <paramref name="pane"/>; the last remaining tab stays open.</summary>
		public void CloseTab(FilePane pane)
		{
			var tab = _tabs.SingleOrDefault(tab => tab.Pane == pane);
			if (tab == null || _tabs.Count == 1)
			{
				return;
			}

			int index = TabStrip.Items.IndexOf(tab.Item);
			if (tab.Item.IsSelected)
			{
				TabStrip.SelectedIndex = index == TabStrip.Items.Count - 1 ? index - 1 : index + 1;
			}

			_tabs.Remove(tab);
			TabStrip.Items.Remove(tab.Item);
			PaneStack.Children.Remove(pane);
			pane.Close();
			UpdateCloseButtons();
		}

		public void SelectAdjacentTab(int offset)
		{
			int count = TabStrip.Items.Count;
			TabStrip.SelectedIndex = ((TabStrip.SelectedIndex + offset) % count + count) % count;
		}

		private FilePane AddTab()
		{
			var pane = new FilePane { Visibility = Visibility.Collapsed };
			pane.Activated += _ => Activated?.Invoke(this);
			pane.CommandRequested += (_, command) => CommandRequested?.Invoke(this, command);

			var title = new TextBlock { MaxWidth = 160, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
			var closeButton = new Button
			{
				Style = (Style)FindResource("TabButton"), Content = "", FontSize = 9, Width = 16, Height = 16, Margin = new Thickness(6, 0, 0, 0),
				ToolTip = "Close tab (Ctrl+W)"
			};
			AutomationProperties.SetName(closeButton, "Close tab");
			var header = new StackPanel { Orientation = Orientation.Horizontal };
			header.Children.Add(title);
			header.Children.Add(closeButton);
			var item = new TabItem { Header = header };
			var tab = new FolderTab(item, pane, title, closeButton);
			item.Tag = tab;
			item.ContextMenu = BuildContextMenu(tab);
			item.MouseUp += (_, eventArgs) =>
			{
				if (eventArgs.ChangedButton == MouseButton.Middle)
				{
					eventArgs.Handled = true;
					CloseTab(pane);
				}
			};
			closeButton.Click += (_, _) => CloseTab(pane);
			pane.Navigated += _ => UpdateTitle(tab);
			UpdateTitle(tab);

			_tabs.Add(tab);
			PaneStack.Children.Add(pane);
			TabStrip.Items.Insert(TabStrip.SelectedIndex + 1, item);
			TabStrip.SelectedItem = item;
			UpdateCloseButtons();
			return pane;
		}

		private ContextMenu BuildContextMenu(FolderTab tab)
		{
			var newTab = new MenuItem { Header = "New tab", InputGestureText = "Ctrl+T" };
			var close = new MenuItem { Header = "Close tab", InputGestureText = "Ctrl+W" };
			var closeOthers = new MenuItem { Header = "Close other tabs" };
			newTab.Click += async (_, _) => await OpenTabAsync(tab.Pane.CurrentPath);
			close.Click += (_, _) => CloseTab(tab.Pane);
			closeOthers.Click += (_, _) =>
			{
				foreach (var other in _tabs.Where(other => other != tab).ToArray())
				{
					CloseTab(other.Pane);
				}
			};
			var menu = new ContextMenu();
			menu.Items.Add(newTab);
			menu.Items.Add(new Separator());
			menu.Items.Add(close);
			menu.Items.Add(closeOthers);
			menu.Opened += (_, _) => close.IsEnabled = closeOthers.IsEnabled = _tabs.Count > 1;
			return menu;
		}

		private static void UpdateTitle(FolderTab tab)
		{
			string path = tab.Pane.CurrentPath;
			string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
			tab.Title.Text = name.Length > 0 ? name : Path.TrimEndingDirectorySeparator(path);
			tab.Item.ToolTip = path.Length > 0 ? path : null;
		}

		private void UpdateCloseButtons()
		{
			foreach (var tab in _tabs)
			{
				tab.CloseButton.Visibility = _tabs.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
			}
		}

		private void Tab_SelectionChanged(object sender, SelectionChangedEventArgs eventArgs)
		{
			if (eventArgs.OriginalSource != TabStrip || TabStrip.SelectedItem is not TabItem { Tag: FolderTab selected })
			{
				return;
			}

			ActivePane = selected.Pane;
			foreach (var tab in _tabs)
			{
				tab.Pane.Visibility = tab == selected ? Visibility.Visible : Visibility.Collapsed;
			}
			IsActive = _isActive;
			if (IsLoaded)
			{
				// Keep the keyboard in the file list, as clicking a tab header would otherwise move focus to the header.
				Dispatcher.BeginInvoke(() => ActivePane.FocusList(), DispatcherPriority.Input);
			}
		}

		private void Strip_PreviewMouseDown(object sender, MouseButtonEventArgs eventArgs) => Activated?.Invoke(this);

		private async void NewTab_Click(object sender, RoutedEventArgs eventArgs) => await OpenTabAsync(ActivePane.CurrentPath);
	}
}
