using System.Windows;

namespace Em68030.Views;

public partial class InputDialog : Window
{
    public string InputText => InputTextBox.Text;

    public InputDialog(string title, string prompt, string defaultValue = "")
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        InputTextBox.Text = defaultValue;
        InputTextBox.SelectAll();
        InputTextBox.Focus();
    }

    private void OK_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
