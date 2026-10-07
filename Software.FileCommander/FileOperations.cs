using System.ComponentModel;
using System.IO;
using System.Windows.Media;
using Microsoft.VisualBasic.FileIO;

namespace Software.FileCommander
{
	public sealed class FileEntry(string fullPath, string name, bool isDirectory, long size, DateTime modified) : INotifyPropertyChanged
	{
		private ImageSource? _icon;

		public string FullPath { get; } = fullPath;
		public string Name { get; } = name;
		public bool IsDirectory { get; } = isDirectory;
		public long Size { get; } = size;
		public DateTime Modified { get; } = modified;
		public event PropertyChangedEventHandler? PropertyChanged;

		public ImageSource? Icon
		{
			get => _icon;
			set
			{
				if (_icon != value)
				{
					_icon = value;
					PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
				}
			}
		}

		public string DisplaySize => IsDirectory ? "<DIR>" : Size switch
		{
			>= 1_073_741_824 => $"{Size / 1_073_741_824d:0.#} GB",
			>= 1_048_576 => $"{Size / 1_048_576d:0.#} MB",
			>= 1024 => $"{Size / 1024d:0.#} KB",
			_ => $"{Size} B"
		};
	}

	public static class FileOperations
	{
		public static FileEntry[] ReadDirectory(string path)
		{
			return new DirectoryInfo(path).EnumerateFileSystemInfos().Select(info =>
			{
				bool directory = (info.Attributes & FileAttributes.Directory) != 0;
				return new FileEntry(info.FullName, info.Name, directory, info is FileInfo file ? file.Length : 0, info.LastWriteTime);
			}).OrderByDescending(entry => entry.IsDirectory).ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase).ToArray();
		}

		public static void Copy(IEnumerable<string> sources, string destination) => Transfer(sources, destination, move: false);

		public static void Move(IEnumerable<string> sources, string destination) => Transfer(sources, destination, move: true);

		private static void Transfer(IEnumerable<string> sources, string destination, bool move)
		{
			string targetDirectory = Path.GetFullPath(destination);
			if (!Directory.Exists(targetDirectory))
			{
				throw new DirectoryNotFoundException("The destination folder does not exist.");
			}

			var plannedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			var plan = sources.Select(source =>
			{
				string fullSource = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
				FileAttributes attributes = File.GetAttributes(fullSource);
				bool directory = (attributes & FileAttributes.Directory) != 0;
				if ((attributes & FileAttributes.ReparsePoint) != 0)
				{
					throw new IOException("Transfers of symbolic links and junctions are not supported.");
				}

				string name = Path.GetFileName(fullSource);
				if (string.IsNullOrEmpty(name))
				{
					throw new IOException("A drive root cannot be transferred.");
				}

				string target = Path.Combine(targetDirectory, name);
				if (fullSource.Equals(target, StringComparison.OrdinalIgnoreCase)
					|| (directory && target.StartsWith(fullSource + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
				{
					throw new IOException("A folder cannot be transferred into itself, and source and destination must differ.");
				}

				EnsureMissing(target);
				if (!plannedTargets.Add(target))
				{
					throw new IOException($"Multiple selected items have the same destination: {name}");
				}

				if (directory)
				{
					EnsureRegularTree(fullSource);
					EnsureRegularAncestors(targetDirectory);
				}
				return (Source: fullSource, Target: target, Directory: directory);
			}).ToArray();

			foreach (var item in plan)
			{
				if (item.Directory)
				{
					if (move)
					{
						FileSystem.MoveDirectory(item.Source, item.Target, overwrite: false);
					}
					else
					{
						FileSystem.CopyDirectory(item.Source, item.Target, overwrite: false);
					}
				}
				else if (move)
				{
					FileSystem.MoveFile(item.Source, item.Target, overwrite: false);
				}
				else
				{
					FileSystem.CopyFile(item.Source, item.Target, overwrite: false);
				}
			}
		}

		public static void Rename(string source, string newName)
		{
			ValidateName(newName);
			string fullSource = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
			string parent = Path.GetDirectoryName(fullSource) ?? throw new IOException("A drive root cannot be renamed.");
			string target = Path.Combine(parent, newName);
			EnsureMissing(target);
			if (Directory.Exists(fullSource))
			{
				Directory.Move(fullSource, target);
			}
			else
			{
				File.Move(fullSource, target);
			}
		}

		public static void CreateFolder(string parent, string name)
		{
			ValidateName(name);
			if (!Directory.Exists(parent))
			{
				throw new DirectoryNotFoundException("The parent folder does not exist.");
			}

			string path = Path.Combine(parent, name);
			EnsureMissing(path);
			Directory.CreateDirectory(path);
		}

		public static void Recycle(IEnumerable<string> sources)
		{
			foreach (string source in sources)
			{
				if (Directory.Exists(source))
				{
					FileSystem.DeleteDirectory(source, UIOption.AllDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);
				}
				else
				{
					FileSystem.DeleteFile(source, UIOption.AllDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);
				}
			}
		}

		private static void ValidateName(string name)
		{
			if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
				|| name.EndsWith('.') || name.EndsWith(' '))
			{
				throw new ArgumentException("Enter a valid file or folder name, without a path or trailing spaces or dots.");
			}
		}

		private static void EnsureMissing(string path)
		{
			if (File.Exists(path) || Directory.Exists(path))
			{
				throw new IOException($"An item already exists: {path}. Existing items are never overwritten.");
			}
		}

		private static void EnsureRegularTree(string directory)
		{
			foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
			{
				FileAttributes attributes = File.GetAttributes(entry);
				if ((attributes & FileAttributes.ReparsePoint) != 0)
				{
					throw new IOException("Folders containing symbolic links or junctions cannot be transferred.");
				}

				if ((attributes & FileAttributes.Directory) != 0)
				{
					EnsureRegularTree(entry);
				}
			}
		}

		private static void EnsureRegularAncestors(string path)
		{
			for (DirectoryInfo? directory = new(path); directory != null; directory = directory.Parent)
			{
				if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
				{
					throw new IOException("Folder transfers through symbolic links or junctions are not supported.");
				}
			}
		}
	}
}