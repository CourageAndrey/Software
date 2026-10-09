using LibVLCSharp.Shared;
using VlcPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace Software.MediaPlayer
{
	public sealed class PlaybackEngine : IDisposable
	{
		private readonly LibVLC _vlc;
		private Media? _media;
		private bool _disposed;
		public VlcPlayer Player { get; }
		public event EventHandler? Ended;
		public event EventHandler? Failed;

		public PlaybackEngine(bool testOutput = false)
		{
			// LibVLCSharp concatenates AppContext.BaseDirectory without a separator, which breaks when the host omits the trailing slash.
			string architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
			LibVLCSharp.Shared.Core.Initialize(System.IO.Path.Combine(AppContext.BaseDirectory, "libvlc", $"win-{architecture}"));
			_vlc = testOutput ? new LibVLC("--quiet", "--no-video-title-show", "--aout=dummy", "--vout=dummy") : new LibVLC("--quiet", "--no-video-title-show");
			Player = new VlcPlayer(_vlc) { Volume = 75 };
			Player.EnableKeyInput = false;
			Player.EnableMouseInput = false;
			Player.EndReached += OnEnded;
			Player.EncounteredError += OnFailed;
		}

		public bool Play(string location)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);
			Player.Stop();
			_media?.Dispose();
			bool network = Uri.TryCreate(location, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "rtsp";
			_media = new Media(_vlc, location, network ? FromType.FromLocation : FromType.FromPath);
			return Player.Play(_media);
		}

		private void OnEnded(object? sender, EventArgs eventArgs) => Ended?.Invoke(this, eventArgs);
		private void OnFailed(object? sender, EventArgs eventArgs) => Failed?.Invoke(this, eventArgs);
		public string Metadata(MetadataType type) => _media?.Meta(type) ?? "";

		public void Dispose()
		{
			if (_disposed)
			{
				return;
			}
			_disposed = true;
			Player.EndReached -= OnEnded;
			Player.EncounteredError -= OnFailed;
			Player.Stop();
			Player.Dispose();
			_media?.Dispose();
			_vlc.Dispose();
		}
	}
}