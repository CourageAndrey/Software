using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using Microsoft.Win32;

namespace Software.MediaPlayer
{
	public partial class MainWindow : Window
	{
		private const string _mediaFilter = "Media files|*.mp3;*.flac;*.wav;*.ogg;*.m4a;*.aac;*.wma;*.opus;*.mp4;*.mkv;*.avi;*.mov;*.webm;*.wmv;*.mpeg;*.mpg;*.ts|All files|*.*";
		private readonly Playlist _playlist = new();
		private readonly string[] _arguments;
		private readonly bool _testOutput;
		private readonly DispatcherTimer _timer;
		private PlaybackEngine? _engine;
		private bool _closed;
		private bool _seeking;
		private bool _fullscreen;
		private bool _playlistVisible = true;
		private bool _stopped = true;
		private WindowStyle _previousStyle;
		private ResizeMode _previousResize;
		private WindowState _previousState;
		public Playlist Playlist => _playlist;
		public PlaybackEngine? Engine => _engine;

		public MainWindow() : this([], false) { }

		public MainWindow(string[] arguments, bool testOutput = false)
		{
			_arguments = arguments;
			_testOutput = testOutput;
			InitializeComponent();
			PlaylistList.ItemsSource = _playlist.Items;
			_playlist.Items.CollectionChanged += (_, _) => PlaylistCount.Text = _playlist.Items.Count.ToString();
			_timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
			_timer.Tick += (_, _) => UpdatePlayback();
		}

		private void Window_Loaded(object sender, RoutedEventArgs eventArgs)
		{
			if (_engine != null)
			{
				return;
			}
			try
			{
				_engine = new PlaybackEngine(_testOutput);
				VideoSurface.MediaPlayer = _engine.Player;
				_engine.Ended += Playback_Ended;
				_engine.Failed += Playback_Failed;
				_engine.Player.Playing += Playback_Started;
				_timer.Start();
				if (_arguments.Length > 0)
				{
					AddSources(_arguments, play: true);
				}
			}
			catch (Exception exception)
			{
				StatusText.Text = "Playback engine could not start: " + exception.Message;
				PlaybackControls.IsEnabled = false;
			}
		}

		public void AddSources(IEnumerable<string> sources, bool play = false)
		{
			int first = _playlist.Items.Count;
			foreach (string source in sources)
			{
				if (Path.GetExtension(source).ToLowerInvariant() is ".m3u" or ".m3u8" && File.Exists(source))
				{
					_playlist.Load(source);
				}
				else
				{
					_playlist.Add([source]);
				}
			}
			if (play && _playlist.Items.Count > first)
			{
				PlayIndex(first);
			}
		}

		public void PlayIndex(int index, bool preserveCycle = false)
		{
			if (_engine == null)
			{
				return;
			}
			var item = _playlist.Select(index, resetCycle: !preserveCycle);
			PlaylistList.SelectedItem = item;
			PlaylistList.ScrollIntoView(item);
			TrackLabel.Text = AudioTitle.Text = item.Title;
			TrackLabel.ToolTip = item.Location;
			ArtistLabel.Text = "";
			Title = item.Title + " - Media Player";
			_stopped = false;
			StatusText.Text = "Opening...";
			if (!_engine.Play(item.Location))
			{
				_stopped = true;
				StatusText.Text = "The media could not be opened.";
			}
			_engine.Player.Volume = (int)VolumeSlider.Value;
		}

