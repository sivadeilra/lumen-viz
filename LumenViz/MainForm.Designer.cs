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

        // Form
        this.AutoScaleMode = AutoScaleMode.Font;
        this.ClientSize = new Size(1280, 800);
        this.Text = "Lumen Viz";
        this.MainMenuStrip = _menuStrip;
        this.Controls.Add(_menuStrip);
    }

    #endregion

    private MenuStrip _menuStrip = null!;
    private ToolStripMenuItem _fileMenu = null!;
    private ToolStripMenuItem _fileExitItem = null!;
    private ToolStripMenuItem _editMenu = null!;
    private ToolStripMenuItem _windowMenu = null!;
}
