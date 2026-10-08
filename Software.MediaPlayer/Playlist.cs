using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Software.MediaPlayer
{
	public enum RepeatMode { None, All, One }
	public sealed record PlaylistItem(Guid Id, string Location, string Title);

	public sealed class Playlist
	{
		private readonly HashSet<Guid> _played = [];
		public ObservableCollection<PlaylistItem> Items { get; } = [];
		public int CurrentIndex { get; private set; } = -1;
		public PlaylistItem? Current => CurrentIndex >= 0 && CurrentIndex < Items.Count ? Items[CurrentIndex] : null;
		public bool Shuffle { get; set; }
		public RepeatMode Repeat { get; set; }

		public void Add(IEnumerable<string> sources)
		{
			var planned = sources.Select(CreateItem).ToArray();
			if (planned.Length + Items.Count > 2000)
			{
				throw new InvalidOperationException("A playlist may contain at most 2,000 items.");
			}
			foreach (var item in planned)
			{
				Items.Add(item);
			}
		}

		private static PlaylistItem CreateItem(string source)
		{
			if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "rtsp")
			{
				return new PlaylistItem(Guid.NewGuid(), uri.AbsoluteUri, uri.GetLeftPart(UriPartial.Path));
			}
			string path = Path.GetFullPath(source);
			if (!File.Exists(path))
			{
				throw new FileNotFoundException("Media file was not found.", path);
			}
			return new PlaylistItem(Guid.NewGuid(), path, Path.GetFileName(path));
		}

		public PlaylistItem Select(int index, bool resetCycle = true)
		{
			if (index < 0 || index >= Items.Count)
			{
				throw new ArgumentOutOfRangeException(nameof(index));
			}
			if (resetCycle)
			{
				_played.Clear();
			}
			CurrentIndex = index;
			_played.Add(Items[index].Id);
			return Items[index];
		}

		public PlaylistItem? Next(bool automatic = false)
		{
			if (Items.Count == 0)
			{
				return null;
			}
			if (automatic && Repeat == RepeatMode.One && Current != null)
			{
				return Current;
			}
			if (Shuffle && Items.Count > 1)
			{
				if (automatic)
				{
					var remaining = Enumerable.Range(0, Items.Count).Where(index => !_played.Contains(Items[index].Id)).ToArray();
					if (remaining.Length == 0)
					{
						if (Repeat == RepeatMode.None)
						{
							return null;
						}
						_played.Clear();
						if (Current != null)
						{
							_played.Add(Current.Id);
						}
						remaining = Enumerable.Range(0, Items.Count).Where(index => !_played.Contains(Items[index].Id)).ToArray();
					}
					return Select(remaining[RandomNumberGenerator.GetInt32(remaining.Length)], resetCycle: false);
				}
				int index = RandomNumberGenerator.GetInt32(Items.Count - 1);
				if (index >= CurrentIndex && CurrentIndex >= 0)
				{
					index++;
				}
				return Select(index);
			}
			int next = CurrentIndex + 1;
			if (next >= Items.Count)
			{
				if (automatic && Repeat == RepeatMode.None)
				{
					return null;
				}
				next = 0;
			}
			return Select(next, resetCycle: !automatic);
		}

		public PlaylistItem? Previous()
		{
			if (Items.Count == 0)
			{
				return null;
			}
			return Select(CurrentIndex <= 0 ? Items.Count - 1 : CurrentIndex - 1);
		}

		public void Remove(Guid id)
		{
			int index = Items.ToList().FindIndex(item => item.Id == id);
			if (index < 0)
			{
				return;
			}
			Items.RemoveAt(index);
			_played.Remove(id);
			if (index == CurrentIndex)
			{
				CurrentIndex = -1;
			}
			else if (index < CurrentIndex)
			{
				CurrentIndex--;
			}
		}

		public void Clear()
		{
			Items.Clear();
			_played.Clear();
			CurrentIndex = -1;
		}

		public void Save(string path)
		{
			string output = Path.GetFullPath(path);
			string temporary = Path.Combine(Path.GetDirectoryName(output)!, $".Software.MediaPlayer-{Guid.NewGuid():N}.tmp");
			try
			{
				File.WriteAllLines(temporary, new[] { "#EXTM3U" }.Concat(Items.Select(item => item.Location)), new UTF8Encoding(false));
				File.Move(temporary, output, overwrite: true);
			}
			finally
			{
				if (File.Exists(temporary))
				{
					File.Delete(temporary);
				}
			}
		}

		public void Load(string path)
		{
			if (new FileInfo(path).Length > 2 * 1024 * 1024)
			{
				throw new InvalidDataException("Playlist files must be at most 2 MiB.");
			}
			string parent = Path.GetDirectoryName(Path.GetFullPath(path))!;
			var sources = File.ReadLines(path).Select(line => line.Trim()).Where(line => line.Length > 0 && !line.StartsWith('#'))
				.Select(line => Uri.TryCreate(line, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "rtsp" ? line : Path.GetFullPath(line, parent));
			Add(sources);
		}
	}
}