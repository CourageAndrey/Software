using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Software.FileCommander
{
	/// <summary>Loads Windows shell icons, including per-file icons and overlays, on background STA threads.</summary>
	internal static class ShellIcons
	{
		private const uint SHGFI_ICON = 0x100;
		private const uint SHGFI_SMALLICON = 0x1;
		private const uint SHGFI_USEFILEATTRIBUTES = 0x10;
		private const uint SHGFI_ADDOVERLAYS = 0x20;
		private const uint SHGFI_OVERLAYINDEX = 0x40;
		private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
		private const uint FILE_ATTRIBUTE_NORMAL = 0x80;

		private static readonly ConcurrentDictionary<string, ImageSource?> _typeIcons = new(StringComparer.OrdinalIgnoreCase);
		private static readonly ConcurrentDictionary<int, ImageSource?> _systemIcons = new();

		/// <summary>
		/// Assigns generic type icons first, then each item's own icon with overlays,
		/// which may require reading the file or calling shell extensions.
		/// </summary>
		public static void Load(IReadOnlyList<FileEntry> entries, CancellationToken token)
		{
			var thread = new Thread(() =>
			{
				foreach (var entry in entries)
				{
					if (token.IsCancellationRequested)
					{
						return;
					}

					entry.Icon ??= GetTypeIcon(entry);
				}

				foreach (var entry in entries)
				{
					if (token.IsCancellationRequested)
					{
						return;
					}

					entry.Icon = GetItemIcon(entry.FullPath) ?? entry.Icon;
				}
			}) { IsBackground = true, Name = "Shell icon loader" };
			thread.SetApartmentState(ApartmentState.STA);
			thread.Start();
		}

		private static ImageSource? GetTypeIcon(FileEntry entry)
		{
			string key = entry.IsDirectory ? "\\" : Path.GetExtension(entry.Name);
			return _typeIcons.GetOrAdd(key, _ =>
			{
				string name = entry.IsDirectory ? "folder" : "file" + key;
				uint attributes = entry.IsDirectory ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL;
				var info = new SHFILEINFO();
				if (SHGetFileInfo(name, attributes, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(),
					SHGFI_ICON | SHGFI_SMALLICON | SHGFI_USEFILEATTRIBUTES) == IntPtr.Zero)
				{
					return null;
				}

				return TakeIcon(info.hIcon);
			});
		}

		private static ImageSource? GetItemIcon(string path)
		{
			var info = new SHFILEINFO();
			if (SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(),
				SHGFI_ICON | SHGFI_SMALLICON | SHGFI_ADDOVERLAYS | SHGFI_OVERLAYINDEX) == IntPtr.Zero)
			{
				return null;
			}

			// iIcon holds the system image list index with the overlay index in its upper eight bits,
			// so items sharing both share one bitmap.
			if (_systemIcons.TryGetValue(info.iIcon, out var cached))
			{
				DestroyIcon(info.hIcon);
				return cached;
			}

			return _systemIcons.GetOrAdd(info.iIcon, TakeIcon(info.hIcon));
		}

		private static ImageSource? TakeIcon(IntPtr icon)
		{
			if (icon == IntPtr.Zero)
			{
				return null;
			}

			try
			{
				var image = Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
				image.Freeze();
				return image;
			}
			finally
			{
				DestroyIcon(icon);
			}
		}

		[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
		private struct SHFILEINFO
		{
			public IntPtr hIcon;
			public int iIcon;
			public uint dwAttributes;
			[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
			public string szDisplayName;
			[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
			public string szTypeName;
		}

		[DllImport("shell32.dll", CharSet = CharSet.Unicode)]
		private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

		[DllImport("user32.dll")]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static extern bool DestroyIcon(IntPtr hIcon);
	}
}