		private void OpenFiles_Click(object sender, RoutedEventArgs eventArgs)
		{
			var dialog = new OpenFileDialog { Title = "Open media", Multiselect = true, Filter = _mediaFilter };
			if (dialog.ShowDialog(this) == true)
			{
				TryAction(() => AddSources(dialog.FileNames, play: true));
			}
		}
		private void AddFolder_Click(object sender, RoutedEventArgs eventArgs)
		{
			var dialog = new OpenFolderDialog { Title = "Add media folder" };
			if (dialog.ShowDialog(this) != true)
			{
				return;
			}
			TryAction(() =>
			{
				string[] extensions = [".mp3", ".flac", ".wav", ".ogg", ".m4a", ".aac", ".wma", ".opus", ".mp4", ".mkv", ".avi", ".mov", ".webm", ".wmv", ".mpeg", ".mpg", ".ts"];
				var files = Directory.EnumerateFiles(dialog.FolderName).Where(path => extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)).OrderBy(path => path).ToArray();
				_playlist.Add(files);
			});
		}
		private void OpenPlaylist_Click(object sender, RoutedEventArgs eventArgs)
		{
			var dialog = new OpenFileDialog { Title = "Load playlist", Filter = "M3U playlists|*.m3u;*.m3u8" };
			if (dialog.ShowDialog(this) == true)
			{
				TryAction(() => _playlist.Load(dialog.FileName));
			}
		}
		private void SavePlaylist_Click(object sender, RoutedEventArgs eventArgs)
		{
			var dialog = new SaveFileDialog { Title = "Save playlist", Filter = "UTF-8 playlist|*.m3u8", DefaultExt = ".m3u8", AddExtension = true, OverwritePrompt = true };
			if (dialog.ShowDialog(this) == true)
			{
				TryAction(() => _playlist.Save(dialog.FileName));
			}
		}
		private void OpenStream_Click(object sender, RoutedEventArgs eventArgs)
		{
			var input = new TextBox { Padding = new Thickness(6), Margin = new Thickness(0, 0, 0, 14) };
			var ok = new Button { Content = "Open", IsDefault = true, MinWidth = 80 };
			var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 };
			var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
			buttons.Children.Add(ok); buttons.Children.Add(cancel);
			var content = new StackPanel { Margin = new Thickness(18) };
			content.Children.Add(new TextBlock { Text = "Stream URL", Margin = new Thickness(0, 0, 0, 6) }); content.Children.Add(input); content.Children.Add(buttons);
			var dialog = new Window { Owner = this, Title = "Open network stream", Content = content, Width = 480, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
			ok.Click += (_, _) => dialog.DialogResult = true;
			if (dialog.ShowDialog() == true)
			{
				TryAction(() =>
				{
					if (!Uri.TryCreate(input.Text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "rtsp"))
					{
						throw new ArgumentException("Enter an HTTP, HTTPS, or RTSP stream URL.");
					}
					AddSources([uri.AbsoluteUri], play: true);
				});
			}
		}

