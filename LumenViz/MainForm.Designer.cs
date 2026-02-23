namespace LumenViz;

partial class MainForm
{
    /// <summary>
    ///  Required designer variable.
    /// </summary>
    private System.ComponentModel.IContainer components = null;

    /// <summary>
    ///  Clean up any resources being used.
    /// </summary>
    /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
    protected override void Dispose(bool disposing)
    {
        if (disposing && (components != null))
        {
            components.Dispose();
        }
        base.Dispose(disposing);
    }

    #region Windows Form Designer generated code

    /// <summary>
    ///  Required method for Designer support - do not modify
    ///  the contents of this method with the code editor.
    /// </summary>
    private void InitializeComponent()
    {
        this.components = new System.ComponentModel.Container();

        // Menu strip
        _menuStrip = new MenuStrip();

        // File menu
        _fileMenu = new ToolStripMenuItem("&File");
        _fileExitItem = new ToolStripMenuItem("E&xit", null, ExitMenuItem_Click);
        _fileExitItem.ShortcutKeys = Keys.Alt | Keys.F4;
        _fileMenu.DropDownItems.Add(_fileExitItem);

        // Edit menu
        _editMenu = new ToolStripMenuItem("&Edit");

        // Window menu
        _windowMenu = new ToolStripMenuItem("&Window");

        _menuStrip.Items.AddRange(new ToolStripItem[] { _fileMenu, _editMenu, _windowMenu });

        // --- Hello-world controls ---

        _helloLabel = new Label
        {
            Text = "Hello from Lumen Viz!",
            Font = new Font("Segoe UI", 18f, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(20, 50),
            Visible = false,
        };

        _inputTextBox = new TextBox
        {
            Text = "",
            Font = new Font("Segoe UI", 12f),
            Location = new Point(20, 110),
            Size = new Size(500, 30),
            Visible = false,
        };

        // --- Graph visualization ---
        _graphView = new GraphView
        {
            Dock = DockStyle.Fill,
        };

        // --- Status bar ---
        _statusBar = new StatusStrip();
        _statusLabel = new ToolStripStatusLabel("Ready");
        _statusBar.Items.Add(_statusLabel);

        // Form
        this.AutoScaleMode = AutoScaleMode.Font;
        this.ClientSize = new Size(1280, 800);
        this.Text = "Lumen Viz  ◇  Graph Visualization";
        this.MainMenuStrip = _menuStrip;
        this.Controls.Add(_graphView);    // fill — added first so it's behind
        this.Controls.Add(_menuStrip);
        this.Controls.Add(_statusBar);
        this.Controls.Add(_helloLabel);
        this.Controls.Add(_inputTextBox);
    }

    #endregion

    private MenuStrip _menuStrip = null!;
    private ToolStripMenuItem _fileMenu = null!;
    private ToolStripMenuItem _fileExitItem = null!;
    private ToolStripMenuItem _editMenu = null!;
    private ToolStripMenuItem _windowMenu = null!;
    private Label _helloLabel = null!;
    private TextBox _inputTextBox = null!;
    private GraphView _graphView = null!;
    private StatusStrip _statusBar = null!;
    private ToolStripStatusLabel _statusLabel = null!;
}
