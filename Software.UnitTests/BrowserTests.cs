using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Software.WebBrowser;

namespace Software.UnitTests
{
	[Apartment(ApartmentState.STA)]
	public class BrowserTests
	{
		private MainWindow window = null!;

		[SetUp]
		public void Setup() => window = new MainWindow();

		[TearDown]
		public void TearDown() => window.Close();

		private Grid TabHost => (Grid)window.FindName("TabHost");

		private ListBox TabStrip => (ListBox)window.FindName("TabStrip");

		private BrowserTab[] Tabs => TabHost.Children.OfType<BrowserTab>().ToArray();

		private void AddTab() => ((Button)window.FindName("NewTabButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

		private void CloseTab(BrowserTab tab)
		{
			TabStrip.Measure(new Size(1000, 100));
			TabStrip.Arrange(new Rect(0, 0, 1000, 100));
			TabStrip.UpdateLayout();
			var container = (ListBoxItem)TabStrip.ItemContainerGenerator.ContainerFromItem(tab);
			var button = Descendants<Button>(container).Single(child => child.Tag == tab);
			button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
		}

		private static IEnumerable<TElement> Descendants<TElement>(DependencyObject parent) where TElement : DependencyObject
		{
			for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
			{
				DependencyObject child = VisualTreeHelper.GetChild(parent, index);
				if (child is TElement element)
					yield return element;
				foreach (var descendant in Descendants<TElement>(child))
					yield return descendant;
			}
		}

		[Test]
		public void StartsWithOneSelectedTab()
		{
			Assert.That(Tabs, Has.Length.EqualTo(1));
			Assert.That(TabStrip.SelectedItem, Is.SameAs(Tabs[0]));
			Assert.That(Tabs[0].Visibility, Is.EqualTo(Visibility.Visible));
		}

		[Test]
		public void AddingTabsSelectsTheNewTabWithoutTheOldFourPanelLimit()
		{
			for (int index = 0; index < 5; index++)
				AddTab();

			Assert.That(Tabs, Has.Length.EqualTo(6));
			Assert.That(TabStrip.SelectedItem, Is.SameAs(Tabs[^1]));
			Assert.That(Tabs.Count(tab => tab.Visibility == Visibility.Visible), Is.EqualTo(1));
			Assert.That(Tabs.Select(tab => tab.FindName("Browser")).Distinct().Count(), Is.EqualTo(6));
		}

		[Test]
		public void SwitchingTabsKeepsBrowserInstancesAndOnlyShowsTheSelectedPage()
		{
			var firstTab = Tabs[0];
			var firstBrowser = firstTab.FindName("Browser");
			AddTab();
			TabStrip.SelectedItem = firstTab;

			Assert.That(firstTab.FindName("Browser"), Is.SameAs(firstBrowser));
			Assert.That(firstTab.Visibility, Is.EqualTo(Visibility.Visible));
			Assert.That(Tabs[1].Visibility, Is.EqualTo(Visibility.Collapsed));
		}

		[Test]
		public void ClosingTabsDisposesThemAndSelectsAnAdjacentTab()
		{
			AddTab();
			AddTab();
			var selected = Tabs[2];
			var background = Tabs[0];
			CloseTab(background);
			Assert.That(TabStrip.SelectedItem, Is.SameAs(selected));
			Assert.That(background.Core, Is.Null);

			CloseTab(selected);
			Assert.That(Tabs, Has.Length.EqualTo(1));
			Assert.That(TabStrip.SelectedItem, Is.SameAs(Tabs[0]));
			Assert.That(Tabs[0].Visibility, Is.EqualTo(Visibility.Visible));
			Assert.That(selected.Core, Is.Null);
		}

		[Test]
		public void ClosingTheLastTabClosesTheWindow()
		{
			bool closed = false;
			window.Closed += (_, _) => closed = true;
			CloseTab(Tabs[0]);
			Assert.That(closed, Is.True);
		}

		[Test]
		public void ChromiumPreservesTabContentAndOpensPopupsInNewTabs()
		{
			try
			{
				CoreWebView2Environment.GetAvailableBrowserVersionString();
			}
			catch (WebView2RuntimeNotFoundException)
			{
				Assert.Ignore("Install the WebView2 Runtime to run the Chromium smoke test.");
			}

			AddTab();
			var operation = window.Dispatcher.InvokeAsync(VerifyChromiumAsync);
			Task task = operation.Task.Unwrap();
			var frame = new DispatcherFrame();
			var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
			timeout.Tick += (_, _) => frame.Continue = false;
			_ = task.ContinueWith(_ => window.Dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
			timeout.Start();
			Dispatcher.PushFrame(frame);
			timeout.Stop();
			Assert.That(task.IsCompleted, Is.True, "Chromium did not finish within 30 seconds.");
			task.GetAwaiter().GetResult();
		}

		private async Task VerifyChromiumAsync()
		{
			var browsers = Tabs.Select(tab => (WebView2)tab.FindName("Browser")).ToArray();
			window.Show();

			for (int index = 0; index < browsers.Length; index++)
			{
				TabStrip.SelectedItem = Tabs[index];
				await Tabs[index].InitializeAsync();
				Assert.That(Tabs[index].Core, Is.Not.Null, ((TextBlock)Tabs[index].FindName("StatusText")).Text);
				var completion = new TaskCompletionSource();
				var browser = browsers[index];
				void OnCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs eventArgs)
				{
					if (browser.CoreWebView2.Source == "about:blank" && eventArgs.IsSuccess)
						completion.TrySetResult();
				}

				browser.CoreWebView2.NavigationCompleted += OnCompleted;
				try
				{
					browser.NavigateToString($"<!DOCTYPE html><html><head><title>Tab {index}</title></head><body>Content {index}</body></html>");
					await completion.Task;
					await browser.ExecuteScriptAsync($"window.savedState = {index}");
				}
				finally
				{
					browser.CoreWebView2.NavigationCompleted -= OnCompleted;
				}
			}

			for (int index = 0; index < browsers.Length; index++)
			{
				TabStrip.SelectedItem = Tabs[index];
				await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
				string content = await browsers[index].ExecuteScriptAsync("document.body.textContent");
				Assert.That(JsonSerializer.Deserialize<string>(content), Is.EqualTo($"Content {index}"));
				Assert.That(await browsers[index].ExecuteScriptAsync("window.savedState"), Is.EqualTo(index.ToString()));
				Assert.That(Tabs[index].Title, Is.EqualTo($"Tab {index}"));
				Assert.That(((TextBox)Tabs[index].FindName("AddressBox")).Text, Is.EqualTo("about:blank"));
				Assert.That(window.Title, Is.EqualTo($"Tab {index} - Web Browser"));
			}

			var popupRequested = new TaskCompletionSource<BrowserTab>();
			browsers[1].CoreWebView2.NewWindowRequested += (_, _) => popupRequested.TrySetResult(Tabs[^1]);
			await browsers[1].ExecuteScriptAsync("window.open('about:blank', '_blank')");
			var popup = await popupRequested.Task;
			await popup.InitializeAsync();
			await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
			Assert.That(Tabs, Has.Length.EqualTo(3));
			Assert.That(TabStrip.SelectedItem, Is.SameAs(popup));
			Assert.That(popup.Core, Is.Not.Null);
			Assert.That(await popup.Core!.ExecuteScriptAsync("window.opener !== null"), Is.EqualTo("true"));

			CloseTab(popup);
			TabStrip.SelectedItem = Tabs[0];
			Assert.That(await browsers[0].ExecuteScriptAsync("window.savedState"), Is.EqualTo("0"));
		}
	}
}
