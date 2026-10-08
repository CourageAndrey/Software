using System.Windows;

namespace Software.MediaPlayer
{
	public partial class App : Application
	{
		protected override void OnStartup(StartupEventArgs eventArgs)
		{
			base.OnStartup(eventArgs);
			MainWindow = new MainWindow(eventArgs.Args);
			MainWindow.Show();
		}
	}

}
