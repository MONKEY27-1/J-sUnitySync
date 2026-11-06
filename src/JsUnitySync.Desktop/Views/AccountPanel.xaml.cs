using System.Windows;
using System.Windows.Controls;
using JsUnitySync.Desktop.Core.Accounts;

namespace JsUnitySync.Desktop.Views;

public partial class AccountPanel : UserControl
{
    public AccountPanel() => InitializeComponent();
    // The PasswordBox boundary deliberately keeps the password out of bindings and persisted view state.
    private async void SignInClick(object sender, RoutedEventArgs e) => await Authenticate(false);
    private async void RegisterClick(object sender, RoutedEventArgs e) => await Authenticate(true);
    private async Task Authenticate(bool register)
    {
        if (DataContext is not AccountViewModel account) return;
        var password = PasswordInput.Password;
        PasswordInput.Clear();
        try { await account.AuthenticateAsync(password, register); }
        catch (Exception) { MessageBox.Show("The account operation failed. Refresh before retrying.", "J'sUnitySync"); }
    }
}
