using System.Security;
using System.Windows;

namespace Software.PasswordManager
{
	public partial class MasterPasswordDialog : Window
	{
		private readonly bool _creating;
		public SecureString? Result { get; private set; }

		public MasterPasswordDialog(bool creating)
		{
			_creating = creating;
			InitializeComponent();
			ConfirmationArea.Visibility = creating ? Visibility.Visible : Visibility.Collapsed;
			AcceptButton.Content = creating ? "Confirm" : "Unlock";
			Loaded += (_, _) => MasterPassword.Focus();
		}

		private void Accept_Click(object sender, RoutedEventArgs eventArgs)
		{
			using var password = MasterPassword.SecurePassword;
			if (password.Length == 0 || (_creating && password.Length < 12))
			{
				ErrorText.Text = _creating ? "Use at least 12 characters." : "Enter the master password.";
				return;
			}
			if (_creating)
			{
				using var confirmation = Confirmation.SecurePassword;
				if (!VaultSession.PasswordsMatch(password, confirmation))
				{
					ErrorText.Text = "Passwords do not match.";
					return;
				}
			}
			Result = password.Copy();
			Result.MakeReadOnly();
			MasterPassword.Clear();
			Confirmation.Clear();
			DialogResult = true;
		}

		private void Window_Closed(object? sender, EventArgs eventArgs)
		{
			MasterPassword.Clear();
			Confirmation.Clear();
		}
	}
}