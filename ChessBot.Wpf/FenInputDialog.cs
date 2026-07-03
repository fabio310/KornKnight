namespace ChessBot.Wpf;

using System.Windows;

/// <summary>
/// Simple dialog for FEN string input.
/// </summary>
public partial class FenInputDialog : Window
{
    public string FenText { get; private set; } = "";

    public FenInputDialog()
    {
        Title = "Load FEN Position";
        Width = 520;
        Height = 180;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = System.Windows.Media.Brushes.White;

        var grid = new System.Windows.Controls.Grid();
        grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
        grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
        grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
        grid.Margin = new Thickness(12);

        var label = new System.Windows.Controls.TextBlock
        {
            Text = "Enter FEN string:",
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 6)
        };
        grid.Children.Add(label);
        System.Windows.Controls.Grid.SetRow(label, 0);

        var textBox = new System.Windows.Controls.TextBox
        {
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 11,
            Padding = new Thickness(4),
            VerticalContentAlignment = VerticalAlignment.Center,
            Height = 32
        };
        textBox.Text = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";
        textBox.SelectAll();
        grid.Children.Add(textBox);
        System.Windows.Controls.Grid.SetRow(textBox, 1);

        var buttonPanel = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        grid.Children.Add(buttonPanel);
        System.Windows.Controls.Grid.SetRow(buttonPanel, 2);

        var btnOk = new System.Windows.Controls.Button
        {
            Content = "Load",
            Width = 80,
            Height = 28,
            Margin = new Thickness(0, 0, 8, 0),
            IsDefault = true
        };
        btnOk.Click += (_, _) =>
        {
            FenText = textBox.Text;
            DialogResult = true;
            Close();
        };
        buttonPanel.Children.Add(btnOk);

        var btnCancel = new System.Windows.Controls.Button
        {
            Content = "Cancel",
            Width = 80,
            Height = 28,
            IsCancel = true
        };
        btnCancel.Click += (_, _) =>
        {
            DialogResult = false;
            Close();
        };
        buttonPanel.Children.Add(btnCancel);

        Content = grid;
        textBox.Focus();
    }
}
