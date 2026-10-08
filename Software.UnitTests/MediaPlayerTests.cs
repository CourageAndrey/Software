using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using Software.MediaPlayer;

namespace Software.UnitTests
{
	public class MediaPlaylistTests
	{
		private string _root = null!;
		private string[] _paths = null!;
		[SetUp]
		public void Setup()
		{
			_root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"MediaPlaylistTests-{Guid.NewGuid():N}")).FullName;
			_paths = Enumerable.Range(0, 3).Select(index => Path.Combine(_root, $"track{index}.wav")).ToArray();
			foreach (string path in _paths)
			{
				File.WriteAllBytes(path, []);
			}
		}
		[TearDown]
		public void TearDown() => Directory.Delete(_root, recursive: true);

		[Test]
		public void NavigationRespectsRepeatAndShuffle()
		{
			var playlist = new Playlist();
			playlist.Add(_paths);
			playlist.Select(2);
			Assert.That(playlist.Next(automatic: true), Is.Null);
			playlist.Repeat = RepeatMode.All;
			Assert.That(playlist.Next(automatic: true)!.Location, Is.EqualTo(_paths[0]));
			playlist.Repeat = RepeatMode.One;
			Assert.That(playlist.Next(automatic: true)!.Location, Is.EqualTo(_paths[0]));
			playlist.Shuffle = true;
			Assert.That(playlist.Next()!.Location, Is.Not.EqualTo(_paths[0]));
		}

		[Test]
		public void ShuffleWithoutRepeatFinishesAfterEveryItemHasPlayed()
		{
			var playlist = new Playlist { Shuffle = true };
			playlist.Add(_paths);
			var played = new HashSet<Guid> { playlist.Select(0).Id };
			for (int index = 1; index < _paths.Length; index++)
			{
				var next = playlist.Next(automatic: true);
				Assert.That(next, Is.Not.Null);
				Assert.That(played.Add(next!.Id), Is.True);
			}
			Assert.That(playlist.Next(automatic: true), Is.Null);
			playlist.Repeat = RepeatMode.All;
			Assert.That(playlist.Next(automatic: true), Is.Not.Null);
		}

		[Test]
		public void RemovingItemsKeepsCurrentIdentityAndClearingResetsIt()
		{
			var playlist = new Playlist();
			playlist.Add(_paths);
			var current = playlist.Select(2);
			playlist.Remove(playlist.Items[0].Id);
			Assert.That(playlist.Current, Is.SameAs(current));
			playlist.Remove(current.Id);
			Assert.That(playlist.Current, Is.Null);
			playlist.Clear();
			Assert.That(playlist.Items, Is.Empty);
		}

		[Test]
		public void PlaylistRoundTripPreservesLocalAndNetworkLocations()
		{
			var playlist = new Playlist();
			playlist.Add([_paths[0], "https://example.test/audio.mp3"]);
			string path = Path.Combine(_root, "list.m3u8");
			playlist.Save(path);
			var reopened = new Playlist();
			reopened.Load(path);
			Assert.That(reopened.Items.Select(item => item.Location), Is.EqualTo(playlist.Items.Select(item => item.Location)));
		}

		[Test]
		public void InvalidBatchLeavesPlaylistUnchangedAndRelativePlaylistPathsResolve()
		{
			var playlist = new Playlist();
			Assert.Throws<FileNotFoundException>(() => playlist.Add([_paths[0], Path.Combine(_root, "missing.wav")]));
			Assert.That(playlist.Items, Is.Empty);
			string path = Path.Combine(_root, "relative.m3u8");
			File.WriteAllText(path, "#EXTM3U\ntrack0.wav\n");
			playlist.Load(path);
			Assert.That(playlist.Items[0].Location, Is.EqualTo(_paths[0]));
		}
	}

	public class MediaEngineTests
	{
		[Test]
		public async Task LibVlcPlaysGeneratedPcmAudioWithDummyOutput()
		{
			string path = Path.Combine(Path.GetTempPath(), $"MediaEngineTests-{Guid.NewGuid():N}.wav");
			try
			{
				WriteWave(path);
				using var engine = new PlaybackEngine(testOutput: true);
				var playing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
				engine.Player.Playing += (_, _) => playing.TrySetResult();
				Assert.That(engine.Play(path), Is.True);
				await playing.Task.WaitAsync(TimeSpan.FromSeconds(15));
				Assert.That(engine.Player.State, Is.EqualTo(VLCState.Playing));
				var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
				engine.Player.Paused += (_, _) => paused.TrySetResult();
				engine.Player.SetPause(true);
				await paused.Task.WaitAsync(TimeSpan.FromSeconds(15));
				Assert.That(engine.Player.State, Is.EqualTo(VLCState.Paused));
				engine.Player.Stop();
			}
			finally
			{
				File.Delete(path);
			}
		}

		public static void WriteWave(string path)
		{
			const int sampleRate = 8000;
			const int seconds = 5;
			int dataLength = sampleRate * seconds * 2;
			using var stream = File.Create(path);
			using var writer = new BinaryWriter(stream, Encoding.ASCII);
			writer.Write("RIFF"u8); writer.Write(36 + dataLength); writer.Write("WAVE"u8);
			writer.Write("fmt "u8); writer.Write(16); writer.Write((short)1); writer.Write((short)1);
			writer.Write(sampleRate); writer.Write(sampleRate * 2); writer.Write((short)2); writer.Write((short)16);
			writer.Write("data"u8); writer.Write(dataLength);
			for (int index = 0; index < sampleRate * seconds; index++)
			{
				writer.Write((short)(Math.Sin(index * Math.PI * 2 * 220 / sampleRate) * 1000));
			}
		}
	}

	[Apartment(ApartmentState.STA)]
	public class MediaPlayerUiTests
	{
		private MainWindow _window = null!;
		private string _root = null!;
		private string[] _paths = null!;

		[SetUp]
		public void Setup()
		{
			_root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"MediaUiTests-{Guid.NewGuid():N}")).FullName;
			_paths = [Path.Combine(_root, "first.wav"), Path.Combine(_root, "second.wav")];
			foreach (string path in _paths)
			{
				MediaEngineTests.WriteWave(path);
			}
			_window = new MainWindow([], testOutput: true);
			_window.Show();
		}
		[TearDown]
		public void TearDown()
		{
			_window.Close();
			Directory.Delete(_root, recursive: true);
		}

		private void Click(string name) => ((Button)_window.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

		[Test]
		public void UiPlayPauseStopAndNextTrackUseTheNativeEngine()
		{
			RunOnDispatcher(async () =>
			{
				await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
				Assert.That(_window.Engine, Is.Not.Null);
				_window.AddSources(_paths);
				var player = _window.Engine!.Player;
				var playing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
				player.Playing += (_, _) => playing.TrySetResult();
				Click("PlayButton");
				await playing.Task.WaitAsync(TimeSpan.FromSeconds(10));
				Assert.That(_window.Playlist.Current!.Location, Is.EqualTo(_paths[0]));
				var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
				player.Paused += (_, _) => paused.TrySetResult();
				Click("PlayButton");
				await paused.Task.WaitAsync(TimeSpan.FromSeconds(10));
				Assert.That(player.State, Is.EqualTo(VLCState.Paused));
				Click("NextButton");
				Assert.That(_window.Playlist.Current!.Location, Is.EqualTo(_paths[1]));
				Click("StopButton");
				Assert.That(((TextBlock)_window.FindName("StatusText")).Text, Is.EqualTo("Stopped"));
				Assert.That(((Slider)_window.FindName("SeekSlider")).Value, Is.EqualTo(0));
			});
		}

		[Test]
		public void PlaylistItemsAndPlaybackModesAreEditableInTheWindow()
		{
			_window.AddSources(_paths);
			Assert.That(((ListBox)_window.FindName("PlaylistList")).Items.Count, Is.EqualTo(2));
			((CheckBox)_window.FindName("ShuffleBox")).IsChecked = true;
			((ComboBox)_window.FindName("RepeatBox")).SelectedIndex = 2;
			Assert.That(_window.Playlist.Shuffle, Is.True);
			Assert.That(_window.Playlist.Repeat, Is.EqualTo(RepeatMode.One));
		}

		[Test]
		public void FullscreenRestoresOriginalWindowChrome()
		{
			var originalStyle = _window.WindowStyle;
			_window.ToggleFullscreen();
			Assert.That(_window.WindowStyle, Is.EqualTo(WindowStyle.None));
			Assert.That(((Grid)_window.FindName("PlaylistPanel")).Visibility, Is.EqualTo(Visibility.Collapsed));
			_window.ToggleFullscreen();
			Assert.That(_window.WindowStyle, Is.EqualTo(originalStyle));
			Assert.That(((Grid)_window.FindName("PlaylistPanel")).Visibility, Is.EqualTo(Visibility.Visible));
		}

		private void RunOnDispatcher(Func<Task> action)
		{
			Task task = _window.Dispatcher.InvokeAsync(action).Task.Unwrap();
			var frame = new DispatcherFrame();
			var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
			timeout.Tick += (_, _) => frame.Continue = false;
			_ = task.ContinueWith(_ => _window.Dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
			timeout.Start();
			Dispatcher.PushFrame(frame);
			timeout.Stop();
			Assert.That(task.IsCompleted, Is.True, "Media UI did not finish within 20 seconds.");
			task.GetAwaiter().GetResult();
		}
	}
}