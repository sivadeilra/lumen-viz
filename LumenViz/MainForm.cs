namespace LumenViz;

public partial class MainForm : Form
{
    public MainForm()
    {
        InitializeComponent();
    }

    // Public accessors for MCP tools
    public Label HelloLabel => _helloLabel;
    public TextBox InputTextBox => _inputTextBox;

    private void ExitMenuItem_Click(object? sender, EventArgs e)
    {
        Close();
    }
}
