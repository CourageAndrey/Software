using System.IO;
using System.Windows;

namespace Software.Archiver
{
	public partial class App : Application
	{
		protected override void OnStartup(StartupEventArgs eventArgs)
		{
			base.OnStartup(eventArgs);
			try
			{
				MainWindow = new MainWindow(eventArgs.Args);
				MainWindow.Show();
			}
			catch (Exception exception)
			{
				MessageBox.Show(exception.Message, "Archiver could not start", MessageBoxButton.OK, MessageBoxImage.Error);
				Shutdown(2);
			}
		}
	}

	public sealed record ArchiveLaunchRequest(string Action, string[] Paths)
	{
		public static ArchiveLaunchRequest Parse(string[] arguments)
		{
			if (arguments.Length == 0)
				return new ArchiveLaunchRequest("None", []);
			string action = arguments[0] switch
			{
				"--add" => "Add",
				"--extract" => "Extract",
				"--open" => "Open",
				_ when !arguments[0].StartsWith("--", StringComparison.Ordinal) => "Open",
				_ => throw new ArgumentException("Use --add <paths...>, --extract <archive>, or --open <archive>.")
			};
			string[] paths = arguments.Skip(arguments[0].StartsWith("--", StringComparison.Ordinal) ? 1 : 0)
				.Select(Path.GetFullPath).ToArray();
			if (paths.Length == 0 || (action != "Add" && paths.Length != 1))
				throw new ArgumentException("Choose at least one input for --add, or exactly one archive to open or extract.");
			return new ArchiveLaunchRequest(action, paths);
		}
	}
}
