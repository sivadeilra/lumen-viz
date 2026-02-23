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
    public GraphView GraphView => _graphView;

    public void SetStatus(string text)
    {
        _statusLabel.Text = text;
    }

    private void ExitMenuItem_Click(object? sender, EventArgs e)
    {
        Close();
    }
}
