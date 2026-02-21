using System.Windows;

namespace ShaderExplorer.App.Views;

public partial class RenameDialog : Window
{
    public RenameDialog(string originalName, string currentName)
    {
        InitializeComponent();
        OriginalName = originalName;
        OriginalNameRun.Text = originalName;
        NewNameTextBox.Text = currentName;
        NewNameTextBox.SelectAll();
        NewNameTextBox.Focus();
    }

    public string OriginalName { get; }
    public string NewName { get; private set; } = string.Empty;

    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        var name = NewNameTextBox.Text.Trim();
        if (string.IsNullOrEmpty(name)) return;

        NewName = name;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}