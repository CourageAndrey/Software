using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace Software.FileCommander
{
	/// <summary>Shows the Windows Explorer context menu for items of one folder.</summary>
	internal static class ShellContextMenu
	{
		private const uint FirstCommand = 1;
		private const uint LastCommand = 0x7FFF;
		private const uint CMF_NORMAL = 0x0;
		private const uint CMF_EXPLORE = 0x4;
		private const uint CMF_CANRENAME = 0x10;
		private const uint CMF_EXTENDEDVERBS = 0x100;
		private const uint CMIC_MASK_UNICODE = 0x4000;
		private const uint CMIC_MASK_SHIFT_DOWN = 0x10000000;
		private const uint CMIC_MASK_PTINVOKE = 0x20000000;
		private const uint CMIC_MASK_CONTROL_DOWN = 0x40000000;
		private const uint GCS_VERBW = 0x4;
		private const uint TPM_RIGHTBUTTON = 0x2;
		private const uint TPM_RETURNCMD = 0x100;
		private const int SW_SHOWNORMAL = 1;
		private const int WM_DRAWITEM = 0x2B;
		private const int WM_MEASUREITEM = 0x2C;
		private const int WM_INITMENUPOPUP = 0x117;
		private const int WM_MENUCHAR = 0x120;
		private const int ERROR_CANCELLED_HRESULT = unchecked((int)0x800704C7);

		/// <summary>
		/// Shows the menu of items at a screen point in device pixels and runs the chosen command.
		/// A verb accepted by <paramref name="interceptVerb"/> is not passed to the shell.
		/// Returns the chosen verb, an empty string for commands without one, or null when the menu was dismissed.
		/// </summary>
		public static string? Show(Window owner, IReadOnlyList<string> paths, Point screenPoint, Predicate<string> interceptVerb)
		{
			IntPtr window = new WindowInteropHelper(owner).Handle;
			var pidls = new List<IntPtr>();
			IShellFolder? parent = null;
			IContextMenu? contextMenu = null;
			try
			{
				foreach (string path in paths)
				{
					pidls.Add(ParseDisplayName(path));
				}

				parent = BindToParent(pidls[0], out _);
				IntPtr[] children = pidls.Select(ILFindLastID).ToArray();
				Guid contextMenuId = typeof(IContextMenu).GUID;
				Marshal.ThrowExceptionForHR(parent.GetUIObjectOf(window, (uint)children.Length, children, ref contextMenuId, IntPtr.Zero, out object menuObject));
				contextMenu = (IContextMenu)menuObject;
				return Track(window, contextMenu, Path.GetDirectoryName(paths[0]), screenPoint, interceptVerb);
			}
			finally
			{
				Release(contextMenu);
				Release(parent);
				pidls.ForEach(Marshal.FreeCoTaskMem);
			}
		}

		/// <summary>Shows the background menu of a folder, with commands such as New, Paste and Properties, like <see cref="Show"/>.</summary>
		public static string? ShowBackground(Window owner, string folder, Point screenPoint, Predicate<string> interceptVerb)
		{
			IntPtr window = new WindowInteropHelper(owner).Handle;
			IntPtr pidl = IntPtr.Zero;
			IShellFolder? parent = null;
			IShellFolder? shellFolder = null;
			IContextMenu? contextMenu = null;
			try
			{
				pidl = ParseDisplayName(folder);
				parent = BindToParent(pidl, out IntPtr child);
				Guid shellFolderId = typeof(IShellFolder).GUID;
				Marshal.ThrowExceptionForHR(parent.BindToObject(child, IntPtr.Zero, ref shellFolderId, out object folderObject));
				shellFolder = (IShellFolder)folderObject;
				Guid contextMenuId = typeof(IContextMenu).GUID;
				Marshal.ThrowExceptionForHR(shellFolder.CreateViewObject(window, ref contextMenuId, out object menuObject));
				contextMenu = (IContextMenu)menuObject;
				return Track(window, contextMenu, folder, screenPoint, interceptVerb);
			}
			finally
			{
				Release(contextMenu);
				Release(shellFolder);
				Release(parent);
				Marshal.FreeCoTaskMem(pidl);
			}
		}

		private static string? Track(IntPtr window, IContextMenu contextMenu, string? directory, Point screenPoint, Predicate<string> interceptVerb)
		{
			IntPtr menu = CreatePopupMenu();
			HwndSource? source = null;
			HwndSourceHook? hook = null;
			try
			{
				uint flags = CMF_NORMAL | CMF_EXPLORE | CMF_CANRENAME | (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? CMF_EXTENDEDVERBS : 0);
				Marshal.ThrowExceptionForHR(contextMenu.QueryContextMenu(menu, 0, FirstCommand, LastCommand, flags));

				// Submenus such as "Send to", "Open with" and "New" are filled and drawn by the handlers on demand.
				var menu2 = contextMenu as IContextMenu2;
				var menu3 = contextMenu as IContextMenu3;
				hook = (IntPtr _, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
				{
					if (message is WM_INITMENUPOPUP or WM_DRAWITEM or WM_MEASUREITEM or WM_MENUCHAR)
					{
						if (menu3 != null && menu3.HandleMenuMsg2((uint)message, wParam, lParam, out IntPtr result) >= 0)
						{
							handled = true;
							return result;
						}

						if (menu2 != null && menu2.HandleMenuMsg((uint)message, wParam, lParam) >= 0)
						{
							handled = true;
						}
					}
					return IntPtr.Zero;
				};
				source = HwndSource.FromHwnd(window);
				source.AddHook(hook);

				var point = new POINT { X = (int)screenPoint.X, Y = (int)screenPoint.Y };
				uint command = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, point.X, point.Y, window, IntPtr.Zero);
				if (command < FirstCommand)
				{
					return null;
				}

				uint offset = command - FirstCommand;
				string verb = GetVerb(contextMenu, offset) ?? "";
				if (verb.Length > 0 && interceptVerb(verb))
				{
					return verb;
				}

				var info = new CMINVOKECOMMANDINFOEX
				{
					cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFOEX>(),
					fMask = CMIC_MASK_UNICODE | CMIC_MASK_PTINVOKE
						| (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? CMIC_MASK_SHIFT_DOWN : 0)
						| (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? CMIC_MASK_CONTROL_DOWN : 0),
					hwnd = window,
					lpVerb = (IntPtr)offset,
					lpVerbW = (IntPtr)offset,
					lpDirectory = directory,
					lpDirectoryW = directory,
					nShow = SW_SHOWNORMAL,
					ptInvoke = point
				};
				int invokeResult = contextMenu.InvokeCommand(ref info);
				if (invokeResult < 0 && invokeResult != ERROR_CANCELLED_HRESULT)
				{
					Marshal.ThrowExceptionForHR(invokeResult);
				}
				return verb;
			}
			finally
			{
				if (hook != null)
				{
					source?.RemoveHook(hook);
				}
				DestroyMenu(menu);
			}
		}

		private static IntPtr ParseDisplayName(string path)
		{
			Marshal.ThrowExceptionForHR(SHParseDisplayName(path, IntPtr.Zero, out IntPtr pidl, 0, out _));
			return pidl;
		}

		private static IShellFolder BindToParent(IntPtr pidl, out IntPtr child)
		{
			Guid shellFolderId = typeof(IShellFolder).GUID;
			Marshal.ThrowExceptionForHR(SHBindToParent(pidl, ref shellFolderId, out IShellFolder parent, out child));
			return parent;
		}

		private static void Release(object? comObject)
		{
			if (comObject != null)
			{
				Marshal.ReleaseComObject(comObject);
			}
		}

		private static string? GetVerb(IContextMenu contextMenu, uint offset)
		{
			const int length = 256;
			IntPtr buffer = Marshal.AllocCoTaskMem(length * sizeof(char));
			try
			{
				Marshal.WriteInt16(buffer, 0);
				return contextMenu.GetCommandString((UIntPtr)offset, GCS_VERBW, IntPtr.Zero, buffer, length) >= 0
					? Marshal.PtrToStringUni(buffer) : null;
			}
			finally
			{
				Marshal.FreeCoTaskMem(buffer);
			}
		}

		[StructLayout(LayoutKind.Sequential)]
		private struct POINT
		{
			public int X;
			public int Y;
		}

		[StructLayout(LayoutKind.Sequential)]
		private struct CMINVOKECOMMANDINFOEX
		{
			public int cbSize;
			public uint fMask;
			public IntPtr hwnd;
			public IntPtr lpVerb;
			[MarshalAs(UnmanagedType.LPStr)] public string? lpParameters;
			[MarshalAs(UnmanagedType.LPStr)] public string? lpDirectory;
			public int nShow;
			public uint dwHotKey;
			public IntPtr hIcon;
			[MarshalAs(UnmanagedType.LPStr)] public string? lpTitle;
			public IntPtr lpVerbW;
			[MarshalAs(UnmanagedType.LPWStr)] public string? lpParametersW;
			[MarshalAs(UnmanagedType.LPWStr)] public string? lpDirectoryW;
			[MarshalAs(UnmanagedType.LPWStr)] public string? lpTitleW;
			public POINT ptInvoke;
		}

		[ComImport, Guid("000214E6-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
		private interface IShellFolder
		{
			[PreserveSig] int ParseDisplayName(IntPtr hwnd, IntPtr pbc, [MarshalAs(UnmanagedType.LPWStr)] string pszDisplayName, IntPtr pchEaten, out IntPtr ppidl, IntPtr pdwAttributes);
			[PreserveSig] int EnumObjects(IntPtr hwnd, uint grfFlags, out IntPtr ppenumIDList);
			[PreserveSig] int BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
			[PreserveSig] int BindToStorage(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
			[PreserveSig] int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
			[PreserveSig] int CreateViewObject(IntPtr hwndOwner, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
			[PreserveSig] int GetAttributesOf(uint cidl, IntPtr apidl, ref uint rgfInOut);
			[PreserveSig] int GetUIObjectOf(IntPtr hwndOwner, uint cidl, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, ref Guid riid, IntPtr rgfReserved, [MarshalAs(UnmanagedType.Interface)] out object ppv);
			[PreserveSig] int GetDisplayNameOf(IntPtr pidl, uint uFlags, IntPtr pName);
			[PreserveSig] int SetNameOf(IntPtr hwnd, IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string pszName, uint uFlags, out IntPtr ppidlOut);
		}

		[ComImport, Guid("000214E4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
		private interface IContextMenu
		{
			[PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
			[PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);
			[PreserveSig] int GetCommandString(UIntPtr idCmd, uint uType, IntPtr pReserved, IntPtr pszName, uint cchMax);
		}

		[ComImport, Guid("000214F4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
		private interface IContextMenu2
		{
			[PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
			[PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);
			[PreserveSig] int GetCommandString(UIntPtr idCmd, uint uType, IntPtr pReserved, IntPtr pszName, uint cchMax);
			[PreserveSig] int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);
		}

		[ComImport, Guid("BCFCE0A0-EC17-11D0-8D10-00A0C90F2719"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
		private interface IContextMenu3
		{
			[PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
			[PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);
			[PreserveSig] int GetCommandString(UIntPtr idCmd, uint uType, IntPtr pReserved, IntPtr pszName, uint cchMax);
			[PreserveSig] int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);
			[PreserveSig] int HandleMenuMsg2(uint uMsg, IntPtr wParam, IntPtr lParam, out IntPtr plResult);
		}

		[DllImport("shell32.dll", CharSet = CharSet.Unicode)]
		private static extern int SHParseDisplayName(string pszName, IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);

		[DllImport("shell32.dll")]
		private static extern int SHBindToParent(IntPtr pidl, ref Guid riid, out IShellFolder ppv, out IntPtr ppidlLast);

		[DllImport("shell32.dll")]
		private static extern IntPtr ILFindLastID(IntPtr pidl);

		[DllImport("user32.dll")]
		private static extern IntPtr CreatePopupMenu();

		[DllImport("user32.dll")]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static extern bool DestroyMenu(IntPtr hMenu);

		[DllImport("user32.dll")]
		private static extern uint TrackPopupMenuEx(IntPtr hmenu, uint fuFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);
	}
}
