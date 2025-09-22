// LeviText.cs
// Single-file WinForms app. Save as Program.cs or LeviText.cs inside a WinForms project.

using System;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LeviText
{
    internal static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new LeviTextEditorForm());
        }
    }

    public class LeviTextEditorForm : Form
    {
        // Controls - made rtb internal so FormatWindow can access it
        internal RichTextBox? rtb;
        private ToolStripMenuItem? levitateMenuItem;
        private FormatWindow? formatWindow;

        // These fields were removed since we now use the floating format window instead of dropdown panel

        // Settings & autosave
        private Settings? cfg;
        private readonly string appFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LeviText");
        private readonly string autosaveFile;
        private readonly string settingsFile;
        private readonly string formatWindowSettingsFile;

        // Public properties
        public bool IsLevitating => cfg?.TopMost ?? false;
        
        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool SuppressInitialAutosaveDialog { get; set; } = false;
        
        private System.Windows.Forms.Timer autosaveTimer;
        private System.Windows.Forms.Timer debounceFormatTimer;

        // Colors & theme
        private readonly Color windowBack = Color.FromArgb(30, 30, 30);
        private readonly Color rtbBack = Color.FromArgb(20, 20, 20);
        private readonly Color windowFore = Color.FromArgb(230, 230, 230);
        private readonly Color adjustedReadableColor = Color.FromArgb(200, 200, 200);

        // Auto-adjust session flag
        private bool autoAdjustRanThisSession = false;

        // P/Invoke for performance
        private const int WM_SETREDRAW = 0x000B;
        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        public LeviTextEditorForm()
        {
            try
            {
                Text = "LeviText";
                Width = 900;
                Height = 600;
                StartPosition = FormStartPosition.CenterScreen;
                FormBorderStyle = FormBorderStyle.Sizable;
                MinimumSize = new Size(320, 200);

                // Set form icon
                var assembly = Assembly.GetExecutingAssembly();
                using (var stream = assembly.GetManifestResourceStream("LeviText.LeviText.ico"))
                {
                    if (stream != null)
                        this.Icon = new Icon(stream);
                    else
                        this.Icon = SystemIcons.Application;
                }

                // Initialize file paths
                autosaveFile = Path.Combine(appFolder, "autosave.rtf");
                settingsFile = Path.Combine(appFolder, "settings.json");
                formatWindowSettingsFile = Path.Combine(appFolder, "formatwindow.json");

                // Ensure folder
                try { Directory.CreateDirectory(appFolder); } catch { /* ignore */ }

                LoadSettings();

                BackColor = windowBack;
                ForeColor = windowFore;

                InitializeComponents();

                // Restore TopMost state from settings
                this.TopMost = cfg?.TopMost ?? true;

                // Initialize timers
                debounceFormatTimer = new System.Windows.Forms.Timer();
                debounceFormatTimer.Interval = 700;
                debounceFormatTimer.Tick += (s, e) => { debounceFormatTimer.Stop(); };

                autosaveTimer = new System.Windows.Forms.Timer();
                autosaveTimer.Interval = 60 * 1000;
                autosaveTimer.Tick += (s, e) => DoAutoSave();
                autosaveTimer.Start();

                // Event handlers
                this.Load += new EventHandler(LeviTextEditorForm_Load);
                this.Activated += (s, e) => { 
                    if (cfg != null && this.TopMost != cfg.TopMost) { 
                        cfg.TopMost = this.TopMost; 
                        SaveSettings(); 
                        UpdatePinButton(); 
                    } 
                };
                this.Activated += (s, e) => { if (cfg != null) this.TopMost = cfg.TopMost; };
                this.Deactivate += (s, e) => { if (cfg != null) this.TopMost = cfg.TopMost; };
            }
            catch (Exception ex)
            {
                MessageBox.Show("Startup error: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                throw;
            }
        }

        private void InitializeComponents()
        {
            // Menu Bar
            var menuBar = new MenuStrip();
            menuBar.BackColor = windowBack;
            menuBar.ForeColor = windowFore;
            menuBar.Renderer = new DarkMenuRenderer();

            // File Menu
            var fileMenu = new ToolStripMenuItem("File");
            fileMenu.ForeColor = windowFore;

            var newWindowItem = new ToolStripMenuItem("New Window");
            newWindowItem.ForeColor = windowFore;
            newWindowItem.Click += (s, e) => LaunchNewInstance();

            var separator1 = new ToolStripSeparator();

            var exitItem = new ToolStripMenuItem("Exit");
            exitItem.ForeColor = windowFore;
            exitItem.Click += (s, e) => this.Close();

            fileMenu.DropDownItems.AddRange(new ToolStripItem[] {
                newWindowItem, separator1, exitItem
            });

            // Edit Menu
            var editMenu = new ToolStripMenuItem("Edit");
            editMenu.ForeColor = windowFore;

            var newItem = new ToolStripMenuItem("New");
            newItem.ForeColor = windowFore;
            newItem.Click += (s, e) => NewFile();

            var openItem = new ToolStripMenuItem("Open");
            openItem.ForeColor = windowFore;
            openItem.Click += (s, e) => DoOpen();

            var saveItem = new ToolStripMenuItem("Save");
            saveItem.ForeColor = windowFore;
            saveItem.Click += (s, e) => DoSave();

            editMenu.DropDownItems.AddRange(new ToolStripItem[] {
                newItem, openItem, saveItem
            });

            // Format Menu (shows floating format window)
            var formatMenu = new ToolStripMenuItem("Format");
            formatMenu.ForeColor = windowFore;
            formatMenu.Click += (s, e) => ShowFormatWindow();

            // Levitate Menu
            levitateMenuItem = new ToolStripMenuItem(cfg != null && cfg.TopMost ? "Levitate: On" : "Levitate: Off");
            levitateMenuItem.ForeColor = windowFore;
            levitateMenuItem.CheckOnClick = true;
            levitateMenuItem.Checked = cfg != null && cfg.TopMost;
            levitateMenuItem.Click += (s, e) => TogglePin();

            menuBar.Items.AddRange(new ToolStripItem[] {
                fileMenu, editMenu, formatMenu, levitateMenuItem
            });
            menuBar.Dock = DockStyle.Top;

            // RichTextBox
            rtb = new RichTextBox()
            {
                BorderStyle = BorderStyle.FixedSingle,
                DetectUrls = true,
                EnableAutoDragDrop = true,
                AcceptsTab = true,
                HideSelection = false,
                Multiline = true,
                ScrollBars = RichTextBoxScrollBars.Both,
                WordWrap = true,
                BackColor = rtbBack,
                ForeColor = windowFore,
                Font = new Font("Segoe UI", 11f),
                Margin = new Padding(0),
                Dock = DockStyle.Fill
            };
            rtb.TextChanged += (s, e) => { debounceFormatTimer.Stop(); debounceFormatTimer.Start(); };

            rtb.SelectionChanged += (s, e) =>
            {
                if (rtb != null && rtb.SelectionLength == 0)
                {
                    int caretIndex = rtb.SelectionStart;
                    int line = rtb.GetLineFromCharIndex(caretIndex);
                    if (line == 0)
                    {
                        int line2Start = rtb.GetFirstCharIndexFromLine(1);
                        if (line2Start >= 0 && line2Start < rtb.TextLength)
                        {
                            rtb.SelectionStart = line2Start;
                            rtb.SelectionLength = 0;
                        }
                    }
                }
                UpdateFormatPanelFromSelection();
            };

            // Add controls
            this.Controls.Add(rtb);
            this.Controls.Add(menuBar);

            // Context menu
            var ctx = new ContextMenuStrip();
            ctx.BackColor = windowBack; 
            ctx.ForeColor = windowFore;
            ctx.Renderer = new DarkMenuRenderer();
            ctx.Items.Add("Cut", null, (s, e) => rtb.Cut());
            ctx.Items.Add("Copy", null, (s, e) => rtb.Copy());
            ctx.Items.Add("Paste", null, (s, e) => rtb.Paste());
            ctx.Items.Add(new ToolStripSeparator());
            ctx.Items.Add("Select All", null, (s, e) => rtb.SelectAll());
            rtb.ContextMenuStrip = ctx;

            this.Padding = new Padding(0);
        }

        private void ShowFormatWindow()
        {
            if (formatWindow == null || formatWindow.IsDisposed)
            {
                formatWindow = new FormatWindow(this, formatWindowSettingsFile);
                formatWindow.Owner = this;
                formatWindow.TopMost = this.TopMost;
            }
            
            if (!formatWindow.Visible)
            {
                formatWindow.Show();
                formatWindow.BringToFront();
            }
            else
            {
                formatWindow.BringToFront();
            }
        }

        public void UpdateFormatWindowLevitateState()
        {
            if (formatWindow != null && !formatWindow.IsDisposed)
            {
                formatWindow.TopMost = this.TopMost;
            }
        }

        private void LaunchNewInstance()
        {
            try
            {
                var currentExe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(currentExe))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = currentExe,
                        UseShellExecute = true
                    });
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to launch new instance: {ex.Message}", "Error", 
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void UpdateFormatPanelFromSelection()
        {
            if (rtb == null) return;
            
            try
            {
                Font? currentFont = rtb.SelectionFont;
                Color currentColor = rtb.SelectionColor;
                Color currentBackColor = rtb.SelectionBackColor;
                
                if (formatWindow != null && !formatWindow.IsDisposed && formatWindow.Visible)
                {
                    formatWindow.UpdateFormatDisplay(currentFont, currentColor, currentBackColor);
                }
            }
            catch
            {
                // Silently ignore errors
            }
        }

        public void ApplyCurrentFormatFromWindow(FormatWindow window)
        {
            if (rtb != null && window != null)
            {
                var fontName = window.FontName;
                var size = window.FontSize;
                var style = FontStyle.Regular;
                if (window.IsBold) style |= FontStyle.Bold;
                if (window.IsItalic) style |= FontStyle.Italic;
                if (window.IsUnderline) style |= FontStyle.Underline;
                var color = window.TextColor;
                var highlightColor = window.HighlightColor;
                
                if (rtb.SelectionLength > 0)
                {
                    ApplyFormattingToSelection(fontName, size, style, color, highlightColor);
                }
                else
                {
                    rtb.SelectionFont = new Font(fontName, size, style);
                    if (cfg != null && cfg.AutoAdjustNearBlack && IsNearlyBlack(color))
                        rtb.SelectionColor = adjustedReadableColor;
                    else
                        rtb.SelectionColor = color;
                    
                    if (highlightColor != Color.Empty)
                        rtb.SelectionBackColor = highlightColor;
                    else
                        rtb.SelectionBackColor = rtbBack;
                }
            }
        }

        private void ApplyFormattingToSelection(string fontName, float size, FontStyle style, Color color, Color highlightColor)
        {
            try
            {
                if (rtb == null) return;
                var selStart = rtb.SelectionStart; 
                var selLen = rtb.SelectionLength;
                if (selLen == 0) return;
                
                rtb.SelectionFont = new Font(fontName, size, style);
                if (cfg != null && cfg.AutoAdjustNearBlack && IsNearlyBlack(color))
                    rtb.SelectionColor = adjustedReadableColor;
                else
                    rtb.SelectionColor = color;
                
                if (highlightColor != Color.Empty)
                    rtb.SelectionBackColor = highlightColor;
                else
                    rtb.SelectionBackColor = rtbBack;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Error applying format: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void LeviTextEditorForm_Load(object? sender, EventArgs e)
        {
            autoAdjustRanThisSession = false;
            if (cfg != null && cfg.AutoAdjustNearBlack)
            {
                Task.Run(() => AdjustNearBlackRunsOncePerSession());
            }
            
            try
            {
                if (File.Exists(autosaveFile))
                {
                    var fileInfo = new FileInfo(autosaveFile);
                    if (fileInfo.Length > 0)
                    {
                        try 
                        { 
                            if (rtb != null) rtb.LoadFile(autosaveFile, RichTextBoxStreamType.RichText); 
                        }
                        catch 
                        { 
                            if (rtb != null) rtb.Text = File.ReadAllText(autosaveFile); 
                        }
                        
                        autoAdjustRanThisSession = false;
                        if (cfg != null && cfg.AutoAdjustNearBlack)
                        {
                            rtb?.Invoke(new Action(() => AdjustNearBlackRunsOncePerSession()));
                        }
                    }
                }
            }
            catch 
            { 
                // Silently ignore autosave recovery errors
            }
        }

        public void NewFile()
        {
            if (rtb != null && rtb.TextLength > 0)
            {
                var res = MessageBox.Show(this, "Clear current content?", "New", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (res != DialogResult.Yes) return;
            }
            if (rtb != null) rtb.Clear();
            autoAdjustRanThisSession = false;
        }

        public void DoOpen()
        {
            using (var ofd = new OpenFileDialog())
            {
                ofd.Filter = "Rich Text Format (*.rtf)|*.rtf|Text files (*.txt)|*.txt|All files (*.*)|*.*";
                if (ofd.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        if (rtb != null)
                        {
                            try { rtb.LoadFile(ofd.FileName, RichTextBoxStreamType.RichText); }
                            catch { rtb.Text = File.ReadAllText(ofd.FileName); }

                            autoAdjustRanThisSession = false;

                            if (cfg != null && cfg.AutoAdjustNearBlack)
                            {
                                rtb.Invoke(new Action(() => AdjustNearBlackRunsOncePerSession()));
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show("Failed to open file:\n" + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        public void DoSave()
        {
            using (var sfd = new SaveFileDialog())
            {
                sfd.Filter = "Rich Text Format (*.rtf)|*.rtf|Text files (*.txt)|*.txt";
                sfd.DefaultExt = "rtf";
                if (sfd.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        if (rtb != null)
                        {
                            if (Path.GetExtension(sfd.FileName).ToLowerInvariant() == ".txt") 
                                File.WriteAllText(sfd.FileName, rtb.Text);
                            else 
                                rtb.SaveFile(sfd.FileName, RichTextBoxStreamType.RichText);
                        }
                    }
                    catch (Exception ex) 
                    { 
                        MessageBox.Show(this, "Error saving: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error); 
                    }
                }
            }
        }

        private void DoAutoSave()
        {
            try
            {
                if (rtb != null) rtb.SaveFile(autosaveFile, RichTextBoxStreamType.RichText);
            }
            catch
            {
                try { if (rtb != null) File.WriteAllText(autosaveFile, rtb.Rtf); } catch { }
            }
        }

        public void TogglePin()
        {
            if (cfg != null)
            {
                cfg.TopMost = !cfg.TopMost;
                this.TopMost = cfg.TopMost;
                SaveSettings();
                UpdatePinButton();
                UpdateFormatWindowLevitateState();
            }
        }

        private void UpdatePinButton()
        {
            if (cfg != null && levitateMenuItem != null)
            {
                levitateMenuItem.Text = cfg.TopMost ? "Levitate: On" : "Levitate: Off";
                levitateMenuItem.Checked = cfg.TopMost;
            }
        }

        private void LoadSettings()
        {
            try
            {
                if (File.Exists(settingsFile))
                {
                    var txt = File.ReadAllText(settingsFile);
                    cfg = JsonSerializer.Deserialize<Settings>(txt) ?? new Settings();
                }
                else cfg = new Settings();
            }
            catch { cfg = new Settings(); }
        }

        private void SaveSettings()
        {
            try
            {
                if (cfg != null)
                {
                    var txt = JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(settingsFile, txt);
                }
            }
            catch { }
        }

        private bool IsNearlyBlack(Color c)
        {
            return Math.Max(c.R, Math.Max(c.G, c.B)) <= 24;
        }

        private void BeginUpdate(RichTextBox box)
        {
            SendMessage(box.Handle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
        }
        
        private void EndUpdate(RichTextBox box)
        {
            SendMessage(box.Handle, WM_SETREDRAW, new IntPtr(1), IntPtr.Zero);
            box.Refresh();
        }

        private void AdjustNearBlackRunsOncePerSession()
        {
            if (autoAdjustRanThisSession) return;
            autoAdjustRanThisSession = true;

            if (cfg == null || !cfg.AutoAdjustNearBlack) return;

            try
            {
                if (InvokeRequired)
                {
                    Invoke(new Action(() => AdjustNearBlackRunsOncePerSession()));
                    return;
                }

                if (rtb == null) return;
                int len = rtb.TextLength;
                if (len == 0) return;

                var selStart = rtb.SelectionStart; 
                var selLen = rtb.SelectionLength;

                BeginUpdate(rtb);

                rtb.SelectAll();
                Color c = rtb.SelectionColor;
                if (IsNearlyBlack(c))
                {
                    rtb.SelectionColor = adjustedReadableColor;
                }

                int i = 0;
                while (i < len)
                {
                    rtb.Select(i, 1);
                    Color nc = rtb.SelectionColor;
                    int runStart = i;
                    bool nearBlack = IsNearlyBlack(nc);
                    i++;
                    while (i < len)
                    {
                        rtb.Select(i, 1);
                        Color nc2 = rtb.SelectionColor;
                        if (IsNearlyBlack(nc2) != nearBlack) break;
                        i++;
                    }
                    int runLen = i - runStart;
                    if (nearBlack)
                    {
                        rtb.Select(runStart, runLen);
                        rtb.SelectionColor = adjustedReadableColor;
                    }
                }

                rtb.Select(selStart, selLen);
                EndUpdate(rtb);
            }
            catch
            {
                // if anything fails, bail quietly
            }
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            try
            {
                if (formatWindow != null && !formatWindow.IsDisposed)
                {
                    formatWindow.Close();
                    formatWindow.Dispose();
                }
                
                autosaveTimer?.Stop();
                autosaveTimer?.Dispose();
                debounceFormatTimer?.Stop();
                debounceFormatTimer?.Dispose();
                
                SaveSettings();
            }
            catch
            {
                // Ignore cleanup errors
            }
            
            base.OnClosing(e);
        }

        private class Settings
        {
            public bool AutoAdjustNearBlack { get; set; } = true;
            public bool TopMost { get; set; } = true;
        }

        private class FormatWindowSettings
        {
            public int X { get; set; } = -1;
            public int Y { get; set; } = -1;
            public int Width { get; set; } = 400;  // Start at minimum width
            public int Height { get; set; } = 300; // Start at minimum height
        }
    }

    public class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer() : base(new DarkMenuColors()) { }
    }

    public class DarkMenuColors : ProfessionalColorTable
    {
        public override Color MenuBorder => Color.FromArgb(40, 40, 40);
        public override Color MenuItemBorder => Color.FromArgb(60, 60, 60);
        public override Color MenuItemSelected => Color.FromArgb(60, 60, 60);
        public override Color MenuItemSelectedGradientBegin => Color.FromArgb(60, 60, 60);
        public override Color MenuItemSelectedGradientEnd => Color.FromArgb(60, 60, 60);
        public override Color MenuItemPressedGradientBegin => Color.FromArgb(70, 70, 70);
        public override Color MenuItemPressedGradientEnd => Color.FromArgb(70, 70, 70);
        
        public override Color ButtonSelectedHighlight => Color.FromArgb(60, 60, 60);
        public override Color ButtonSelectedHighlightBorder => Color.FromArgb(70, 70, 70);
        public override Color ButtonPressedHighlight => Color.FromArgb(70, 70, 70);
        public override Color ButtonPressedHighlightBorder => Color.FromArgb(80, 80, 80);
        public override Color ButtonCheckedHighlight => Color.FromArgb(60, 60, 60);
        public override Color ButtonCheckedHighlightBorder => Color.FromArgb(70, 70, 70);
        public override Color ButtonSelectedBorder => Color.FromArgb(70, 70, 70);
        public override Color ButtonPressedBorder => Color.FromArgb(80, 80, 80);
        public override Color ButtonCheckedGradientBegin => Color.FromArgb(60, 60, 60);
        public override Color ButtonCheckedGradientEnd => Color.FromArgb(60, 60, 60);
        public override Color ButtonSelectedGradientBegin => Color.FromArgb(60, 60, 60);
        public override Color ButtonSelectedGradientEnd => Color.FromArgb(60, 60, 60);
        public override Color ButtonPressedGradientBegin => Color.FromArgb(70, 70, 70);
        public override Color ButtonPressedGradientEnd => Color.FromArgb(70, 70, 70);
        
        public override Color ToolStripDropDownBackground => Color.FromArgb(30, 30, 30);
        public override Color ImageMarginGradientBegin => Color.FromArgb(30, 30, 30);
        public override Color ImageMarginGradientMiddle => Color.FromArgb(30, 30, 30);
        public override Color ImageMarginGradientEnd => Color.FromArgb(30, 30, 30);
        public override Color MenuStripGradientBegin => Color.FromArgb(30, 30, 30);
        public override Color MenuStripGradientEnd => Color.FromArgb(30, 30, 30);
        public override Color ToolStripBorder => Color.FromArgb(40, 40, 40);
        public override Color SeparatorDark => Color.FromArgb(50, 50, 50);
        public override Color SeparatorLight => Color.FromArgb(50, 50, 50);
    }

    public class FormatWindow : Form
    {
        private readonly LeviTextEditorForm parentEditor;
        private readonly string settingsFile;
        private readonly Color windowBack = Color.FromArgb(30, 30, 30);
        private readonly Color windowFore = Color.FromArgb(230, 230, 230);
        private FormatWindowSettings? settings;
        
        // Format controls
        private ComboBox? cboFont;
        private NumericUpDown? nudSize;
        private Button? btnBold, btnItalic, btnUnderline;
        private Button? btnColor, btnHighlight;
        private TextBox? txtColorHex, txtHighlightHex;
        
        public FormatWindow(LeviTextEditorForm parent, string settingsFilePath)
        {
            parentEditor = parent;
            settingsFile = settingsFilePath;
            LoadSettings();
            InitializeFormatWindow();
        }
        
        private void LoadSettings()
        {
            try
            {
                if (File.Exists(settingsFile))
                {
                    var txt = File.ReadAllText(settingsFile);
                    settings = JsonSerializer.Deserialize<FormatWindowSettings>(txt) ?? new FormatWindowSettings();
                }
                else
                {
                    settings = new FormatWindowSettings();
                }
            }
            catch
            {
                settings = new FormatWindowSettings();
            }
        }
        
        private void SaveSettings()
        {
            try
            {
                if (settings != null)
                {
                    settings.X = this.Left;
                    settings.Y = this.Top;
                    settings.Width = this.Width;
                    settings.Height = this.Height;
                    
                    var txt = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(settingsFile, txt);
                }
            }
            catch { }
        }
        
        private void InitializeFormatWindow()
        {
            Text = "Format";
            FormBorderStyle = FormBorderStyle.Sizable; // Changed from SizableToolWindow to show icon
            MinimumSize = new Size(400, 300);
            BackColor = windowBack;
            ForeColor = windowFore;
            ShowInTaskbar = false; // Keep this to prevent taskbar appearance
            
            // Set the same icon as the parent window
            var assembly = Assembly.GetExecutingAssembly();
            using (var stream = assembly.GetManifestResourceStream("LeviText.LeviText.ico"))
            {
                if (stream != null)
                    this.Icon = new Icon(stream);
                else
                    this.Icon = SystemIcons.Application; // fallback
            }
            
            // Restore size and position from settings
            if (settings != null)
            {
                Width = settings.Width;
                Height = settings.Height;
                
                if (settings.X >= 0 && settings.Y >= 0)
                {
                    StartPosition = FormStartPosition.Manual;
                    Left = settings.X;
                    Top = settings.Y;
                    EnsureVisibleOnScreen();
                }
                else
                {
                    StartPosition = FormStartPosition.Manual;
                    if (parentEditor != null)
                    {
                        Left = parentEditor.Left + parentEditor.Width + 10;
                        Top = parentEditor.Top;
                        EnsureVisibleOnScreen();
                    }
                }
            }
            
            var panel = new Panel() { Dock = DockStyle.Fill, BackColor = windowBack, ForeColor = windowFore, Padding = new Padding(10) };

            int y = 10;
            var lblFont = new Label() { Text = "Font:", Left = 10, Top = y, Width = 50, ForeColor = windowFore };
            cboFont = new ComboBox() { Left = 70, Top = y, Width = 200, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Color.FromArgb(50, 50, 50), ForeColor = windowFore };
            foreach (var f in FontFamily.Families.OrderBy(ff => ff.Name)) cboFont.Items.Add(f.Name);
            cboFont.SelectedItem = "Segoe UI";

            y += 40;
            var lblSize = new Label() { Text = "Size:", Left = 10, Top = y, Width = 50, ForeColor = windowFore };
            nudSize = new NumericUpDown() { Left = 70, Top = y, Width = 80, Minimum = 6, Maximum = 200, Value = 11, BackColor = Color.FromArgb(50, 50, 50), ForeColor = windowFore };

            y += 40;
            // Style button colors
            Color styleBtnBack = Color.FromArgb(50, 50, 50);
            Color styleBtnFore = windowFore;
            Color styleBtnBackActive = Color.FromArgb(80, 80, 80);
            Color styleBtnBorder = Color.FromArgb(100, 100, 100);

            // Create style buttons
            btnBold = new Button()
            {
                Text = "B",
                Left = 10,
                Top = y,
                Width = 50,
                Height = 36,
                Font = new Font(FontFamily.GenericSansSerif, 10, FontStyle.Bold),
                FlatStyle = FlatStyle.Flat,
                BackColor = styleBtnBack,
                ForeColor = styleBtnFore
            };
            btnBold.FlatAppearance.BorderSize = 1;
            btnBold.FlatAppearance.BorderColor = styleBtnBorder;
            btnBold.FlatAppearance.MouseOverBackColor = styleBtnBackActive;
            btnBold.FlatAppearance.MouseDownBackColor = styleBtnBackActive;

            btnItalic = new Button()
            {
                Text = "I",
                Left = 70,
                Top = y,
                Width = 50,
                Height = 36,
                Font = new Font(FontFamily.GenericSansSerif, 10, FontStyle.Italic),
                FlatStyle = FlatStyle.Flat,
                BackColor = styleBtnBack,
                ForeColor = styleBtnFore
            };
            btnItalic.FlatAppearance.BorderSize = 1;
            btnItalic.FlatAppearance.BorderColor = styleBtnBorder;
            btnItalic.FlatAppearance.MouseOverBackColor = styleBtnBackActive;
            btnItalic.FlatAppearance.MouseDownBackColor = styleBtnBackActive;

            btnUnderline = new Button()
            {
                Text = "U",
                Left = 130,
                Top = y,
                Width = 50,
                Height = 36,
                Font = new Font(FontFamily.GenericSansSerif, 10, FontStyle.Underline),
                FlatStyle = FlatStyle.Flat,
                BackColor = styleBtnBack,
                ForeColor = styleBtnFore
            };
            btnUnderline.FlatAppearance.BorderSize = 1;
            btnUnderline.FlatAppearance.BorderColor = styleBtnBorder;
            btnUnderline.FlatAppearance.MouseOverBackColor = styleBtnBackActive;
            btnUnderline.FlatAppearance.MouseDownBackColor = styleBtnBackActive;

            y += 50;
            var lblColor = new Label() { Text = "Color:", Left = 10, Top = y, Width = 50, ForeColor = windowFore };
            btnColor = new Button() { Left = 70, Top = y, Width = 60, Height = 36, BackColor = Color.White, FlatStyle = FlatStyle.Flat };
            txtColorHex = new TextBox() { Left = 140, Top = y, Width = 80, Text = "#FFFFFF", BackColor = Color.FromArgb(50, 50, 50), ForeColor = windowFore };

            btnColor.FlatAppearance.BorderSize = 1;
            btnColor.FlatAppearance.BorderColor = styleBtnBorder;
            btnColor.FlatAppearance.MouseOverBackColor = Color.FromArgb(70, 70, 70);
            btnColor.FlatAppearance.MouseDownBackColor = Color.FromArgb(90, 90, 90);

            y += 40;
            var lblHighlight = new Label() { Text = "Highlight:", Left = 10, Top = y, Width = 60, ForeColor = windowFore };
            btnHighlight = new Button() { Left = 70, Top = y, Width = 60, Height = 36, BackColor = Color.Transparent, FlatStyle = FlatStyle.Flat, Text = "None" };
            txtHighlightHex = new TextBox() { Left = 140, Top = y, Width = 80, Text = "None", BackColor = Color.FromArgb(50, 50, 50), ForeColor = windowFore };

            btnHighlight.FlatAppearance.BorderSize = 1;
            btnHighlight.FlatAppearance.BorderColor = styleBtnBorder;
            btnHighlight.FlatAppearance.MouseOverBackColor = Color.FromArgb(70, 70, 70);
            btnHighlight.FlatAppearance.MouseDownBackColor = Color.FromArgb(90, 90, 90);

            y += 50;
            var chkWordWrap = new CheckBox()
            {
                Text = "Word Wrap",
                Left = 10,
                Top = y,
                Checked = parentEditor?.rtb?.WordWrap ?? true,
                ForeColor = windowFore,
                BackColor = windowBack,
                Width = 120
            };

            // Toggle logic for style buttons
            Action<Button> toggleAction = (btn) =>
            {
                bool st = !(btn.Tag as bool? ?? false);
                btn.Tag = st;
                btn.BackColor = st ? styleBtnBackActive : styleBtnBack;
                btn.ForeColor = styleBtnFore;
                btn.FlatAppearance.BorderColor = styleBtnBorder;
            };

            // Event handlers for immediate formatting
            btnBold.Click += (s, e) => { toggleAction(btnBold); parentEditor?.ApplyCurrentFormatFromWindow(this); };
            btnItalic.Click += (s, e) => { toggleAction(btnItalic); parentEditor?.ApplyCurrentFormatFromWindow(this); };
            btnUnderline.Click += (s, e) => { toggleAction(btnUnderline); parentEditor?.ApplyCurrentFormatFromWindow(this); };
            cboFont.SelectedIndexChanged += (s, e) => parentEditor?.ApplyCurrentFormatFromWindow(this);
            nudSize.ValueChanged += (s, e) => parentEditor?.ApplyCurrentFormatFromWindow(this);

            btnColor.Click += (s, e) => {
                using (var cd = new ColorDialog()) {
                    if (cd.ShowDialog() == DialogResult.OK) {
                        btnColor.BackColor = cd.Color;
                        txtColorHex.Text = $"#{cd.Color.R:X2}{cd.Color.G:X2}{cd.Color.B:X2}";
                        parentEditor?.ApplyCurrentFormatFromWindow(this);
                    }
                }
            };
            txtColorHex.TextChanged += (s, e) => {
                try {
                    if (txtColorHex.Text.StartsWith("#") && txtColorHex.Text.Length == 7) {
                        var c = ColorTranslator.FromHtml(txtColorHex.Text);
                        btnColor.BackColor = c;
                        parentEditor?.ApplyCurrentFormatFromWindow(this);
                    }
                } catch { }
            };

            btnHighlight.Click += (s, e) => {
                using (var cd = new ColorDialog()) {
                    if (cd.ShowDialog() == DialogResult.OK) {
                        btnHighlight.BackColor = cd.Color;
                        btnHighlight.Text = "";
                        txtHighlightHex.Text = $"#{cd.Color.R:X2}{cd.Color.G:X2}{cd.Color.B:X2}";
                        parentEditor?.ApplyCurrentFormatFromWindow(this);
                    }
                }
            };
            txtHighlightHex.TextChanged += (s, e) => {
                try {
                    if (txtHighlightHex.Text.ToLower() == "none" || txtHighlightHex.Text.Trim() == "") {
                        btnHighlight.BackColor = Color.Transparent;
                        btnHighlight.Text = "None";
                        parentEditor?.ApplyCurrentFormatFromWindow(this);
                    }
                    else if (txtHighlightHex.Text.StartsWith("#") && txtHighlightHex.Text.Length == 7) {
                        var c = ColorTranslator.FromHtml(txtHighlightHex.Text);
                        btnHighlight.BackColor = c;
                        btnHighlight.Text = "";
                        parentEditor?.ApplyCurrentFormatFromWindow(this);
                    }
                } catch { }
            };

            chkWordWrap.CheckedChanged += (s, e) =>
            {
                if (parentEditor?.rtb != null)
                {
                    parentEditor.rtb.WordWrap = chkWordWrap.Checked;
                    parentEditor.rtb.ScrollBars = chkWordWrap.Checked
                       ? RichTextBoxScrollBars.Vertical
                       : RichTextBoxScrollBars.Both;
                }
            };

            panel.Controls.AddRange(new Control[] {
                lblFont, cboFont, lblSize, nudSize,
                btnBold, btnItalic, btnUnderline,
                lblColor, btnColor, txtColorHex,
                lblHighlight, btnHighlight, txtHighlightHex,
                chkWordWrap
            });

            this.Controls.Add(panel);
            
            // Handle window events for position saving
            this.LocationChanged += (s, e) => SaveSettings();
            this.SizeChanged += (s, e) => SaveSettings();
        }
        
        private void EnsureVisibleOnScreen()
        {
            // Make sure the window is visible on at least one screen
            var screen = Screen.FromPoint(new Point(Left, Top));
            if (Left < screen.WorkingArea.Left) Left = screen.WorkingArea.Left;
            if (Top < screen.WorkingArea.Top) Top = screen.WorkingArea.Top;
            if (Left + Width > screen.WorkingArea.Right) Left = screen.WorkingArea.Right - Width;
            if (Top + Height > screen.WorkingArea.Bottom) Top = screen.WorkingArea.Bottom - Height;
        }
        
        // Public methods for the parent to update format display
        public void UpdateFormatDisplay(Font? font, Color textColor, Color backColor)
        {
            try
            {
                if (font != null)
                {
                    if (cboFont != null) cboFont.SelectedItem = font.FontFamily.Name;
                    if (nudSize != null) nudSize.Value = (decimal)font.Size;
                    
                    Color styleBtnBack = Color.FromArgb(50, 50, 50);
                    Color styleBtnBackActive = Color.FromArgb(80, 80, 80);
                    
                    // Update style buttons
                    if (btnBold != null)
                    {
                        btnBold.Tag = font.Bold;
                        btnBold.BackColor = font.Bold ? styleBtnBackActive : styleBtnBack;
                    }
                    
                    if (btnItalic != null)
                    {
                        btnItalic.Tag = font.Italic;
                        btnItalic.BackColor = font.Italic ? styleBtnBackActive : styleBtnBack;
                    }
                    
                    if (btnUnderline != null)
                    {
                        btnUnderline.Tag = font.Underline;
                        btnUnderline.BackColor = font.Underline ? styleBtnBackActive : styleBtnBack;
                    }
                }
                
                if (btnColor != null) btnColor.BackColor = textColor;
                if (txtColorHex != null) txtColorHex.Text = $"#{textColor.R:X2}{textColor.G:X2}{textColor.B:X2}";
                
                if (backColor == Color.Empty || backColor == Color.FromArgb(20, 20, 20))
                {
                    if (btnHighlight != null)
                    {
                        btnHighlight.BackColor = Color.Transparent;
                        btnHighlight.Text = "None";
                    }
                    if (txtHighlightHex != null) txtHighlightHex.Text = "None";
                }
                else
                {
                    if (btnHighlight != null)
                    {
                        btnHighlight.BackColor = backColor;
                        btnHighlight.Text = "";
                    }
                    if (txtHighlightHex != null) txtHighlightHex.Text = $"#{backColor.R:X2}{backColor.G:X2}{backColor.B:X2}";
                }
            }
            catch { }
        }
        
        // Public getters for format values
        public string FontName => cboFont?.SelectedItem?.ToString() ?? "Segoe UI";
        public float FontSize => (float)(nudSize?.Value ?? 11);
        public bool IsBold => btnBold?.Tag as bool? ?? false;
        public bool IsItalic => btnItalic?.Tag as bool? ?? false;
        public bool IsUnderline => btnUnderline?.Tag as bool? ?? false;
        public Color TextColor => btnColor?.BackColor ?? Color.White;
        public Color HighlightColor => (btnHighlight?.BackColor == Color.Transparent || btnHighlight?.Text == "None") ? Color.Empty : (btnHighlight?.BackColor ?? Color.Empty);
        
        protected override void OnClosing(CancelEventArgs e)
        {
            SaveSettings();
            base.OnClosing(e);
        }

        private class FormatWindowSettings
        {
            public int X { get; set; } = -1;
            public int Y { get; set; } = -1;
            public int Width { get; set; } = 500;
            public int Height { get; set; } = 350;
        }
    }
}