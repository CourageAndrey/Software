using System.IO;

namespace Software.FileCommander
{
	/// <summary>
	/// Keeps the open folder tabs between runs in a plain text file: a "[Left]" or "[Right]" line starts each pane,
	/// followed by one folder per line, with the active tab prefixed by "*".
	/// </summary>
	public static class TabSession
	{
		public sealed record Side(string[] Folders, int ActiveIndex);

		private const string LeftSection = "[Left]";
		private const string RightSection = "[Right]";
		private const char ActiveMarker = '*';

		/// <summary>Reads the saved tabs, dropping folders that no longer exist. A missing or unreadable file yields no tabs.</summary>
		public static (Side? Left, Side? Right) Load(string path)
		{
			string[] lines;
			try
			{
				lines = File.ReadAllLines(path);
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				return (null, null);
			}

			var sides = new Dictionary<string, (List<string> Folders, int ActiveIndex)>(StringComparer.OrdinalIgnoreCase);
			string? section = null;
			foreach (string rawLine in lines)
			{
				string line = rawLine.Trim();
				if (line is LeftSection or RightSection)
				{
					section = line;
					sides[section] = ([], 0);
					continue;
				}

				bool active = line.StartsWith(ActiveMarker);
				string folder = active ? line[1..].Trim() : line;
				if (section == null || folder.Length == 0 || !Directory.Exists(folder))
				{
					continue;
				}

				var side = sides[section];
				if (active)
				{
					side.ActiveIndex = side.Folders.Count;
				}
				side.Folders.Add(folder);
				sides[section] = side;
			}

			return (ToSide(LeftSection), ToSide(RightSection));

			Side? ToSide(string name) => sides.TryGetValue(name, out var side) ? new Side(side.Folders.ToArray(), side.ActiveIndex) : null;
		}

		/// <summary>Writes the tabs, skipping tabs without a folder. Failures are ignored, since losing the tab list must not block closing the app.</summary>
		public static void Save(string path, Side left, Side right)
		{
			var lines = new List<string>();
			Append(LeftSection, left);
			Append(RightSection, right);
			try
			{
				File.WriteAllLines(path, lines);
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
			}

			void Append(string section, Side side)
			{
				lines.Add(section);
				for (int index = 0; index < side.Folders.Length; index++)
				{
					if (side.Folders[index].Length > 0)
					{
						lines.Add(index == side.ActiveIndex ? ActiveMarker + side.Folders[index] : side.Folders[index]);
					}
				}
			}
		}
	}
}