		private void Play_Click(object sender, RoutedEventArgs eventArgs)
		{
			if (_engine == null || _playlist.Items.Count == 0)
			{
				return;
			}
			if (_engine.Player.IsPlaying)
			{
				_engine.Player.SetPause(true);
			}
			else if (_engine.Player.State == VLCState.Paused)
			{
				_engine.Player.SetPause(false);
			}
			else
			{
				PlayIndex(PlaylistList.SelectedIndex >= 0 ? PlaylistList.SelectedIndex : Math.Max(0, _playlist.CurrentIndex));
			}
		}
		private void Stop_Click(object sender, RoutedEventArgs eventArgs)
		{
			_engine?.Player.Stop();
			_stopped = true;
			SeekSlider.Value = 0;
			TimeLabel.Text = "00:00 / 00:00";
			StatusText.Text = "Stopped";
		}
		private void Previous_Click(object sender, RoutedEventArgs eventArgs)
		{
			if (_engine?.Player.Time > 3000)
			{
				_engine.Player.Time = 0;
				return;
			}
			if (_playlist.Previous() != null)
			{
				PlayIndex(_playlist.CurrentIndex);
			}
		}
		private void Next_Click(object sender, RoutedEventArgs eventArgs)
		{
			if (_playlist.Next() != null)
			{
				PlayIndex(_playlist.CurrentIndex);
			}
		}
		private void Playlist_DoubleClick(object sender, MouseButtonEventArgs eventArgs)
		{
			if (PlaylistList.SelectedIndex >= 0 && ItemsControl.ContainerFromElement(PlaylistList, eventArgs.OriginalSource as DependencyObject) is ListBoxItem)
			{
				PlayIndex(PlaylistList.SelectedIndex);
			}
		}
		private void Playlist_KeyDown(object sender, KeyEventArgs eventArgs)
		{
			if (eventArgs.Key == Key.Enter && PlaylistList.SelectedIndex >= 0)
			{
				eventArgs.Handled = true; PlayIndex(PlaylistList.SelectedIndex);
			}
			else if (eventArgs.Key == Key.Delete)
			{
				eventArgs.Handled = true; Remove_Click(sender, new RoutedEventArgs());
			}
		}
		private void Remove_Click(object sender, RoutedEventArgs eventArgs)
		{
			var selected = PlaylistList.SelectedItems.Cast<PlaylistItem>().ToArray();
			foreach (var item in selected)
			{
				if (_playlist.Current?.Id == item.Id)
				{
					Stop_Click(sender, eventArgs);
				}
				_playlist.Remove(item.Id);
			}
		}
		private void Clear_Click(object sender, RoutedEventArgs eventArgs)
		{
			Stop_Click(sender, eventArgs); _playlist.Clear(); TrackLabel.Text = AudioTitle.Text = "No media"; ArtistLabel.Text = ""; Title = "Media Player";
		}
		private void Modes_Changed(object sender, RoutedEventArgs eventArgs) => _playlist.Shuffle = ShuffleBox.IsChecked == true;
		private void Repeat_Changed(object sender, SelectionChangedEventArgs eventArgs) => _playlist.Repeat = (RepeatMode)RepeatBox.SelectedIndex;
		private void Volume_Changed(object sender, RoutedPropertyChangedEventArgs<double> eventArgs)
		{
			if (_engine != null)
			{
				_engine.Player.Volume = (int)eventArgs.NewValue;
			}
		}
		private void Mute_Click(object sender, RoutedEventArgs eventArgs)
		{
			if (_engine != null)
			{
				_engine.Player.Mute = !_engine.Player.Mute;
				MuteButton.Content = _engine.Player.Mute ? "\uE74F" : "\uE767";
				MuteButton.ToolTip = _engine.Player.Mute ? "Unmute" : "Mute";
			}
		}
		private void Speed_Changed(object sender, SelectionChangedEventArgs eventArgs)
		{
			if (_engine != null && SpeedBox.SelectedItem is ComboBoxItem { Tag: string rate })
			{
				if (_engine.Player.SetRate(float.Parse(rate, System.Globalization.CultureInfo.InvariantCulture)) != 0)
				{
					StatusText.Text = "This media does not support changing playback speed.";
				}
			}
		}
		private void Seek_Start(object sender, MouseButtonEventArgs eventArgs) => _seeking = true;
		private void Seek_End(object sender, RoutedEventArgs eventArgs)
		{
			if (_seeking)
			{
				_seeking = false;
				SeekToSlider();
			}
		}
		private void Seek_KeyUp(object sender, KeyEventArgs eventArgs) => SeekToSlider();
		private void SeekToSlider()
		{
			if (_engine?.Player.IsSeekable == true)
			{
				_engine.Player.Position = (float)(SeekSlider.Value / 1000);
			}
		}

