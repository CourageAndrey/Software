using System.Windows;

namespace Software.Notepad
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
				MessageBox.Show(exception.Message, "Notepad could not start", MessageBoxButton.OK, MessageBoxImage.Error);
				Shutdown(2);
			}
		}
	}

}