		private void Playback_Ended(object? sender, EventArgs eventArgs)
		{
			Dispatch(() =>
			{
				if (_playlist.Next(automatic: true) != null)
				{
					PlayIndex(_playlist.CurrentIndex, preserveCycle: true);
				}
				else
				{
					_stopped = true;
					StatusText.Text = "Playlist finished.";
				}
			});
		}
		private void Playback_Started(object? sender, EventArgs eventArgs)
		{
			Dispatch(() =>
			{
				if (_engine == null)
				{
					return;
				}
				_engine.Player.Volume = (int)VolumeSlider.Value;
				if (SpeedBox.SelectedItem is ComboBoxItem { Tag: string rate })
				{
					_engine.Player.SetRate(float.Parse(rate, System.Globalization.CultureInfo.InvariantCulture));
				}
			});
		}
		private void Playback_Failed(object? sender, EventArgs eventArgs) => Dispatch(() => { _stopped = true; StatusText.Text = "Playback failed. The file or stream may be unavailable or unsupported."; });
		private void Dispatch(Action action)
		{
			if (!_closed)
			{
				_ = Dispatcher.BeginInvoke(() =>
				{
					if (!_closed)
					{
						action();
					}
				});
			}
		}
		private void UpdatePlayback()
		{
			if (_engine == null)
			{
				return;
			}
			var player = _engine.Player;
			PlayButton.Content = player.IsPlaying ? "\uE769" : "\uE768";
			SeekSlider.IsEnabled = player.IsSeekable && player.Length > 0 && !_stopped;
			if (!_seeking && !_stopped)
			{
				SeekSlider.Value = Math.Clamp(player.Position * 1000d, 0, 1000);
				TimeLabel.Text = FormatTime(player.Time) + " / " + (player.Length > 0 ? FormatTime(player.Length) : "LIVE");
				if (player.State is VLCState.Playing or VLCState.Paused)
				{
					StatusText.Text = player.State == VLCState.Playing ? "Playing" : "Paused";
					string title = _engine.Metadata(MetadataType.Title);
					AudioTitle.Text = title.Length > 0 ? title : _playlist.Current?.Title ?? "No media";
					ArtistLabel.Text = _engine.Metadata(MetadataType.Artist);
				}
			}
			AudioSurface.Visibility = player.VideoTrackCount > 0 && !_stopped ? Visibility.Collapsed : Visibility.Visible;
		}
		public static string FormatTime(long milliseconds)
		{
			var time = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
			return time.TotalHours >= 1 ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}" : $"{time.Minutes:00}:{time.Seconds:00}";
		}
		private void PlaylistVisibility_Click(object sender, RoutedEventArgs eventArgs)
		{
			_playlistVisible = !_playlistVisible;
			UpdatePlaylistVisibility();
		}
		private void UpdatePlaylistVisibility()
		{
			bool visible = _playlistVisible && !_fullscreen;
			PlaylistPanel.Visibility = PlaylistSplitter.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
			PlaylistColumn.Width = new GridLength(visible ? 310 : 0);
			PlaylistDivider.Width = new GridLength(visible ? 7 : 0);
		}
		private void Fullscreen_Click(object sender, RoutedEventArgs eventArgs) => ToggleFullscreen();
		public void ToggleFullscreen()
		{
			if (!_fullscreen)
			{
				_previousStyle = WindowStyle; _previousResize = ResizeMode; _previousState = WindowState;
				WindowState = WindowState.Normal; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; WindowState = WindowState.Maximized;
			}
			else
			{
				WindowState = WindowState.Normal; WindowStyle = _previousStyle; ResizeMode = _previousResize; WindowState = _previousState;
			}
			_fullscreen = !_fullscreen;
			Toolbar.Visibility = _fullscreen ? Visibility.Collapsed : Visibility.Visible;
			UpdatePlaylistVisibility();
		}
		private void Video_Click(object sender, MouseButtonEventArgs eventArgs)
		{
			if (eventArgs.ClickCount == 2)
			{
				ToggleFullscreen();
			}
		}
		private void Window_PreviewKeyDown(object sender, KeyEventArgs eventArgs)
		{
			if (Keyboard.FocusedElement is TextBox)
			{
				return;
			}
			if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && eventArgs.Key == Key.O)
			{
				eventArgs.Handled = true; OpenFiles_Click(sender, new RoutedEventArgs());
			}
			else if (eventArgs.Key == Key.Space)
			{
				eventArgs.Handled = true; Play_Click(sender, new RoutedEventArgs());
			}
			else if (eventArgs.Key is Key.F11 or Key.F)
			{
				eventArgs.Handled = true; ToggleFullscreen();
			}
			else if (eventArgs.Key == Key.Escape && _fullscreen)
			{
				eventArgs.Handled = true; ToggleFullscreen();
			}
			else if (eventArgs.Key == Key.M)
			{
				eventArgs.Handled = true; Mute_Click(sender, new RoutedEventArgs());
			}
			else if (eventArgs.Key is Key.Left or Key.Right && _engine?.Player.IsSeekable == true && Keyboard.FocusedElement is not Slider)
			{
				eventArgs.Handled = true; _engine.Player.Time = Math.Max(0, _engine.Player.Time + (eventArgs.Key == Key.Right ? 5000 : -5000));
			}
		}
		private void Window_DragOver(object sender, DragEventArgs eventArgs)
		{
			eventArgs.Effects = eventArgs.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; eventArgs.Handled = true;
		}
		private void Window_Drop(object sender, DragEventArgs eventArgs)
		{
			if (eventArgs.Data.GetData(DataFormats.FileDrop) is string[] paths)
			{
				TryAction(() => AddSources(paths, play: true));
			}
		}
		private void TryAction(Action action)
		{
			try
			{
				action();
			}
			catch (Exception exception)
			{
				StatusText.Text = exception.Message;
			}
		}
		private void Window_Closing(object? sender, CancelEventArgs eventArgs)
		{
			_closed = true;
			_timer.Stop();
			VideoSurface.MediaPlayer = null;
			if (_engine != null)
			{
				_engine.Player.Playing -= Playback_Started;
				_engine.Ended -= Playback_Ended; _engine.Failed -= Playback_Failed; _engine.Dispose(); _engine = null;
			}
		}
	}
}