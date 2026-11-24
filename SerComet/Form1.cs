using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace UartSlTest
{
    public partial class Form1 : Form
    {
        SerialPort port;
        readonly List<byte> rx = new List<byte>();
        readonly object rxLock = new object();
        int lastRx;
        volatile bool abortRequested;
        bool rxStartLogged;
        bool sendingFlag;
        // while a file is being sent the user cannot change the port, the target computer, the transmission mode and the check boxes
        // (the controls keep their look, see InputLock)
        bool sending { get { return sendingFlag; } set { sendingFlag = value; InputLock.Active = value; } }
        readonly System.Diagnostics.Stopwatch blinkWatch = System.Diagnostics.Stopwatch.StartNew();
        Timer blinkTimer;
        int lastRxAny, lastTxAny;     // time of the last received / sent data (also in the terminal)
        volatile bool rs232;

        BannerPanel banner;
        CheckBox chkEof;
        Button btnView;
        ComboBox comboPorts, comboBaud, comboEol, comboFlow;
        CheckBox chkAuto, chkSkipRem;
        readonly WrapToolTip tips = new WrapToolTip { AutoPopDelay = 9000, InitialDelay = 400, ReshowDelay = 200, ShowAlways = true };
        TerminalForm term;
        Button btnTerminal, btnAbort, btnConvert, btnLocations, btnDelete;
        // default folders of the profiles (see NProf)
        // profiles of the target computers: 0 PC-1500, 1 PC-1600, 2 PC-1350, 3 PC-1360, 4 PC-E500, 5 PC-E500S, 6 PC-1450..75, 7 Sharp other,
        // 8 Casio FX-850/870, 9 TRS-80 / NEC, 10 other UART. The group (the old model number 0..5) joins the profiles 2..7 as "Other Sharp".
        const int NProf = 11;
        static int GroupOf(int prof) { return prof < 2 ? prof : prof <= 7 ? 2 : prof - 5; }
        int curProf = -1;
        string[] locs = LoadLocations();      // overwritten by settings.ini when it exists
        Button btnTest, btnRefresh, btnOpen, btnFlush, btnReset, btnBrowse, btnOpenDir, btnSend, btnSendOther, btnRefreshFiles;
        TextBox txtDir;
        RichTextBox txtLog;
        ListBox lstFiles;
        TextBox lblHint;                    // the Info text: read-only, can be selected and copied
        LedLabel led;
        RadioButton radio1500, radio1600, radioOther, radioFx, radioTrs, radioUart;
        ComboBox comboSharp;                // the Sharp (other) models: PC-1350, PC-1360, PC-E500, PC-E500S, PC-1450..75, other
        RadioButton radioSoft, radioRs, radioSio;
        NumericUpDown numDelay;
        Timer timer;

        public Form1()
        {
            InitializeComponent();
            Text = "SerComet " + AppInfo.Version;
            try { Icon = Icons.AppIcon(); } catch { }
            BuildUi();
            SetRefreshMode(false);
            FormClosing += delegate { SaveSettings(); ClosePort(); };
            RefreshPorts();
            UpdateMode();
            InputLock.Notify = delegate
            {
                int now = Environment.TickCount;
                if (unchecked(now - lastLockNote) < 4000) return;
                lastLockNote = now;
                Log("A file is being sent: the port, target computer, transmission mode and options cannot be changed now (Abort stops the transmission).");
            };
            LoadSettings();
            // restore the last port and target computer, and with it that computer's mode and transmission settings
            if (startPort != null && comboPorts.Items.Contains(startPort)) comboPorts.SelectedItem = startPort;
            else if (startPort != null && comboPorts.SelectedItem != null)
                Log("The saved port " + startPort + " is not available; the first available port " + comboPorts.SelectedItem + " is selected.");
            int startGroup = GroupOf(startModel);
            if (startGroup == 2) comboSharp.SelectedIndex = startModel - 2;
            (startGroup == 0 ? radio1500 : startGroup == 2 ? radioOther : startGroup == 3 ? radioFx : startGroup == 4 ? radioTrs : startGroup == 5 ? radioUart : radio1600).Checked = true;
            SetTarget();
            if (!File.Exists(SettingsFile())) SaveSettings();       // first start or the file was deleted: build it again
            Log("Ready. Select the port and click 'Open port'.");
        }

        Button Btn(string text, Bitmap img, int x, int y, int w, int h, AnchorStyles anchor)
        {
            return new Button
            {
                Text = text, Image = img, ImageAlign = ContentAlignment.MiddleLeft,
                TextAlign = ContentAlignment.MiddleRight, Padding = new Padding(0, 0, 6, 0), Left = x, Top = y, Width = w, Height = h, Anchor = anchor
            };
        }

        // the Buy me a coffee picture (img/buymeacoffe58x58.jpg) is built into the program, so it always appears
        static Image LoadBmcImage()
        {
            try
            {
                using (var ms = new MemoryStream(Convert.FromBase64String(BmcData.Jpg)))
                using (var src = Image.FromStream(ms)) return new Bitmap(src);
            }
            catch { return null; }
        }

        void BuildUi()
        {
            ClientSize = new Size(ClientSize.Width, ClientSize.Height + 8);        // the title row is 60 px high (the Buy me a coffee picture is 60 x 60)
            const AnchorStyles TL = AnchorStyles.Top | AnchorStyles.Left;
            const AnchorStyles TR = AnchorStyles.Top | AnchorStyles.Right;
            const AnchorStyles TLR = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            // the two boxes of the top area: Port settings (left) and Target computer (right), both 84 px high
            var gbPort = new GroupBox { Text = "Port settings", Left = 10, Top = 0, Width = 340, Height = 84, Anchor = TL };
            var lblPort = new Label { Text = "Port:", Left = 8, Top = 24, Width = 32 };
            comboPorts = new LockCombo { Left = 44, Top = 20, Width = 100, DropDownStyle = ComboBoxStyle.DropDownList };
            led = new LedLabel { Left = 24, Top = 48, Width = 74, Height = 26 };
            btnTest = new Button { Text = "TEST", Left = 102, Top = 48, Width = 42, Height = 28, UseCompatibleTextRendering = false, FlatStyle = FlatStyle.Standard, Font = new Font("Segoe UI", 8F, FontStyle.Regular), Padding = new Padding(0), TextAlign = ContentAlignment.MiddleCenter, Anchor = TL };
            btnOpen = Btn("Open port", Icons.Plug(), 156, 16, 85, 28, TL);
            btnRefresh = Btn("Refresh", Icons.Refresh(), 248, 16, 85, 28, TL);
            btnFlush = Btn("Flush", Icons.Flush(), 156, 48, 85, 28, TL);
            btnReset = Btn("Reset port", Icons.Cross(), 248, 48, 85, 28, TL);
            btnFlush.Enabled = btnReset.Enabled = btnTest.Enabled = false;
            gbPort.Controls.AddRange(new Control[] { lblPort, comboPorts, led, btnTest, btnOpen, btnRefresh, btnFlush, btnReset });
            var gbTarget = new GroupBox { Text = "Target computer", Left = 354, Top = 0, Width = 356, Height = 84, Anchor = TR };
            radio1500 = new LockRadio { Text = "Sharp PC-1500", Left = 8, Top = 20, Width = 100 };
            radio1600 = new LockRadio { Text = "Sharp PC-1600", Left = 126, Top = 20, Width = 100, Checked = true };
            radioOther = new LockRadio { Text = "", Left = 238, Top = 20, Width = 20 };
            comboSharp = new LockCombo { Left = 258, Top = 21, Width = 92, DropDownStyle = ComboBoxStyle.DropDown, Enabled = false };
            comboSharp.Items.AddRange(new object[] { "PC-1350", "PC-1360", "PC-E500", "PC-E500S", "PC-1450..75", "Sharp other" });
            comboSharp.Text = "Sharp other";
            // the box only shows the placeholder or a chosen item: typing is not possible
            comboSharp.KeyPress += delegate (object sn, KeyPressEventArgs ke) { ke.Handled = true; };
            comboSharp.KeyDown += delegate (object sn, KeyEventArgs ke) { if (ke.KeyCode != Keys.Down && ke.KeyCode != Keys.Up && ke.KeyCode != Keys.F4 && ke.KeyCode != Keys.Tab) { ke.Handled = true; ke.SuppressKeyPress = true; } };
            comboSharp.TextChanged += delegate { if (comboSharp.SelectedIndex < 0 && comboSharp.Text != "Sharp other") comboSharp.Text = "Sharp other"; };
            comboSharp.SelectedIndexChanged += delegate { if (radioOther.Checked) SetTarget(); };
            radioFx = new LockRadio { Text = "Casio FX-850..870", Left = 8, Top = 50, Width = 112 };
            radioTrs = new LockRadio { Text = "NEC/TRS-80", Left = 126, Top = 50, Width = 100 };
            radioUart = new LockRadio { Text = "UART other", Left = 238, Top = 50, Width = 112 };
            gbTarget.Controls.AddRange(new Control[] { radio1500, radio1600, radioOther, comboSharp, radioFx, radioTrs, radioUart });

            banner = new BannerPanel { Left = 10, Top = 88, Width = 700, Height = 132, Anchor = TLR };

            var gb = new GroupBox { Text = "Transmission mode", Left = 10, Top = 226, Width = 700, Height = 58, Anchor = TLR };
            radioSoft = new LockRadio { Text = "Software Serial", Checked = true };
            radioRs = new LockRadio { Text = "RS-232 serial" };
            radioSio = new LockRadio { Text = "TTL-UART", Enabled = false };
            var lblBaud = new Label { Text = "Baud:" };
            comboBaud = new LockCombo { Width = 56, DropDownStyle = ComboBoxStyle.DropDownList };
            foreach (int b in new[] { 300, 600, 1200, 2400, 4800, 9600, 19200 }) comboBaud.Items.Add(b.ToString());
            comboBaud.SelectedItem = "9600";
            var lblDelay = new Label { Text = "Delay:" };
            numDelay = new LockNumeric { Width = 42, Minimum = 0, Maximum = 5000, Value = 100, Increment = 50 };
            var lblEol = new Label { Text = "EOL:" };
            comboEol = new LockCombo { Width = 50, DropDownStyle = ComboBoxStyle.DropDownList };
            comboEol.Items.AddRange(new object[] { "CR", "CR+LF", "LF" });
            comboEol.SelectedIndex = 1;
            comboEol.DrawMode = DrawMode.OwnerDrawFixed;                  // CR alone or LF alone: red text where the machine expects CR+LF
            comboEol.DrawItem += delegate (object sn, DrawItemEventArgs de)
            {
                if (de.Index < 0) return;
                bool hi = (de.State & DrawItemState.Selected) != 0 && (de.State & DrawItemState.ComboBoxEdit) == 0;
                bool bad = EolSuspect(de.Index);
                de.DrawBackground();
                Color c = bad ? (hi ? Color.FromArgb(255, 170, 170) : Color.Red) : de.ForeColor;
                TextRenderer.DrawText(de.Graphics, (string)comboEol.Items[de.Index], de.Font, de.Bounds, c, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                de.DrawFocusRectangle();
            };
            comboEol.SelectedIndexChanged += delegate { comboEol.Invalidate(); };
            var lblFlow = new Label { Text = "Flow:" };
            comboFlow = new LockCombo { Width = 74, DropDownStyle = ComboBoxStyle.DropDownList };
            comboFlow.Items.AddRange(new object[] { "None", "XON/XOFF", "RTS/CTS", "DSR/DTR" });
            comboFlow.SelectedIndex = 1;       // XON/XOFF, as it was fixed before
            foreach (RadioButton rb in new[] { radioSoft, radioRs, radioSio }) rb.AutoSize = true;   // identical height and baseline
            foreach (Label l in new[] { lblBaud, lblDelay, lblEol, lblFlow }) l.AutoSize = true;
            // one row, evenly spread over the box: a label sits right next to its field, the remaining
            // space is divided into equal gaps between the groups (radio, radio, radio, Baud, Delay, EOL, Flow)
            var groups = new Control[][] {
                new Control[] { radioSoft }, new Control[] { radioRs }, new Control[] { radioSio },
                new Control[] { lblBaud, comboBaud }, new Control[] { lblDelay, numDelay },
                new Control[] { lblEol, comboEol }, new Control[] { lblFlow, comboFlow } };
            Func<Control, int> wid = c => c is RadioButton || c is Label ? c.PreferredSize.Width : c.Width;
            int used = 0;
            foreach (var g in groups) foreach (Control c in g) used += wid(c) + (c is Label ? 1 : 0);
            int left = 8, right = gb.Width - 8;                      // the last field ends exactly at the right edge (like the Open button below)
            int gap = Math.Max(0, (right - left - used) / (groups.Length - 1));
            int x = left;
            foreach (var g in groups)
            {
                if (g == groups[groups.Length - 1])             // last group flush with the right edge
                {
                    int lastW = 0;
                    foreach (Control c in g) lastW += wid(c) + (c is Label ? 1 : 0);
                    x = right - lastW;
                }
                foreach (Control c in g)
                {
                    c.Left = x;
                    c.Top = c is RadioButton ? 24 : c is Label ? 27 : 23;
                    x += wid(c) + (c is Label ? 1 : 0);
                }
                x += gap;
            }
            comboBaud.Left -= 5; comboBaud.Width += 5;       // 115200 was cut off
            gb.Controls.AddRange(new Control[] { radioSoft, radioRs, radioSio, lblBaud, comboBaud, lblDelay, numDelay, lblEol, comboEol, lblFlow, comboFlow });

            var lblDir = new Label { Text = "Folder:", Left = 10, Top = 298, Width = 50 };
            txtDir = new TextBox
            {
                Left = 65, Top = 294, Width = 405, Anchor = TLR,
                Text = locs[1]
            };
            btnLocations = Btn("Folders", Icons.Folder(), 474, 291, 76, 28, TR);
            btnBrowse = Btn("Browse", Icons.Folder(), 554, 291, 76, 28, TR);
            btnOpenDir = Btn("Open", Icons.Folder(), 634, 291, 76, 28, TR);

            var gbInfo = new GroupBox { Text = "Info", Left = 10, Top = 322, Width = 700, Height = 84, Anchor = TLR };
            lblHint = new TextBox { Left = 8, Top = 14, Width = 684, Height = 67, Anchor = TLR, Multiline = true, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = SystemColors.Control, TabStop = false, ScrollBars = ScrollBars.None };
            gbInfo.Controls.Add(lblHint);

            var gbFiles = new GroupBox { Text = "Files", Left = 10, Top = 408, Width = 565, Height = 148, Anchor = TLR };   // only the list; the row of buttons below it ends with the Terminal button
            lstFiles = new ListBox { Left = 6, Top = 16, Width = 553, Height = 126, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            lstFiles.DrawMode = DrawMode.OwnerDrawFixed;          // the row of the file being sent has a red background
            lstFiles.DrawItem += delegate (object sn, DrawItemEventArgs de)
            {
                if (de.Index < 0 || de.Index >= lstFiles.Items.Count) return;
                string nm = (string)lstFiles.Items[de.Index];
                bool tx = sendingPath != null && string.Equals(Path.Combine(txtDir.Text, nm), sendingPath, StringComparison.OrdinalIgnoreCase);
                if (tx)
                {
                    using (var b = new SolidBrush(Color.FromArgb(220, 40, 40))) de.Graphics.FillRectangle(b, de.Bounds);
                    TextRenderer.DrawText(de.Graphics, nm, de.Font, de.Bounds, Color.White, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                }
                else
                {
                    de.DrawBackground();
                    TextRenderer.DrawText(de.Graphics, nm, de.Font, de.Bounds, de.ForeColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                    de.DrawFocusRectangle();
                }
            };
            chkEof = new LockCheck { Text = "Add EOF marker", Left = 198, Top = 562, Width = 115, Checked = true };
            chkAuto = new LockCheck { Text = "Auto token", Left = 316, Top = 562, Width = 86, Checked = true };
            chkSkipRem = new LockCheck { Text = "Skip REM", Left = 402, Top = 562, Width = 82, Checked = false };
            btnAbort = Btn("Abort", Icons.Cross(), 485, 560, 90, 24, TR);
            btnConvert = Btn("Convert", Icons.File(), 104, 560, 90, 24, TL);
            btnView = Btn("WAV", Icons.Note(), 10, 560, 90, 24, TL);
            btnView.Enabled = false;
            // like the Convert button (note at the left edge, the text at the right), but the caption is centred in the free space beside the note
            // (the icon takes about 24 px at the left)
            int capW = TextRenderer.MeasureText("WAV", btnView.Font).Width;
            btnView.Padding = new Padding(0, 0, Math.Max(6, (btnView.Width - 24 - capW) / 2), 0);
            gbFiles.Controls.Add(lstFiles);
            // hovering over a file name: a tip with the size, dates and type of the file
            lstFiles.MouseMove += delegate (object sn, MouseEventArgs me)
            {
                if (fileMenuOpen) return;
                int i = lstFiles.IndexFromPoint(me.Location);
                if (i == tipIndex) return;
                tipIndex = i;
                tips.Hide(lstFiles);
                tips.SetToolTip(lstFiles, i >= 0 && i < lstFiles.Items.Count ? FileInfoText((string)lstFiles.Items[i]) : ListTipDefault);
            };
            lstFiles.MouseLeave += delegate { tipIndex = -2; tips.SetToolTip(lstFiles, ListTipDefault); };
            // a file can be dragged out of the list to the desktop or to Explorer (copied)
            lstFiles.MouseDown += delegate (object sn, MouseEventArgs me)
            {
                dragIndex = me.Button == MouseButtons.Left ? lstFiles.IndexFromPoint(me.Location) : -1;
                dragStart = me.Location;
                if (me.Button == MouseButtons.Right) { int i = lstFiles.IndexFromPoint(me.Location); if (i >= 0) lstFiles.SelectedIndex = i; }
            };
            lstFiles.MouseUp += delegate { dragIndex = -1; };
            lstFiles.MouseMove += delegate (object sn, MouseEventArgs me)
            {
                if (me.Button != MouseButtons.Left || dragIndex < 0 || dragIndex >= lstFiles.Items.Count) return;
                Size ds = SystemInformation.DragSize;
                if (Math.Abs(me.X - dragStart.X) < ds.Width && Math.Abs(me.Y - dragStart.Y) < ds.Height) return;
                string p = Path.Combine(txtDir.Text, (string)lstFiles.Items[dragIndex]);
                dragIndex = -1;
                if (!File.Exists(p)) return;
                try { lstFiles.DoDragDrop(new DataObject(DataFormats.FileDrop, new[] { p }), DragDropEffects.Copy); } catch { }
            };
            // right mouse button: View, Edit, Delete, Rename, Check Basic
            var fileMenu = new ContextMenuStrip();
            var miView = new ToolStripMenuItem("View", null, delegate { ViewSelected(); });
            var miEdit = new ToolStripMenuItem("Edit", null, delegate { EditSelected(); });
            var miDelete = new ToolStripMenuItem("Delete", null, delegate { DeleteSelected(); });
            var miRename = new ToolStripMenuItem("Rename", null, delegate { RenameSelected(); });
            var miCheck = new ToolStripMenuItem("Check Basic", null, delegate { CheckBasicSelected(); });
            var miRenum = new ToolStripMenuItem("Renumber", null, delegate { RenumberSelected(); });
            var miNoRem = new ToolStripMenuItem("Remove REM", null, delegate { RemoveRemSelected(); });
            var miCompare = new ToolStripMenuItem("Compare", null, delegate { CompareSelected(); });
            var miPlay = new ToolStripMenuItem("Play", null, delegate { PlaySelected(); });
            fileMenu.Items.AddRange(new ToolStripItem[] { miView, miEdit, miDelete, miRename, new ToolStripSeparator(), miCheck, miRenum, miNoRem, miCompare, miPlay });
            fileMenu.Opening += delegate (object sn, System.ComponentModel.CancelEventArgs ce)
            {
                if (lstFiles.SelectedItem == null || lstFiles.IndexFromPoint(lstFiles.PointToClient(Cursor.Position)) < 0) { ce.Cancel = true; return; }
                string ext = Path.GetExtension((string)lstFiles.SelectedItem).ToLowerInvariant();
                bool wav = ext == ".wav";                                  // a recording: no View, Edit, Check Basic, Renumber, Remove REM; Compare and Play work
                miView.Enabled = !wav;
                miCheck.Enabled = !wav;
                miPlay.Enabled = wav;
                miEdit.Enabled = ext != ".img" && ext != ".bin" && !wav;          // a program image is edited as BASIC text (Convert first)
                miDelete.Enabled = !sending;
                miRenum.Enabled = miNoRem.Enabled = ext == ".bas" || ext == ".txt";       // BASIC text only
            };
            fileMenu.Opened += delegate { fileMenuOpen = true; tips.Hide(lstFiles); tips.Active = false; };     // no tip while the menu is shown
            fileMenu.Closed += delegate { fileMenuOpen = false; tipIndex = -2; tips.Active = true; };
            lstFiles.ContextMenuStrip = fileMenu;
            // files dragged from the desktop / Explorer are copied to the current folder
            foreach (Control dc in new Control[] { lstFiles, gbFiles })
            {
                dc.AllowDrop = true;
                dc.DragEnter += (s2, e2) => { e2.Effect = e2.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; };
                dc.DragDrop += (s2, e2) => DropFiles(e2.Data.GetData(DataFormats.FileDrop) as string[]);
            }
            btnSend = Btn("Send selected", Icons.Plane(), 585, 408, 125, 32, TR);
            btnSendOther = Btn("Send file...", Icons.File(), 585, 444, 125, 32, TR);
            btnRefreshFiles = Btn("Refresh list", Icons.Refresh(), 585, 480, 125, 32, TR);
            btnDelete = Btn("Delete file", Icons.Cross(), 585, 516, 125, 32, TR);
            btnTerminal = Btn("Terminal", Icons.Terminal(), 585, 552, 125, 32, TR);

            var gbLog = new GroupBox
            {
                Text = "Log", Left = 10, Top = 588, Width = 700, Height = 118,
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
            };
            txtLog = new RichTextBox
            {
                Left = 6, Top = 14, Width = 688, Height = 100,
                ScrollBars = RichTextBoxScrollBars.Vertical, ReadOnly = true, Font = new Font("Consolas", 9F), BackColor = SystemColors.Window, DetectUrls = false,
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
            };
            gbLog.Controls.Add(txtLog);
            // right mouse button in the log: Clear log, Copy log
            var logMenu = new ContextMenuStrip();
            var miClear = new ToolStripMenuItem("Clear log");
            var miCopy = new ToolStripMenuItem("Copy log");
            miClear.Click += delegate { txtLog.Clear(); progStart = 0; progEnd = -1; };       // a running progress line is not redrawn
            miCopy.Click += delegate
            {
                try { if (txtLog.TextLength > 0) Clipboard.SetText(txtLog.Text); } catch (Exception ex) { Log("Copy error: " + ex.Message); }
            };
            logMenu.Items.Add(miClear);
            logMenu.Items.Add(miCopy);
            logMenu.Opening += delegate { miCopy.Enabled = miClear.Enabled = txtLog.TextLength > 0; };
            txtLog.ContextMenuStrip = logMenu;

            Controls.AddRange(new Control[] { gbPort, gbTarget, banner, gb,
                lblDir, txtDir, btnLocations, btnBrowse, btnOpenDir, gbInfo, gbFiles, btnView, chkEof, chkAuto, chkSkipRem, btnConvert, btnAbort, btnSend, btnSendOther, btnRefreshFiles, btnDelete, btnTerminal, gbLog });

            // title row: everything else moves down by one row (40 px)
            foreach (Control c in Controls) c.Top += 60;
            {
                const string titleText = "FILE TRANSFER TOOL FOR PC AND SHARP POCKET COMPUTERS";
                var titleFont = new Font("Segoe UI", 11F, FontStyle.Bold);
                Size ts = TextRenderer.MeasureText(titleText, titleFont);
                int x0 = (ClientSize.Width - (32 + 8 + ts.Width)) / 2 - 22;
                Controls.Add(new PictureBox { Image = Icons.Comet(32), SizeMode = PictureBoxSizeMode.AutoSize, Left = x0, Top = 14, Anchor = AnchorStyles.Top });
                Controls.Add(new Label
                {
                    Text = titleText, AutoSize = true, Font = titleFont, ForeColor = Color.FromArgb(30, 70, 120),
                    Left = x0 + 40, Top = 14 + (32 - ts.Height) / 2, Anchor = AnchorStyles.Top
                });
                // "by Alex F4VTS" to the right of the title, a round "?" button (About) on the left
                var byFont = new Font("Segoe UI", 9F, FontStyle.Bold | FontStyle.Italic);
                Size byS = TextRenderer.MeasureText("by Alex F4VTS", byFont);
                // Buy me a coffee: the picture img\buymeacoffee.jpg (looked for next to the program and in the folders above it); without the file a text link
                Image bmcImg = LoadBmcImage();
                int bmcH = bmcImg != null ? bmcImg.Height : 58;          // the picture 58 x 58 px, not scaled
                int bmcW;
                if (bmcImg != null) bmcW = bmcImg.Width;
                else bmcW = bmcH;          // (the picture is built into the program; this is only a safety net)
                // the group icon + title + "by Alex F4VTS" + the picture ends at the right edge of the window (10 px margin)
                x0 = ClientSize.Width - 10 - (32 + 8 + ts.Width + 10 + byS.Width + 12 + bmcW);
                foreach (Control c0 in Controls)
                {
                    if (c0 is PictureBox) { c0.Left = x0; c0.Anchor = AnchorStyles.Top | AnchorStyles.Right; }
                    else if (c0 is Label && ((Label)c0).Text == titleText) { c0.Left = x0 + 40; c0.Anchor = AnchorStyles.Top | AnchorStyles.Right; }
                }
                var by = new Label { Text = "by Alex F4VTS", AutoSize = true, Font = byFont, ForeColor = Color.FromArgb(90, 110, 140), Left = x0 + 40 + ts.Width + 10, Top = 14 + (32 - ts.Height) / 2 + 3, Anchor = AnchorStyles.Top | AnchorStyles.Right };
                Controls.Add(by);
                Control bmc = new PictureBox { Image = bmcImg, SizeMode = PictureBoxSizeMode.Normal, Left = by.Left + byS.Width + 12, Top = 4, Width = bmcW, Height = bmcH, Anchor = AnchorStyles.Top | AnchorStyles.Right, Cursor = Cursors.Hand };
                bmc.Click += delegate { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://www.buymeacoffee.com/Alex_F4VTS") { UseShellExecute = true }); } catch (Exception ex) { Log("Cannot open the page: " + ex.Message); } };
                tips.SetToolTip(bmc, "Support the author: Buy me a coffee (opens the page buymeacoffee.com/Alex_F4VTS in the browser).");
                Controls.Add(bmc);
                var about = new RoundHelpButton { Left = 10, Top = 19, Width = 21, Height = 21, Anchor = AnchorStyles.Top | AnchorStyles.Left };
                about.Click += delegate { using (var d = new AboutForm()) d.ShowDialog(this); };
                tips.SetToolTip(about, "About SerComet: author, license and project page.");
                Controls.Add(about);
            }


            btnRefresh.Click += delegate { RefreshPorts(); };
            btnFlush.Click += delegate { FlushBuffers(); };
            btnReset.Click += delegate { ResetPort(); };
            btnOpen.Click += delegate { TogglePort(); };
            btnTest.Click += delegate { TestPort(); };
            radioSoft.CheckedChanged += delegate { UpdateMode(); };
            radioRs.CheckedChanged += delegate { UpdateMode(); };
            radioSio.CheckedChanged += delegate { UpdateMode(); };
            radio1500.CheckedChanged += delegate { if (radio1500.Checked) SetTarget(); };
            radio1600.CheckedChanged += delegate { if (radio1600.Checked) SetTarget(); };
            radioOther.CheckedChanged += delegate { if (radioOther.Checked) SetTarget(); };
            radioFx.CheckedChanged += delegate { if (radioFx.Checked) SetTarget(); };
            radioTrs.CheckedChanged += delegate { if (radioTrs.Checked) SetTarget(); };
            radioUart.CheckedChanged += delegate { if (radioUart.Checked) SetTarget(); };
            comboBaud.SelectedIndexChanged += delegate { if (comboBaud.Enabled && comboBaud.SelectedItem != null) { if (rs232) { savedBaud = (string)comboBaud.SelectedItem; numDelay.Value = Math.Max(numDelay.Minimum, Math.Min(numDelay.Maximum, DefaultDelay(int.Parse(savedBaud), curModel))); } else savedSoftBaud = (string)comboBaud.SelectedItem; lblHint.Text = HintText(); } ApplyBaud(); };
            btnBrowse.Click += delegate
            {
                using (var d = new FolderBrowserDialog())
                {
                    d.SelectedPath = txtDir.Text;
                    if (d.ShowDialog() == DialogResult.OK) { txtDir.Text = d.SelectedPath; RefreshFiles(); }
                }
            };
            btnOpenDir.Click += delegate
            {
                try { Directory.CreateDirectory(txtDir.Text); System.Diagnostics.Process.Start(txtDir.Text); }
                catch (Exception ex) { Log("Error: " + ex.Message); }
            };
            btnRefreshFiles.Click += delegate { RefreshFiles(); };
            btnTerminal.Click += delegate { OpenTerminal(); };
            btnAbort.Click += delegate { Abort(); };
            btnConvert.Click += delegate { ConvertSelected(); };
            btnView.Click += delegate { WavSelected(); };
            lstFiles.MouseDoubleClick += delegate (object sn, MouseEventArgs me)      // double click on a file name = View
            {
                int i = lstFiles.IndexFromPoint(me.Location);
                if (i < 0 || i >= lstFiles.Items.Count) return;
                lstFiles.SelectedIndex = i;
                if (Path.GetExtension((string)lstFiles.SelectedItem).Equals(".wav", StringComparison.OrdinalIgnoreCase)) PlaySelected(); else ViewSelected();
            };
            lstFiles.SelectedIndexChanged += delegate { SelectionChanged(); };
            btnSend.Enabled = false;
            btnSendOther.Enabled = false;          // the port is closed at the start
            btnDelete.Enabled = false;
            btnConvert.Enabled = false;
            comboFlow.SelectedIndexChanged += delegate { ApplyFlow(); };
            btnLocations.Click += delegate { EditLocations(); };
            btnDelete.Click += delegate { DeleteSelected(); };
            chkAuto.CheckedChanged += delegate { RefreshFiles(); };
            btnSend.Click += delegate
            {
                if (lstFiles.SelectedItem == null) return;
                SendFile(Path.Combine(txtDir.Text, (string)lstFiles.SelectedItem));
            };
            btnSendOther.Click += delegate
            {
                using (var d = new OpenFileDialog())
                {
                    d.Filter = rs232 ? "BASIC text (*.bas;*.txt)|*.bas;*.txt|All files|*.*" : chkAuto.Checked ? "PC-1600 files (*.img;*.bas;*.txt)|*.img;*.bas;*.txt|All files|*.*" : "Program images (*.img)|*.img|All files|*.*";
                    if (d.ShowDialog() == DialogResult.OK) SendFile(d.FileName);
                }
            };

            tips.SetToolTip(comboPorts, "Select a COM port, or the virtual COM port created by a USB adapter (FTDI)");
            tips.SetToolTip(btnRefresh, "Re-scan the list of available COM ports.");
            tips.SetToolTip(btnFlush, "Flush: discard the data waiting in the port buffers and in the receive buffer. Use it before a transfer, after an interrupted transfer, and after the Sharp was switched on or reset (it may have sent garbage). Active when the port is open.");
            tips.SetToolTip(btnReset, "Reset port: close the port and open it again with the current settings, and clear the buffers. Active when the port is open.");
            tips.SetToolTip(btnOpen, "Open or close the selected COM port.");
            tips.SetToolTip(btnTest, "Port test: connect RXD with TXD and send test data at 300 to 115200 bps.");
            tips.SetToolTip(led, "Port status: green = open, dark = closed.");
            tips.SetToolTip(radio1500, "Target: Sharp PC-1500 (software serial Saver/Loader at 4800 baud, or RS-232 through the CE-158(X) interface).");
            tips.SetToolTip(radio1600, "Target: Sharp PC-1600 (software serial Saver/Loader or its RS-232 port).");
            tips.SetToolTip(radioOther, "Target: other Sharp pocket computers with the 15-pin connector (TTL-UART SIO). Choose the model in the box beside it.");
            tips.SetToolTip(comboSharp, "The Sharp model (15-pin connector): PC-1350, PC-1360, PC-E500, PC-E500S, PC-1450..75 or Sharp other. It sets the folder, the notes in Info and the WAV converter. Active when the Sharp radio button is on.");
            tips.SetToolTip(radioFx, "Casio FX-850P, FX-870P and similar.");
            tips.SetToolTip(radioTrs, "TRS-80 model 100, NEC-8201.");
            tips.SetToolTip(radioUart, "Other computers with UART interface TTL or RS-232.");
            tips.SetToolTip(radioSoft, "Software UART in the Sharp (machine-language Saver/Loader), binary .img files. PC-1600: speed 1200 to 19200 (as chosen in the SL installer); PC-1500: 4800 baud.");
            tips.SetToolTip(radioRs, "Classic RS-232 text transfer, like a terminal program. The baud rate is selectable.");
            tips.SetToolTip(radioSio, "TTL-UART SIO text transfer through the 15-pin connector. The baud rate is selectable.");
            tips.SetToolTip(comboBaud, "Baud rate (8N1). Fixed and greyed out for Software Serial, selectable for the text modes.");
            tips.SetToolTip(numDelay, "Line delay: pause after each line sent from a text file, in ms (the Sharp needs time to store a line).\r\n" +
                "It is set automatically when you choose the baud rate (tune it by testing, raise it if lines are lost or garbled):\r\n" +
                "  300 to 2400 bps: 20 ms   4800: 50   9600: 100   19200: 125   38400 and 57600: 140   115200: 145\r\n" +
                "  PC-1500 and NEC / TRS-80: twice these values.\r\n" +
                "Note: a published PC-1600 setup (9600 baud, XON/XOFF) uses a delay of 2/60 s per character and 0 per line; this program delays per line only.");
            tips.SetToolTip(txtDir, "Folder where received files are saved and listed from.");
            tips.SetToolTip(btnBrowse, "Choose another folder.");
            tips.SetToolTip(btnOpenDir, "Open the folder in Windows Explorer.");
            tips.SetToolTip(lstFiles, ListTipDefault);
            tips.SetToolTip(btnSend, "Send the selected file to the Sharp. Start the Loader / receiver on the Sharp first.");
            tips.SetToolTip(btnSendOther, "Pick any file from the disk and send it to the Sharp.");
            tips.SetToolTip(btnRefreshFiles, "Re-read the folder and refresh the file list.");
            tips.SetToolTip(btnTerminal, "Open a terminal window (like TeraTerm): typed characters are sent, received data is shown. Text modes only.");
            tips.SetToolTip(comboEol, "Line end sent after every line of a text file: CR, CR+LF (TeraTerm style) or LF. Red = CR alone or LF alone is probably a wrong choice for the Sharp and Casio computers (they expect CR+LF); TRS-80 / NEC and other UART computers can use any of them.");
            tips.SetToolTip(btnView, "WAV: PC-1500, PC-1600, PC-1350, PC-1360 and PC-E500 (the last three under Sharp (other)). A BASIC text file (.bas / .txt) is converted to a tape recording (.wav; the PC-1600 asks for MODE 0 = CE-1600P or MODE 1 = CE-150), a .wav recording is converted back to BASIC text.");
            tips.SetToolTip(chkEof, "Add the end-of-file marker. Text modes: after the file (and the dummy line) send the end-of-file character 1Ah (Ctrl-Z) so that the Sharp finishes the reception (1Ah is the end-of-file code of an ASCII file). Switch it off if the Sharp reports an error.");
            tips.SetToolTip(btnAbort, "Abort the transmission at once: stops sending the file and discards data waiting to be sent or received.");
            tips.SetToolTip(btnConvert, "Software UART mode only. Convert the selected file: BASIC text (.bas, .txt) to a program image (.img), or a program image (.img, or a .bin that is a BASIC program) to BASIC text (.bas).");
            tips.SetToolTip(chkSkipRem, "Skip REM: lines that contain only a comment (REM) are not sent to the pocket computer, to save its memory. A REM line that a GOTO / GOSUB points to is kept. Off by default. Not applied to ready program images (.img).");
            tips.SetToolTip(chkAuto, "Software UART. ON: the converter is active. A program received from the Sharp is saved as BASIC text (.bas) and BASIC files are tokenized automatically when sent (the image is not kept). OFF: only program images (.img) are used, no automatic conversion (use the Convert button).");
            tips.SetToolTip(btnLocations, "Set the default folders of the programs for PC-1500, PC-1600 and the other models.");
            tips.SetToolTip(btnDelete, "Delete the selected file from the folder (after a confirmation).");
            tips.SetToolTip(comboFlow, "Flow control for the text modes: None, XON/XOFF (default), RTS/CTS or DSR/DTR (the PC waits while the Sharp does not hold DSR). Not used by Software Serial.");
            tips.SetToolTip(txtLog, "Log of transfers and messages.");

            timer = new Timer { Interval = 100 };
            timer.Tick += TimerTick;
            timer.Start();
            // the arrows flash with a frequency that depends on the baud rate (a fast timer: the 100 ms tick is too slow for 6 to 8 Hz)
            blinkTimer = new Timer { Interval = 40 };
            blinkTimer.Tick += BlinkTick;
            blinkTimer.Start();
        }

        // target computer: default folder and the available transmission modes
        //   0 PC-1500, 1 PC-1600 (Software Serial or RS-232), 2 Other Sharp (15-pin, TTL-UART), 3 Casio FX-850/870 (TTL-UART),
        //   4 TRS-80 / NEC (RS-232), 5 other UART computer (RS-232 or TTL-UART)
        void SetTarget()
        {
            int nm = radio1500.Checked ? 0 : radio1600.Checked ? 1 : radioOther.Checked ? 2 : radioFx.Checked ? 3 : radioTrs.Checked ? 4 : 5;
            int prof = nm < 2 ? nm : nm == 2 ? 2 + (comboSharp.SelectedIndex < 0 ? 5 : comboSharp.SelectedIndex) : nm + 5;
            if (curProf >= 0 && prof != curProf) CaptureUi(curProf);     // keep the settings of the computer we leave
            curModel = nm;
            curProf = prof;
            comboSharp.Enabled = nm == 2;
            radioSoft.Enabled = nm == 0;          // Software Serial: PC-1500 only (blocked for the PC-1600 for the time being)
            radioRs.Enabled = nm != 2 && nm != 3;
            radioSio.Enabled = nm == 2 || nm == 3 || nm == 5;
            radioRs.Text = "RS-232 serial";
            comboEol.Invalidate();
            banner.ModelName = ModelCaptions[prof];
            banner.Model = nm;
            banner.Prof = prof;
            banner.Invalidate();
            ApplyCfg(prof);
            SetDefaultDir(cfg[prof].Dir ?? locs[prof]);
            UpdateMode();
        }

        static readonly string[] ModelCaptions = { "Sharp PC-1500", "Sharp PC-1600", "Sharp PC-1350", "Sharp PC-1360", "Sharp PC-E500", "Sharp PC-E500S", "Sharp PC-1450..75", "Sharp with 15-pin connector", "Casio FX-850P / FX-870P", "TRS-80 / NEC PC-8201", "Other UART computer" };

        string HintText()
        {
            if (curModel == 3)
                return "Casio FX-850P / FX-870P: TTL levels 0 / +5 V (USB-TTL adapter, never RS-232 levels), 300 to 4800 bps.\r\n" +
                       "OPEN \"COM0:6,N,8,1,N,N,N,N,N\" AS #1 (6 = 4800, 5 = 2400, 4 = 1200, 3 = 600, 2 = 300); PRINT #1,..; INPUT #1,A$; LINE INPUT #1,A$; CLOSE.\r\n" +
                       "Program (text): LOAD \"COM0:6,N,8,1,N,N,N,N,N\" = Send.";
            if (curModel == 4)
                return "TRS-80 Model 100 / NEC PC-8201: DB-25 RS-232C, null-modem cable. OPEN \"COM:88N1E\" FOR OUTPUT AS 1 / FOR INPUT AS 2 (8 = 9600, 3 = 300, 9 = 19200).\r\n" +
                       "PRINT #1,..; LINE INPUT #2,A$; A$=INPUT$(n,2); CLOSE 1,2. Program (ASCII): LOAD \"COM:48N1E\" = Send.\r\n" +
                       "LOAD tokenizes while receiving: use 600 to 1200 bps; SAVE is stable up to 19200.";
            if (curModel == 5 && radioSio.Checked)
                return "UART Other, TTL: logic levels 0 / +5 V; 3.3 V CMOS devices work too, and so do Arduino and similar boards. Connect through a USB-TTL adapter and match its voltage (3.3 V or 5 V) to the device.\r\n" +
                       "TX of one side goes to RX of the other, and the grounds are joined. Never connect RS-232 levels to a TTL input.\r\n" +
                       "Text transfer (.bas) with the baud rate (up to 115200), flow control, line end and line delay set here (8N1). Use the Terminal for tests.";
            if (curModel == 5)
                return "UART Other, RS-232: the signal swings between a negative and a positive voltage (about -5 to -15 V and +5 to +15 V). A device with 0 / +5 V logic needs a level converter (for example MAX232 / MAX3232) between it and the RS-232 adapter.\r\n" +
                       "Connect through a USB-RS232 adapter with a null-modem cable (TX and RX crossed).\r\n" +
                       "Text transfer (.bas) with the baud rate (up to 115200), flow control, line end and line delay set here (8N1). Use the Terminal for tests.";
            if (curModel == 2 && curProf == 2)
                return "Sharp PC-1350 (15-pin connector, TTL-UART SIO via a USB/COM adapter, 1200 bps).\r\n" +
                       "OPEN \"1200,N,8,1,A,C,&1A\", then LOAD (SerComet: Send). Text transfer (.bas).\r\n" +
                       "WAV: the tokenized program as a tape recording (CLOAD / CSAVE, ID 70H); a BASIC text file is converted to a recording and back.";
            if (curModel == 2 && curProf == 3)
                return "Sharp PC-1360 (15-pin connector, TTL-UART SIO via a USB/COM adapter, 1200 bps).\r\n" +
                       "OPEN \"1200,N,8,1,A,C,&1A\", then LOAD (SerComet: Send). After MERGE of several programs the transmission by SAVE / LOAD may not work correctly (see the manual).\r\n" +
                       "WAV: the tokenized program as a tape recording (CLOAD / CSAVE, ID 72H, two byte tokens); a BASIC text file is converted to a recording and back.";
            if (curModel == 2 && (curProf == 4 || curProf == 5))
                return "Sharp " + (curProf == 4 ? "PC-E500" : "PC-E500S (structured BASIC: SWITCH, WHILE, REPEAT ...)") + " (15-pin connector, TTL-UART SIO, up to 9600 bps).\r\n" +
                       "OPEN \"9600,N,8,1,A,L,&H1A,N,N\" : CLOSE, then LOAD / SAVE (COM is the default device); SAVE [\"name\"[,A]]: on COM the program is always sent as ASCII text. Text transfer (.bas).\r\n" +
                       "WAV: BASIC text is written as an ASCII tape file (SAVE \"CAS:\"); recordings of SAVE \"CAS:\" and CSAVE are read back as BASIC text.";
            if (curModel == 2 && curProf == 6)
                return "Sharp PC-1450 to PC-1475 (15-pin connector, TTL-UART SIO via a USB/COM adapter).\r\n" +
                       "Tokenization and tape format is not supported. You can use the usual UART transfer of Basic programs.";
            if (curModel == 2)
                return "Other Sharp pocket computer with the 15-pin connector (TTL-UART SIO via a USB/COM adapter).\r\n" +
                       "Tokenization and tape format is not supported. You can use the usual UART transfer of Basic programs.";
            if (radioSio.Checked)
                return "Compatible: PC-1350, PC-1360, PC-1450..75, PC-E500..650, PC-U6000, PC-2500 (15-pin, TTL-UART SIO via a USB/COM adapter).\r\n" +
                       "PC-1350 / PC-1360: OPEN \"1200,N,8,1,A,C,&1A\", then LOAD (SerComet: Send).\r\n" +
                       "PC-E500(S): OPEN \"9600,N,8,1,A,L,&H1A,N,N\" : CLOSE, then LOAD / SAVE (COM is the default device).\r\n" +
                       "SAVE [\"name\"[,A]]: the name (up to 8 characters) is optional; on COM the program is always sent as ASCII text.\r\n" +
                       "Text transfer (.bas).";
            if (radio1500.Checked && radioRs.Checked)
                return "PC-1500 with CE-158(X): SETCOM 2400,8,N,1 (the same baud rate as in SerComet).\r\n" +
                       "Into the PC-1500 (SerComet: Send): SETDEV CI, then CLOADa.\r\n" +
                       "Text transfer (.bas).";
            if (radio1500.Checked)
                return "Software UART: 8N1 at 4800 baud (fixed), binary transfer (.img files). TX = PC7 (pin 10), RX = PB2 (pin 27) of the PC-1500.\r\nSEND: type NEW, then CALL &4100 (LOAD) on the PC-1500 first, then press Send (30 s to start). Addresses for a RAM starting at &4000 (the SL installer shows the others).";
            if (radioRs.Checked)
                return "Into the PC-1600 (Send): INIT\"COM1:\",512 / SETCOM\"COM1:\"," + savedBaud + " / LOAD\"COM1:\" (same baud rate as in SerComet).\r\n" +
                       "Text transfer (.bas).\r\n" +
                       "SETCOM\"COM1:\"," + savedBaud + ",8,N,1,X,N switches XON/XOFF on in the Sharp (the default Flow of SerComet).";
            return "Software UART: 8N1 at the speed chosen here and in the SL installer (9600 recommended), binary transfer (.img files).\r\nSEND: type CALL &C100 (LOAD) on the PC-1600 first, then press Send (30 s to start).";
        }

        static string DefaultLocation(int i)
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), new[] { "PC1500", "PC1600", "PC1350", "PC1360", "PCE500", "PCE500S", "PC1450", "SHARP_other", "Casio", "TRS-80", "UART" }[i]);
        }

        // ------------------------------------------------------------------ settings.ini
        // Saved when the program is closed and read at start: the port, the target computer and, for every computer,
        // the mode and all transmission settings and the folder; also the default folders (Folders window).
        // File: settings.ini next to the program if it is there (portable use), otherwise in %AppData%\SerComet.
        sealed class ModelCfg
        {
            public int Mode;                    // 0 Software UART, 1 RS-232, 2 TTL-UART
            public string Baud = "9600";        // text modes
            public string SoftBaud = "9600";    // Software UART
            public int Delay = 100, Eol = 1, Flow = 1;
            public bool Eof = true, Auto = true, Skip = false;
            public string Dir;                  // null = the default folder
        }

        // Line delay (ms) that goes with a baud rate: the pocket computer needs about P = 150 ms to tokenize and store a line, and its receive buffer
        // (about 48 characters) takes up the characters that arrive meanwhile, so the pause after a line is P minus the time of the buffer:
        //   delay = 150 - 480000 / baud   (rounded to 5 ms, at least 20 ms, at most 150 ms)
        //   300..2400: 20   4800: 50   9600: 100   19200: 125   38400: 140   57600: 140   115200: 145
        // The slow computers (PC-1500 and the NEC / TRS-80 machines, which always had twice the value) get twice that.
        // The value is set when the baud rate is chosen (and for a new target computer); a delay saved in settings.ini is kept.
        static int DefaultDelay(int baud, int model)
        {
            double d = 150.0 - 480000.0 / Math.Max(1, baud);
            int ms = (int)(Math.Round(d / 5.0) * 5.0);
            ms = Math.Max(20, Math.Min(150, ms));
            if (model == 0 || model == 4) ms *= 2;
            return ms;
        }

        readonly ModelCfg[] cfg = {
            new ModelCfg { Delay = DefaultDelay(9600, 0) },                                                  // PC-1500
            new ModelCfg { Mode = 1, Baud = "4800", Delay = DefaultDelay(4800, 1) },                         // PC-1600
            new ModelCfg { Mode = 2, Baud = "1200", Delay = DefaultDelay(1200, 2) },                         // PC-1350
            new ModelCfg { Mode = 2, Baud = "1200", Delay = DefaultDelay(1200, 2) },                         // PC-1360
            new ModelCfg { Mode = 2, Delay = DefaultDelay(9600, 2) },                                        // PC-E500
            new ModelCfg { Mode = 2, Delay = DefaultDelay(9600, 2) },                                        // PC-E500S
            new ModelCfg { Mode = 2, Delay = DefaultDelay(9600, 2) },                                        // PC-1450..75
            new ModelCfg { Mode = 2, Delay = DefaultDelay(9600, 2) },                                        // Sharp other
            new ModelCfg { Mode = 2, Baud = "4800", Delay = DefaultDelay(4800, 3) },                         // Casio
            new ModelCfg { Mode = 1, Baud = "1200", Delay = DefaultDelay(1200, 4), Eol = 0 },                // TRS-80 / NEC
            new ModelCfg { Mode = 1, Delay = DefaultDelay(9600, 5) } };                                      // other UART
        int curModel = -1, startModel = 1;
        string startPort;
        static readonly string[] ModelNames = { "PC-1500", "PC-1600", "PC-1350", "PC-1360", "PC-E500", "PC-E500S", "PC-1450", "Sharp other", "FX-850/870", "TRS-80", "UART Other" };
        static readonly string[] GroupNames = { "PC-1500", "PC-1600", "Other Sharp" };
        static readonly string[] ModeNames = { "Software UART", "RS-232", "TTL-UART" };
        static readonly string[] EolNames = { "CR", "CR+LF", "LF" };
        static readonly string[] FlowNames = { "None", "XON/XOFF", "RTS/CTS", "DSR/DTR" };

        static string SettingsFile()
        {
            string local = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.ini");
            if (File.Exists(local)) return local;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SerComet", "settings.ini");
        }

        static int IndexOf(string[] names, string v, int def)
        {
            for (int i = 0; i < names.Length; i++) if (string.Equals(names[i], v, StringComparison.OrdinalIgnoreCase)) return i;
            return def;
        }

        void CaptureUi(int m)
        {
            if (m < 0 || m >= NProf) return;
            var c = cfg[m];
            c.Mode = radioSio.Checked ? 2 : radioRs.Checked ? 1 : 0;
            c.Baud = savedBaud;
            c.SoftBaud = savedSoftBaud;
            c.Delay = (int)numDelay.Value;
            c.Eol = comboEol.SelectedIndex;
            c.Flow = comboFlow.SelectedIndex;
            c.Eof = chkEof.Checked;
            c.Auto = chkAuto.Checked;
            c.Skip = chkSkipRem.Checked;
            c.Dir = txtDir.Text;
        }

        void ApplyCfg(int prof)
        {
            int m = GroupOf(prof);
            var c = cfg[prof];
            if (m == 1 && c.Mode == 0) { c.Mode = 1; c.Baud = "4800"; }      // PC-1600: the Software Serial is blocked; RS-232 at 4800 bps is the default
            if (m == 2 || m == 3 || (m == 5 && c.Mode == 2)) radioSio.Checked = true;
            else if (m == 4 || m == 5 || c.Mode == 1) radioRs.Checked = true;
            else radioSoft.Checked = true;
            savedBaud = Array.IndexOf(TextBaudsFor(m), c.Baud) >= 0 ? c.Baud : (m == 3 ? "4800" : "9600");
            savedSoftBaud = Array.IndexOf(SoftBauds, c.SoftBaud) >= 0 ? c.SoftBaud : "9600";
            numDelay.Value = Math.Max(numDelay.Minimum, Math.Min(numDelay.Maximum, c.Delay));
            comboEol.SelectedIndex = c.Eol >= 0 && c.Eol < 3 ? c.Eol : 1;
            comboFlow.SelectedIndex = c.Flow >= 0 && c.Flow < 4 ? c.Flow : 1;
            chkEof.Checked = c.Eof;
            chkAuto.Checked = c.Auto;
            chkSkipRem.Checked = c.Skip;
        }

        void LoadSettings()
        {
            try
            {
                string f = SettingsFile();
                if (!File.Exists(f)) return;                       // first start (or only the old locations.txt)
                var sec = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
                Dictionary<string, string> cur = null;
                foreach (string raw in File.ReadAllLines(f))
                {
                    string l = raw.Trim();
                    if (l.Length == 0 || l[0] == ';' || l[0] == '#') continue;
                    if (l[0] == '[' && l[l.Length - 1] == ']')
                    {
                        string n = l.Substring(1, l.Length - 2).Trim();
                        if (!sec.TryGetValue(n, out cur)) { cur = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); sec[n] = cur; }
                        continue;
                    }
                    int eq = l.IndexOf('=');
                    if (cur != null && eq > 0) cur[l.Substring(0, eq).Trim()] = l.Substring(eq + 1).Trim();
                }
                Func<string, string, string> get = (s1, k) =>
                {
                    Dictionary<string, string> d; string v;
                    return sec.TryGetValue(s1, out d) && d.TryGetValue(k, out v) && v.Length > 0 ? v : null;
                };
                startPort = get("General", "Port");
                string tg = get("General", "Target");
                startModel = string.Equals(tg, "Other Sharp", StringComparison.OrdinalIgnoreCase) ? 7 : IndexOf(ModelNames, tg, 1);
                for (int m = 0; m < NProf; m++)
                {
                    string d = get("Folders", ModelNames[m]);
                    if (d != null) locs[m] = d;
                    string n = ModelNames[m];
                    var c = cfg[m];
                    c.Mode = IndexOf(ModeNames, get(n, "Mode"), c.Mode);
                    c.Baud = get(n, "Baud") ?? c.Baud;
                    c.SoftBaud = get(n, "SoftBaud") ?? c.SoftBaud;
                    int v;
                    if (int.TryParse(get(n, "Delay"), out v)) c.Delay = v;
                    c.Eol = IndexOf(EolNames, get(n, "LineEnd"), c.Eol);
                    c.Flow = IndexOf(FlowNames, get(n, "Flow"), c.Flow);
                    if (get(n, "EofMarker") != null) c.Eof = get(n, "EofMarker") == "1";
                    if (get(n, "AutoTokenize") != null) c.Auto = get(n, "AutoTokenize") == "1";
                    if (get(n, "SkipRem") != null) c.Skip = get(n, "SkipRem") == "1";
                    c.Dir = get(n, "Folder");
                }
            }
            catch { }
        }

        void SaveSettings()
        {
            try
            {
                CaptureUi(curProf);
                var sb = new StringBuilder();
                sb.AppendLine("; SerComet settings (written when the program is closed)");
                sb.AppendLine("[General]");
                sb.AppendLine("Port=" + (comboPorts.SelectedItem as string ?? ""));
                sb.AppendLine("Target=" + ModelNames[curProf < 0 ? 1 : curProf]);
                sb.AppendLine();
                sb.AppendLine("; default folders of the programs (Folders window)");
                sb.AppendLine("[Folders]");
                for (int m = 0; m < NProf; m++) sb.AppendLine(ModelNames[m] + "=" + locs[m]);
                for (int m = 0; m < NProf; m++)
                {
                    var c = cfg[m];
                    sb.AppendLine();
                    sb.AppendLine("[" + ModelNames[m] + "]");
                    sb.AppendLine("Mode=" + ModeNames[c.Mode]);
                    sb.AppendLine("Baud=" + c.Baud);
                    sb.AppendLine("SoftBaud=" + c.SoftBaud);
                    sb.AppendLine("Delay=" + c.Delay);
                    sb.AppendLine("LineEnd=" + EolNames[c.Eol]);
                    sb.AppendLine("Flow=" + FlowNames[c.Flow]);
                    sb.AppendLine("EofMarker=" + (c.Eof ? "1" : "0"));
                    sb.AppendLine("AutoTokenize=" + (c.Auto ? "1" : "0"));
                    sb.AppendLine("SkipRem=" + (c.Skip ? "1" : "0"));
                    sb.AppendLine("Folder=" + (c.Dir ?? locs[m]));
                }
                string f = SettingsFile();
                Directory.CreateDirectory(Path.GetDirectoryName(f));
                File.WriteAllText(f, sb.ToString());
            }
            catch (Exception ex) { Log("Cannot save the settings: " + ex.Message); }
        }

        static string LocationsFile()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SerComet", "locations.txt");
        }

        static string[] LoadLocations()
        {
            var l = new string[NProf];
            for (int i = 0; i < NProf; i++) l[i] = DefaultLocation(i);
            try
            {
                if (File.Exists(LocationsFile()))
                {
                    string[] lines = File.ReadAllLines(LocationsFile());
                    for (int i = 0; i < NProf && i < lines.Length; i++) if (lines[i].Trim().Length > 0) l[i] = lines[i].Trim();
                }
            }
            catch { }
            return l;
        }

        // second window: the folders of the programs for each target computer
        void EditLocations()
        {
            using (var d = new LocationsForm(locs, Enumerable.Range(0, NProf).Select(DefaultLocation).ToArray()))
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                locs = d.Result;
                for (int m = 0; m < NProf; m++) cfg[m].Dir = locs[m];      // the new default folders are used from now on
                SaveSettings();
                SetDefaultDir(locs[curProf < 0 ? 1 : curProf]);
            }
        }

        void SetDefaultDir(string dir)
        {
            txtDir.Text = dir;
            RefreshFiles();
            Log("Folder: " + txtDir.Text);
        }

        void OpenTerminal()
        {
            if (port == null || !port.IsOpen) { Log("Open the port first."); return; }
            if (!rs232) { Log("The terminal works in RS-232 and TTL-UART modes."); return; }
            if (term != null) { term.Activate(); return; }
            lock (rxLock) { rx.Clear(); }
            term = new TerminalForm("SerComet Terminal - " + port.PortName + " " + port.BaudRate + " 8N1", SendRaw);
            term.FormClosed += delegate { term = null; Log("Terminal closed."); };
            term.Show(this);
            Log("Terminal open: typed characters are sent immediately, received data is shown live.");
        }

        void SendRaw(byte[] b)
        {
            lastTxAny = Environment.TickCount;
            try { if (port != null && port.IsOpen) port.Write(b, 0, b.Length); }
            catch (Exception ex) { Log("Send error: " + ex.Message); }
        }

        string Ext { get { return rs232 ? ".bas" : ".img"; } }
        Handshake FlowMode()
        {
            if (!rs232) return Handshake.None;
            switch (comboFlow.SelectedIndex)
            {
                case 0: return Handshake.None;
                case 2: return Handshake.RequestToSend;
                case 3: return Handshake.None;           // DSR/DTR: handled in SendFile (the port library has no DSR/DTR handshake)
                default: return Handshake.XOnXOff;
            }
        }

        void ApplyFlow()
        {
            try { if (port != null && port.IsOpen) { port.Handshake = FlowMode(); if (rs232 && comboFlow.SelectedIndex == 3) port.DtrEnable = true; } } catch (Exception ex) { Log("Error: " + ex.Message); }
        }

        string savedBaud = "9600";       // baud chosen by the user for the text modes
        string savedSoftBaud = "9600";   // baud of the Software UART of the PC-1600 (the same as chosen in the SL installer)
        static readonly string[] TextBauds = { "300", "600", "1200", "2400", "4800", "9600", "19200" };
        static readonly string[] SoftBauds = { "1200", "2400", "4800", "9600", "19200" };
        static readonly string[] FxBauds = { "300", "600", "1200", "2400", "4800" };      // Casio FX-850P / 870P: BASIC offers 150 to 4800 bps
        static readonly string[] UartBauds = { "300", "600", "1200", "2400", "4800", "9600", "19200", "38400", "57600", "115200" };   // UART Other: up to 115200
        static string[] TextBaudsFor(int m) { return m == 3 ? FxBauds : m == 5 ? UartBauds : TextBauds; }

        // the combo has all the speeds for the text modes and the five speeds of the SL installer for the Software UART
        void FillBaud(string[] list)
        {
            bool same = comboBaud.Items.Count == list.Length;
            for (int i = 0; same && i < list.Length; i++) if ((string)comboBaud.Items[i] != list[i]) same = false;
            if (same) return;
            comboBaud.Items.Clear();
            comboBaud.Items.AddRange(list);
        }

        int Baud
        {
            get
            {
                if (rs232) return int.Parse((string)comboBaud.SelectedItem);
                return radio1500.Checked ? 4800 : int.Parse((string)comboBaud.SelectedItem);
            }
        }

        void UpdateMode()
        {
            rs232 = radioRs.Checked || radioSio.Checked;
            bool softEdit = !rs232 && !radio1500.Checked;                  // Software UART of the PC-1600: the speed can be chosen
            numDelay.Enabled = btnTerminal.Enabled = comboEol.Enabled = comboFlow.Enabled = chkEof.Enabled = rs232;
            chkAuto.Enabled = !rs232;
            comboBaud.Enabled = false;                                      // (so that filling the list does not change the saved values)
            FillBaud(rs232 ? TextBaudsFor(curModel) : SoftBauds);
            comboBaud.SelectedItem = rs232 ? savedBaud : softEdit ? savedSoftBaud : "4800";
            comboBaud.Enabled = rs232 || softEdit;
            tips.SetToolTip(comboBaud, softEdit
                ? "Baud rate of the Software UART (8N1), the same as chosen in the SL installer. 9600 is the default and recommended, 19200 is fast, the lower speeds 4800 and 2400 (and 1200) are more reliable."
                : rs232 ? "Baud rate (8N1) of the text transfer." : "Baud rate: fixed 4800 for the Saver/Loader of the PC-1500.");
            ApplyFlow();
            if (!rs232 && term != null) term.Close();
            lblHint.Text = HintText();
            ApplyBaud();
            RefreshFiles();
        }

        void ApplyBaud()
        {
            try { if (port != null && port.IsOpen) { port.BaudRate = Baud; SetStatus(true); } } catch (Exception ex) { Log("Error: " + ex.Message); }
        }

        void SetStatus(bool open)
        {
            banner.PortOpen = open;
            banner.Invalidate();
            led.IsOpen = open;
        }

        void Log(string s)
        {
            txtLog.SelectionStart = txtLog.TextLength; txtLog.SelectionLength = 0;
            txtLog.SelectionFont = txtLog.Font; txtLog.SelectionColor = txtLog.ForeColor;
            txtLog.AppendText((txtLog.TextLength > 0 ? "\r\n" : "") + DateTime.Now.ToString("HH:mm:ss") + "  " + s);       // no empty last line: the newest text is at the bottom edge
            txtLog.SelectionStart = txtLog.TextLength; ScrollLogToEnd();
        }

        // an important message: bold and dark red
        void LogBold(string s)
        {
            txtLog.SelectionStart = txtLog.TextLength; txtLog.SelectionLength = 0;
            txtLog.SelectionFont = new Font(txtLog.Font, FontStyle.Bold); txtLog.SelectionColor = Color.DarkRed;
            txtLog.AppendText((txtLog.TextLength > 0 ? "\r\n" : "") + DateTime.Now.ToString("HH:mm:ss") + "  " + s);       // no empty last line: the newest text is at the bottom edge
            txtLog.SelectionStart = txtLog.TextLength; txtLog.SelectionLength = 0;
            txtLog.SelectionFont = txtLog.Font; txtLog.SelectionColor = txtLog.ForeColor;
            ScrollLogToEnd();
        }

        // ---- wrong baud rate: framing errors of the PC UART (the stop bit is not where it should be) and garbage in a text transfer
        int frameErrors, frameWindow, lastBaudWarn;
        void OnPortError(object sender, SerialErrorReceivedEventArgs e)
        {
            if (e.EventType != SerialError.Frame) return;
            int now = Environment.TickCount;
            if (unchecked(now - frameWindow) > 2000) { frameWindow = now; frameErrors = 0; }
            if (++frameErrors < 5 || unchecked(now - lastBaudWarn) < 10000) return;
            lastBaudWarn = now; frameErrors = 0;
            try { BeginInvoke(new Action(() => LogBold("Wrong baud rate (framing errors on the line): reduce baud rate, or check that the Sharp uses the same speed."))); } catch { }
        }

        // text received but it is garbage: null bytes mean that the PC is too fast (the Sharp's bits are read as a longer "0"),
        // bytes with the top bit set mean that the PC is too slow. null = does not look like a speed problem.
        static string GuessBaudProblem(byte[] d)
        {
            if (d.Length < 8) return null;
            int zeros = 0, high = 0, ctl = 0;
            foreach (byte b in d)
            {
                if (b == 0) zeros++;
                else if (b >= 0x80) high++;
                else if (b < 32 && b != 9 && b != 10 && b != 13 && b != 26) ctl++;
            }
            if (zeros >= d.Length * 0.3) return "reduce baud rate";
            if (high >= d.Length * 0.3) return "increase baud rate";
            if (high + zeros + ctl >= d.Length * 0.3) return "change";
            return null;
        }

        void RefreshPorts()
        {
            string cur = comboPorts.SelectedItem as string;
            comboPorts.Items.Clear();
            foreach (string p in SerialPort.GetPortNames()) comboPorts.Items.Add(p);
            if (cur != null && comboPorts.Items.Contains(cur)) comboPorts.SelectedItem = cur;
            else if (comboPorts.Items.Count > 0) comboPorts.SelectedIndex = 0;
        }

        const string ListTipDefault = "Files in the folder for the current mode (.img or .bas). Select one to send it. Point at a name to see the size and dates.";
        int tipIndex = -2, dragIndex = -1;
        bool fileMenuOpen;
        // CR alone or LF alone as the line end is a wrong choice for the Sharp (15-pin, PC-1500, PC-1600) and the Casio, which expect CR+LF;
        // the TRS-80 / NEC (CR is their default) and the other UART computers can use any of them
        bool EolSuspect(int idx) { return (idx == 0 || idx == 2) && curModel >= 0 && curModel <= 3; }

        int lastLockNote = Environment.TickCount - 10000;
        string sendingPath;                 // the file being sent (its row in the list is red)
        void MarkSending(string path) { sendingPath = path; if (lstFiles != null) lstFiles.Invalidate(); }
        Point dragStart;

        // ---- Skip REM: lines of BASIC text that hold only a comment are dropped (a REM line that a GOTO / GOSUB / THEN ... points to is kept)
        static string StripRemLines(string text, out int removed, out int kept)
        {
            removed = 0; kept = 0;
            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var refs = new HashSet<string>();
            var rx = new System.Text.RegularExpressions.Regex(@"(?:GOTO|GOSUB|THEN|ELSE|RESTORE|RUN)\s*(\d+(?:\s*,\s*\d+)*)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var rem = new System.Text.RegularExpressions.Regex(@"^\s*(\d*)\s*(?:REM\b|')", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            foreach (string l in lines)
                if (!rem.IsMatch(l))
                    foreach (System.Text.RegularExpressions.Match m in rx.Matches(l))
                        foreach (string n in m.Groups[1].Value.Split(',')) refs.Add(n.Trim());
            var sb = new StringBuilder();
            foreach (string l in lines)
            {
                var m = rem.Match(l);
                if (m.Success)
                {
                    if (m.Groups[1].Value.Length > 0 && refs.Contains(m.Groups[1].Value)) kept++;
                    else { removed++; continue; }
                }
                sb.Append(l).Append("\r\n");
            }
            return sb.ToString();
        }

        // free BASIC memory of a machine without a memory expansion (MEM): PC-1500A about 5946 B (5.8 KB, to be checked with MEM; the bare PC-1500 has less), PC-1600 about 11834 B (to be checked on real machines).
        // The Saver/Loader takes a part of it: PC-1500 BASIC starts at RAM start + &300 instead of + &C5 (571 B less); PC-1600 the code lies
        // in the machine-language area from &C100 to about &C2B0 (about 490 B).
        const int Mem1500 = 5946, Mem1600 = 11834, Sl1500 = 571, Sl1600 = 3648;

        void WarnSize(int imageBytes, bool software)
        {
            if (curModel < 0 || curModel > 1) return;
            int limit = curModel == 0 ? Mem1500 - (software ? Sl1500 : 0) : Mem1600 - (software ? Sl1600 : 0);
            if (imageBytes <= limit) return;
            LogBold("Listing can be too long: the program takes " + imageBytes + " B, the free memory of the " + (curModel == 0 ? "PC-1500A" : "PC-1600") +
                    " without a memory expansion is about " + limit + " B" + (software ? " (after the Saver/Loader)" : "") + ". An optional memory expansion is needed.");
        }

        string FileInfoText(string name)
        {
            try
            {
                var fi = new FileInfo(Path.Combine(txtDir.Text, name));
                if (!fi.Exists) return name;
                string ext = fi.Extension.ToLowerInvariant();
                string kind = ext == ".img" ? "Program image" : ext == ".bas" || ext == ".txt" ? "BASIC text" : ext == ".bin" ? "Binary file" : ext == ".wav" ? "WAV recording" : "File";
                string extra = "";
                if (ext == ".img")
                {
                    string err; byte[] d = File.ReadAllBytes(fi.FullName);
                    if (ProgImage.CheckImage(d, out err)) extra = "\r\nLines: " + ProgImage.CountLines(d);
                }
                else if (ext == ".wav")
                {
                    TapeWav.WavInfo wi; int wo, wl;
                    byte[] head = new byte[Math.Min(fi.Length, 4096)];
                    using (var fs = File.OpenRead(fi.FullName)) fs.Read(head, 0, head.Length);
                    // the header is in the first bytes; the length of the data is taken from the file size
                    if (TapeWav.Parse(head, fi.Length, out wi, out wo, out wl)) extra = "\r\n" + TapeWav.Describe(wi);
                }
                else if (ext == ".bas" || ext == ".txt")
                {
                    int n = 0; foreach (string l in File.ReadAllLines(fi.FullName)) if (l.Trim().Length > 0) n++;
                    extra = "\r\nLines: " + n;
                }
                return fi.Name + "\r\nType: " + kind + "\r\nSize: " + fi.Length.ToString("N0") + " B (" + (fi.Length / 1024.0).ToString("0.0") + " KB)" + extra +
                       "\r\nModified: " + fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss") + "\r\nCreated: " + fi.CreationTime.ToString("yyyy-MM-dd HH:mm:ss");
            }
            catch { return name; }
        }

        void DropFiles(string[] files)
        {
            if (files == null) return;
            if (!Directory.Exists(txtDir.Text)) { Log("Drop: folder does not exist: " + txtDir.Text); return; }
            int n = 0;
            foreach (string f in files)
            {
                try
                {
                    if (!File.Exists(f)) continue;
                    string ext = Path.GetExtension(f).ToLowerInvariant();
                    if (ext != ".bas" && ext != ".txt" && ext != ".img" && ext != ".wav") { Log("Drop: skipped " + Path.GetFileName(f) + " (only .bas, .txt, .img, .wav)"); continue; }
                    if (string.Equals(Path.GetDirectoryName(Path.GetFullPath(f)), Path.GetFullPath(txtDir.Text), StringComparison.OrdinalIgnoreCase)) continue;   // already there
                    string dest = Path.Combine(txtDir.Text, Path.GetFileName(f));
                    if (File.Exists(dest))
                    {
                        var src = new FileInfo(f); var old = new FileInfo(dest);
                        double diff = (src.LastWriteTime - old.LastWriteTime).TotalSeconds;
                        string state = Math.Abs(diff) <= 2
                            ? (src.Length == old.Length ? "and the file has the same date and size" : "and the file has the same date but a different size")
                            : diff > 0 ? "and the file is newer" : "but the file is older";
                        string question = "A file named " + Path.GetFileName(f) + " already exists in the folder, " + state + ".\r\n\r\n" +
                            "In the folder: " + old.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss") + ", " + old.Length.ToString("N0") + " B\r\n" +
                            "New file:      " + src.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss") + ", " + src.Length.ToString("N0") + " B\r\n\r\nOverwrite it?";
                        if (MessageBox.Show(this, question, "SerComet", MessageBoxButtons.YesNo, diff < -2 ? MessageBoxIcon.Warning : MessageBoxIcon.Question) != DialogResult.Yes) continue;
                    }
                    File.Copy(f, dest, true);
                    Log("Copied " + Path.GetFileName(f) + " to " + txtDir.Text);
                    n++;
                }
                catch (Exception ex) { Log("Drop error: " + ex.Message); }
            }
            if (n > 0) RefreshFiles();
        }

        void RefreshFiles()
        {
            lstFiles.Items.Clear();
            try
            {
                if (Directory.Exists(txtDir.Text))
                {
                    var found = new List<string>();
                    foreach (string pat in rs232 ? new[] { "*.bas", "*.txt", "*.wav" } : chkAuto.Checked ? new[] { "*.img", "*.bas", "*.txt", "*.wav" } : new[] { "*.img", "*.wav" })
                        foreach (string f in Directory.GetFiles(txtDir.Text, pat)) found.Add(Path.GetFileName(f));
                    found.Sort(StringComparer.OrdinalIgnoreCase);
                    lstFiles.Items.AddRange(found.ToArray());
                }
            }
            catch (Exception ex) { Log("List error: " + ex.Message); }
            SelectionChanged();
        }

        void TogglePort()
        {
            if (port != null) { ClosePort(); return; }
            string name = comboPorts.SelectedItem as string;
            if (name == null) { Log("No port selected."); return; }
            try
            {
                port = new SerialPort(name, Baud, Parity.None, 8, StopBits.One);
                port.Handshake = FlowMode();
                port.ReadBufferSize = 65536;
                port.DataReceived += OnData;
                port.ErrorReceived += OnPortError;
                port.Open();
                // like Tera Term: DTR and RTS are switched on (many adapters and the Sharp interfaces send only when they see them)
                try { port.DtrEnable = true; if (port.Handshake != Handshake.RequestToSend) port.RtsEnable = true; } catch { }
                btnOpen.Text = "Close port";
                btnOpen.Image = Icons.Cross();
                SetRefreshMode(true);
                SetStatus(true);
                UpdateSendButtons();
                Log("Port " + name + " open (" + port.BaudRate + " 8N1).");
            }
            catch (Exception ex)
            {
                Log("Cannot open the port: " + ex.Message);
                port = null;
            }
        }

        // Flush and Reset port work only while the port is open (Refresh and Open port always)
        void SetRefreshMode(bool open)
        {
            btnFlush.Enabled = btnReset.Enabled = btnTest.Enabled = open;
        }

        // close the port and open it again with the current settings; the buffers are cleared
        void ResetPort()
        {
            if (port == null) return;
            string name = port.PortName;
            ClosePort();
            if (comboPorts.Items.Contains(name)) comboPorts.SelectedItem = name;
            lock (rxLock) { rx.Clear(); }
            TogglePort();
            if (port != null) Log("Reset port: " + name + " was closed and opened again.");
        }

        // two short white flashes of both arrows (0.2 s in all): Flush and Reset port
        int flashStep;
        void FlashArrows()
        {
            flashStep = 0;
            var t = new Timer { Interval = 50 };
            t.Tick += delegate
            {
                flashStep++;                                   // 0-50 ms white, 50-100 off, 100-150 white, 150-200 off
                banner.FlashWhite = flashStep % 2 == 0 && flashStep < 4;
                banner.Invalidate();
                if (flashStep >= 4) { t.Stop(); t.Dispose(); }
            };
            banner.FlashWhite = true; banner.Invalidate();
            t.Start();
        }

        void FlushBuffers()
        {
            FlashArrows();
            try
            {
                if (port != null && port.IsOpen) { port.DiscardInBuffer(); port.DiscardOutBuffer(); }
                lock (rxLock) { rx.Clear(); }
                Log("Flush: the port buffers and the receive buffer were cleared.");
            }
            catch (Exception ex) { Log("Flush error: " + ex.Message); }
        }

        void ClosePort()
        {
            if (port == null) return;
            // closing the port during a transmission: stop sending like Abort and clear the red mark of the file
            if (sending)
            {
                abortRequested = true;
                MarkSending(null);
                lock (rxLock) { rx.Clear(); }
                Log("Port closed: transmission aborted.");
            }
            if (term != null) term.Close();
            try { port.DataReceived -= OnData; port.ErrorReceived -= OnPortError; if (port.IsOpen) port.Close(); } catch { }
            port = null;
            btnOpen.Text = "Open port";
            btnOpen.Image = Icons.Plug();
            SetRefreshMode(false);
            banner.PortOpen = false;
            banner.Invalidate();
            led.IsOpen = false;
            UpdateSendButtons();
        }

        void OnData(object sender, SerialDataReceivedEventArgs e)
        {
            try
            {
                var p = port;
                if (p == null) return;
                int n = p.BytesToRead;
                if (n <= 0) return;
                var buf = new byte[n];
                n = p.Read(buf, 0, n);
                if (n != buf.Length) Array.Resize(ref buf, n);
                lastRxAny = Environment.TickCount;
                var t = term;
                if (t != null)
                {
                    try { t.BeginInvoke(new Action(() => t.Rx(buf))); } catch { }
                    return;
                }
                if (sending) return;
                lock (rxLock)
                {
                    for (int i = 0; i < n; i++) rx.Add(buf[i]);
                    lastRx = Environment.TickCount;
                }
            }
            catch { }
        }

        // end of transmission = silence: 0.8 s (software serial, SAVE sends without gaps), 3 s (RS-232 text)
        // flashes per 2 seconds of the arrows for a baud rate: 300 -> 1, 600 -> 2, 1200 -> 3, 2400 -> 4, 4800 -> 5, 9600 -> 6,
        // 19200 -> 8, above 19200 -> 10
        static double BlinkHz(int baud)
        {
            int n = baud <= 300 ? 1 : baud <= 600 ? 2 : baud <= 1200 ? 3 : baud <= 2400 ? 4 : baud <= 4800 ? 5 : baud <= 9600 ? 6 : baud <= 19200 ? 8 : 10;
            return n / 2.0;
        }

        void BlinkTick(object sender, EventArgs e)
        {
            if (!(banner.Sending || banner.Receiving)) return;
            int baud = 9600;
            try
            {
                // the speed the port really runs at (every mode, also the Software UART); without a port the speed of the combo
                var p = port;
                baud = p != null && p.IsOpen ? p.BaudRate : Baud;
            }
            catch { }
            long half = (long)(500.0 / BlinkHz(baud));                // half of a period in ms
            bool on = (blinkWatch.ElapsedMilliseconds / half) % 2 == 0;
            if (banner.Blink != on) { banner.Blink = on; banner.Invalidate(); }
        }

        void TimerTick(object sender, EventArgs e)
        {
            byte[] data = null;
            bool receiving = false, started = false;
            lock (rxLock)
            {
                int quiet = unchecked(Environment.TickCount - lastRx);
                receiving = rx.Count > 0;
                // a program has begun to arrive: the data so far look like a program (not like stray bytes or garbage)
                if (receiving && !rxStartLogged && rx.Count >= 8)
                {
                    bool prog;
                    try { prog = rs232 ? LooksLikeProgramText(rx.ToArray()) : LooksLikeImageBegin(rx); } catch { prog = false; }
                    if (prog) { rxStartLogged = true; started = true; }
                }
                if (!receiving) rxStartLogged = false;
                int eofAt = rs232 ? rx.IndexOf(0x1A) : -1;     // 1Ah = end-of-file code of an ASCII file: finish at once
                if (eofAt >= 0)
                {
                    data = rx.GetRange(0, eofAt).ToArray();
                    rx.Clear();
                    receiving = false;
                    rxStartLogged = false;
                }
                else if (rx.Count > 0 && quiet > (rs232 ? 3000 : 800))
                {
                    data = rx.ToArray();
                    rx.Clear();
                    receiving = false;
                    rxStartLogged = false;
                }
            }
            if (sending && port == null) sending = false;          // safety net: no port, so nothing can be sent (and received data must not be dropped)
            if (started) Log("Start receiving program data");
            receiving |= unchecked(Environment.TickCount - lastRxAny) < 300;
            bool tx = sending || unchecked(Environment.TickCount - lastTxAny) < 300;
            bool changed = banner.Receiving != receiving || banner.Sending != tx;
            banner.Receiving = receiving;
            banner.Sending = tx;
            if (!(receiving || tx)) { if (changed || banner.Blink) { banner.Blink = false; banner.Invalidate(); } }
            else if (changed) banner.Invalidate();
            if (data != null) Received(data);
        }

        // text mode: the Sharp sent the program in its internal (intermediate / tokenized) format, e.g. SAVE "COM1:" without ,A.
        // PC-1600 and PC-1500: converted to BASIC text. All other computers: rejected with a red warning (send a BASIC listing instead).
        bool ReceivedInternalFormat(byte[] data)
        {
            byte[] img;
            if (!ProgImage.FindLines(data, out img)) return false;
            Directory.CreateDirectory(txtDir.Text);
            string name = SharpBase(ProgImage.NameFromRem(img));
            if (curModel <= 1)
            {
                LogBold("Detected a file in the internal (intermediate / tokenized) format: after the reception it is converted (detokenized) to a BASIC text file! " +
                        (curModel == 1 ? "To send BASIC text at once use SAVE \"COM1:\",A (PC-1600)." : "To send BASIC text at once check the SAVE command of the PC-1500 (CE-158) in the manual."));
                string problem;
                string bas = Pc1600Basic.Detokenize(img, curModel == 0, out problem);
                if (problem == null)
                {
                    string path = UniqueSharp(name, ".bas");
                    File.WriteAllBytes(path, Encoding.GetEncoding(28591).GetBytes(bas));
                    Log("RECEIVED " + data.Length + " B, " + ProgImage.CountLines(img) + " lines -> " + Path.GetFileName(path) + " (converted from the internal format).");
                    RefreshFiles();
                    return true;
                }
                Log("Conversion problem: " + problem + "; the program is kept as an image (.img).");
            }
            else
            {
                // the tokens of these computers are not known: the file is rejected (nothing is saved)
                LogBold("Detected a file in the internal (tokenized) format: it is REJECTED and not saved! Send the program as a BASIC LISTING (text): " +
                        "PC-1350 / PC-1360: OPEN \"1200,N,8,1,A,C,&1A\" and then SAVE; PC-E500(S): OPEN \"9600,N,8,1,A,C,&H1A,N,N\" and then SAVE \"COM:PROGRAM\",A " +
                        "(over COM the listing is ASCII); other computers: check the manual.");
                return true;
            }
            string ipath = UniqueSharp(name, ".img");
            File.WriteAllBytes(ipath, img);
            Log("RECEIVED " + data.Length + " B, " + ProgImage.CountLines(img) + " lines -> " + Path.GetFileName(ipath));
            RefreshFiles();
            return true;
        }

        void Received(byte[] data)
        {
            try
            {
                if (rs232 && ReceivedInternalFormat(data)) return;
                if (rs232 && !LooksLikeProgramText(data))
                {
                    string guess = GuessBaudProblem(data);
                    if (guess != null) LogBold("Wrong baud rate, " + guess + " (the received data is garbage).");
                    return;
                }
                Directory.CreateDirectory(txtDir.Text);
                if (rs232)
                {
                    string name = SharpBase(ProgImage.NameFromText(data));
                    string path = UniqueSharp(name, ".bas");
                    File.WriteAllBytes(path, data);
                    Log("RECEIVED " + data.Length + " B (text) -> " + Path.GetFileName(path));
                    RefreshFiles();
                    return;
                }
                string err;
                byte[] img = null;
                string recovered = null;
                if (ProgImage.CheckStream(data, out err)) img = ProgImage.ImageFromStream(data);
                else
                {
                    // the Sharp sent more than the program (a wrong end pointer): the program ends at its end marker FFh
                    byte[] sal;
                    if (data.Length >= 16 && ProgImage.Salvage(data, out sal))
                    {
                        img = sal;
                        recovered = "the Sharp sent " + data.Length + " B but the program is " + sal.Length + " B up to its end marker (FFh); the rest was dropped. Check the end pointer on the Sharp";
                    }
                }
                if (img != null)
                {
                    string name = SharpBase(ProgImage.NameFromRem(img));
                    string path = null;
                    string note = "";
                    if (chkAuto.Checked)
                    {
                        // auto tokenize ON: the image only exists in memory, the file is BASIC text
                        string problem;
                        string bas = Pc1600Basic.Detokenize(img, radio1500.Checked, out problem);
                        if (problem == null)
                        {
                            path = UniqueSharp(name, ".bas");
                            File.WriteAllBytes(path, Encoding.GetEncoding(28591).GetBytes(bas));
                            note = " (converted from the " + (radio1500.Checked ? "PC-1500" : "PC-1600") + " intermediate code)";
                        }
                        else Log("Conversion problem: " + problem + "; the image is kept as .img.");
                    }
                    if (path == null)
                    {
                        path = UniqueSharp(name, ".img");
                        File.WriteAllBytes(path, img);
                    }
                    if (recovered == null) Log("RECEIVED " + data.Length + " B, " + ProgImage.CountLines(img) + " lines, checksum OK -> " + Path.GetFileName(path) + note);
                    else Log("RECOVERED " + ProgImage.CountLines(img) + " lines, " + img.Length + " B -> " + Path.GetFileName(path) + note + " (" + recovered + ").");
                    RefreshFiles();
                }
                else
                {
                    if (data.Length < 16)       // a few random bytes on the line, not a transfer
                    {
                        return;
                    }
                    string path = UniquePath("ERROR_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"), ".bad");
                    File.WriteAllBytes(path, data);
                    Log("RECEIVE ERROR (" + err + "), " + data.Length + " B saved as " + Path.GetFileName(path) + ". Repeat SAVE.");
                }
            }
            catch (Exception ex) { Log("Write error: " + ex.Message); }
        }

        // text received in the text modes is saved only when it looks like a program: mostly printable characters and at least one
        // numbered BASIC line (or, without line numbers, a longer text); a few random bytes from the line are noise
        // Software UART: the stream begins with a program line: line number (2 bytes), length, the line, CR (the first line may be still arriving)
        static bool LooksLikeImageBegin(List<byte> d)
        {
            if (d.Count < 8) return false;
            int len = d[2];
            if (len < 2) return false;
            if (3 + len > d.Count) return true;
            return d[2 + len] == 0x0D;
        }

        static bool LooksLikeProgramText(byte[] d)
        {
            if (d.Length < 8) return false;
            int printable = 0;
            foreach (byte b in d) if ((b >= 32 && b < 127) || b == 9 || b == 10 || b == 13 || b == 26) printable++;
            if (printable < d.Length * 0.85) return false;
            string text = Encoding.GetEncoding(28591).GetString(d);
            foreach (string line in text.Split('\n', '\r'))
            {
                string t = line.Trim();
                int i = 0;
                while (i < t.Length && char.IsDigit(t[i])) i++;
                if (i > 0 && i < t.Length) return true;          // "10 PRINT ..."
            }
            return d.Length >= 64;
        }

        // ------------------------------------------------------------------ names for the Sharp
        int NameModel { get { if (curProf == 4 || curProf == 5) return 1; return curModel < 0 ? 1 : curModel > 2 ? 2 : curModel; } }      // the PC-E500 uses the rules of the PC-1600 (8 characters), the other computers those of the 15-pin models

        // the name of a program for the selected computer (length and characters); no name: PRG + time
        string SharpBase(string raw)
        {
            bool changed;
            string n = SharpName.Fix(string.IsNullOrEmpty(raw) ? "PRG" + DateTime.Now.ToString("HHmmss") : raw, NameModel, out changed);
            return n;
        }

        // a free file name with a number at the end that keeps the length limit of the Sharp name
        string UniqueSharp(string name, string ext)
        {
            string path = Path.Combine(txtDir.Text, name + ext);
            int max = SharpName.MaxBase(NameModel);
            for (int i = 2; File.Exists(path); i++)
            {
                string suffix = i.ToString();
                string b = name.Substring(0, Math.Min(name.Length, max - suffix.Length)) + suffix;
                path = Path.Combine(txtDir.Text, b + ext);
            }
            return path;
        }

        // before a file is sent: the name the pocket computer can use (the stream itself carries no name)
        void CheckSharpName(string path)
        {
            string raw = Path.GetFileNameWithoutExtension(path);
            bool changed;
            string n = SharpName.Fix(raw, NameModel, out changed);
            if (changed) Log("Name for the " + GroupNames[NameModel] + ": \"" + n + "\" (shortened or changed from \"" + raw + "\": " + SharpName.Rules(NameModel) + ").");
        }

        string UniquePath(string name, string ext)
        {
            string path = Path.Combine(txtDir.Text, name + ext);
            for (int i = 2; File.Exists(path); i++)
                path = Path.Combine(txtDir.Text, name + "_" + i + ext);
            return path;
        }

        // BASIC text <-> program image (.img) for the selected target computer; only for a selected .bas / .txt / .img file
        void ConvertSelected()
        {
            if (lstFiles.SelectedItem == null) return;
            string path = Path.Combine(txtDir.Text, (string)lstFiles.SelectedItem);
            try
            {
                bool p1500 = radio1500.Checked;
                string ext = Path.GetExtension(path).ToLowerInvariant();
                byte[] data = File.ReadAllBytes(path);
                var enc = Encoding.GetEncoding(28591);
                string problem;
                string outPath;
                if (ext == ".img" || ext == ".bin")
                {
                    string err;
                    // a .bin is converted only when it really is a BASIC program image (otherwise it is machine code or data)
                    if (ext == ".bin" && !ProgImage.CheckImage(data, out err)) { Log("Not converted: " + Path.GetFileName(path) + " is not a BASIC program image (" + err + "); machine code or data?"); return; }
                    string bas = Pc1600Basic.Detokenize(data, p1500, out problem);
                    outPath = UniquePathIn(Path.GetDirectoryName(path), Path.GetFileNameWithoutExtension(path), ".bas");
                    File.WriteAllBytes(outPath, enc.GetBytes(bas));
                    Log("Converted " + Path.GetFileName(path) + " to BASIC text -> " + Path.GetFileName(outPath) + (problem != null ? " (warning: " + problem + ")" : ""));
                }
                else
                {
                    Log("Tokenizing to the " + (p1500 ? "PC-1500" : "PC-1600") + " intermediate code format...");
                    byte[] img = Pc1600Basic.Tokenize(enc.GetString(data), p1500, out problem);
                    if (img == null) { Log("Tokenizer error: " + problem); return; }
                    outPath = UniquePathIn(Path.GetDirectoryName(path), Path.GetFileNameWithoutExtension(path), ".img");
                    File.WriteAllBytes(outPath, img);
                    Log("Converted " + Path.GetFileName(path) + " -> " + Path.GetFileName(outPath) + " (" + img.Length + " B)");
                }
                RefreshFiles();
            }
            catch (Exception ex) { Log("Conversion error: " + ex.Message); }
        }

        // preview of the selected file: text as it is, program images as BASIC text, anything else as a hex dump
        // a REM outside quotation marks followed by a space loses that one space ("10 REM TEXT" is sent as "10 REMTEXT")
        static string FixRemSpace(string src)
        {
            var sb = new StringBuilder();
            foreach (string line in src.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                var o = new StringBuilder();
                bool q = false;
                for (int i = 0; i < line.Length; i++)
                {
                    char c = line[i];
                    if (c == '"') q = !q;
                    o.Append(c);
                    if (!q && (c == 'R' || c == 'r') && i + 3 < line.Length && string.Compare(line, i, "REM", 0, 3, StringComparison.OrdinalIgnoreCase) == 0
                        && (i == 0 || !char.IsLetter(line[i - 1])) && line[i + 3] == ' ')
                    {
                        o.Append(line, i + 1, 2);                 // "EM"
                        o.Append(line, i + 4, line.Length - (i + 4));   // the comment without the space
                        break;
                    }
                }
                sb.Append(o).Append('\n');
            }
            string r = sb.ToString();
            return r.EndsWith("\n") && !src.EndsWith("\n") && !src.EndsWith("\r") ? r.Substring(0, r.Length - 1) : r;
        }

        // ---- Compare: the listing of the selected file against another file (only the content, not the dates)
        string ListingOf(string path)
        {
            return Encoding.GetEncoding(28591).GetString(File.ReadAllBytes(path));
        }

        // lines of a listing: text split at CR / LF, empty lines and the end-of-file code dropped; the number of the line (or -1)
        static List<KeyValuePair<int, string>> ListingLines(string text)
        {
            var res = new List<KeyValuePair<int, string>>();
            foreach (string raw in text.Replace("\u001A", "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                string t = raw.TrimEnd();
                if (t.Trim().Length == 0) continue;
                int i = 0;
                string tt = t.TrimStart();
                while (i < tt.Length && char.IsDigit(tt[i])) i++;
                int num = -1;
                if (i > 0 && i < 6) int.TryParse(tt.Substring(0, i), out num);
                res.Add(new KeyValuePair<int, string>(num, t));
            }
            return res;
        }

        // a line for the comparison: the spaces outside quotation marks are dropped (the PC-1600 lists "IF A THEN" as "IF A THEN" or
        // "IF G$="F"THEN", "RND (4)" and so on), and in a REM the spaces before the comment; the text of strings and comments stays
        static string NormLine(string line)
        {
            var o = new StringBuilder();
            bool q = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '"') { q = !q; o.Append(c); continue; }
                if (q) { o.Append(c); continue; }
                if (c == ' ' || c == '\t') continue;
                o.Append(c);
                if ((c == 'M' || c == 'm') && o.Length >= 3 && string.Compare(o.ToString(o.Length - 3, 3), "REM", StringComparison.OrdinalIgnoreCase) == 0
                    && (o.Length == 3 || !char.IsLetter(o[o.Length - 4])))
                { o.Append(line.Substring(i + 1).TrimStart()); break; }
            }
            return o.ToString();
        }

        static bool SameFile(string a, string b)
        {
            try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        void CompareSelected()
        {
            if (lstFiles.SelectedItem == null) return;
            string nameA = (string)lstFiles.SelectedItem;
            string pathA = Path.Combine(txtDir.Text, nameA);
            string pathB;
            if (Path.GetExtension(nameA).Equals(".wav", StringComparison.OrdinalIgnoreCase))
            {
                using (var dw = new OpenFileDialog { Title = "Compare " + nameA + " with ...", InitialDirectory = txtDir.Text, Filter = "WAV recordings (*.wav)|*.wav" })
                {
                    if (dw.ShowDialog(this) != DialogResult.OK) return;
                    pathB = dw.FileName;
                }
                if (SameFile(pathA, pathB)) { LogBold("Compare: choose another file (it is the same file)."); return; }
                try { new PreviewForm("Compare: " + nameA + " / " + Path.GetFileName(pathB), CompareWavs(nameA, pathA, Path.GetFileName(pathB), pathB)).Show(this); }
                catch (Exception ex) { Log("Compare error: " + ex.Message); }
                return;
            }
            // only files of the same kind: program images (.img) with .img, BASIC text (.bas = .txt) with .bas / .txt
            bool img = Path.GetExtension(nameA).Equals(".img", StringComparison.OrdinalIgnoreCase);
            using (var d = new OpenFileDialog { Title = "Compare " + nameA + " with ...", InitialDirectory = txtDir.Text,
                Filter = img ? "Program images (*.img)|*.img" : "BASIC text (*.bas;*.txt)|*.bas;*.txt" })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                pathB = d.FileName;
            }
            if (SameFile(pathA, pathB)) { LogBold("Compare: choose another file (it is the same file)."); return; }
            if (Path.GetExtension(pathB).Equals(".img", StringComparison.OrdinalIgnoreCase) != img)
            { Log("Compare: choose a file of the same kind (" + (img ? ".img" : ".bas / .txt") + ")."); return; }
            string nameB = Path.GetFileName(pathB);
            string report;
            try { report = CompareReport(nameA, pathA, nameB, pathB); }
            catch (Exception ex) { Log("Compare error: " + ex.Message); return; }
            new PreviewForm("Compare: " + nameA + " / " + nameB, report).Show(this);
        }

        // program images: the lines [number 2 B][length 1 B][content ending with 0D], compared as bytes
        static List<KeyValuePair<int, byte[]>> ImageLines(byte[] d)
        {
            var res = new List<KeyValuePair<int, byte[]>>();
            int p = 0;
            while (p + 3 <= d.Length && d[p] != 0xFF)
            {
                int len = d[p + 2], end = Math.Min(d.Length, p + 3 + len);
                var c = new byte[end - p];
                Array.Copy(d, p, c, 0, c.Length);
                res.Add(new KeyValuePair<int, byte[]>(d[p] * 256 + d[p + 1], c));
                p = end;
            }
            return res;
        }

        string CompareImages(string nameA, byte[] a, string nameB, byte[] b)
        {
            var la = ImageLines(a); var lb = ImageLines(b);
            var sb = new StringBuilder();
            sb.AppendLine("A: " + nameA + "   B: " + nameB);
            sb.AppendLine("Program images are compared byte by byte (not converted to BASIC); dates are not compared.");
            sb.AppendLine();
            bool eq = a.Length == b.Length;
            for (int i = 0; eq && i < a.Length; i++) if (a[i] != b[i]) eq = false;
            if (eq) { sb.AppendLine("The images are IDENTICAL (" + la.Count + " lines, " + a.Length + " B)."); return sb.ToString(); }
            sb.AppendLine("The images are DIFFERENT:");
            sb.AppendLine("  size:           A " + a.Length + " B, B " + b.Length + " B" + (a.Length == b.Length ? " (the same)" : " (different)"));
            sb.AppendLine("  lines:          A " + la.Count + ", B " + lb.Count + (la.Count == lb.Count ? " (the same)" : " (different number of lines)"));
            Func<List<KeyValuePair<int, byte[]>>, string> first = l => l.Count == 0 ? "-" : l[0].Key.ToString();
            Func<List<KeyValuePair<int, byte[]>>, string> last = l => l.Count == 0 ? "-" : l[l.Count - 1].Key.ToString();
            sb.AppendLine("  first line no.: A " + first(la) + ", B " + first(lb) + (first(la) == first(lb) ? " (the same)" : " (different start numbering)"));
            sb.AppendLine("  last line no.:  A " + last(la) + ", B " + last(lb) + (last(la) == last(lb) ? " (the same)" : " (different end numbering)"));
            var mapB = new Dictionary<int, byte[]>();
            foreach (var kv in lb) if (!mapB.ContainsKey(kv.Key)) mapB[kv.Key] = kv.Value;
            int same = 0, diff = 0, onlyA = 0;
            var diffs = new List<string>();
            foreach (var kv in la)
            {
                byte[] o;
                if (!mapB.TryGetValue(kv.Key, out o)) { onlyA++; continue; }
                bool e = o.Length == kv.Value.Length;
                int k = 0;
                while (k < o.Length && k < kv.Value.Length && o[k] == kv.Value[k]) k++;
                if (e && k == o.Length) { same++; continue; }
                diff++;
                if (diffs.Count < 25) diffs.Add("  line " + kv.Key + " (first difference at byte " + (k + 1) + ", A " + kv.Value.Length + " B, B " + o.Length + " B)");
            }
            var setA = new HashSet<int>();
            foreach (var kv in la) setA.Add(kv.Key);
            int onlyB = 0;
            foreach (var kv in lb) if (!setA.Contains(kv.Key)) onlyB++;
            sb.AppendLine("  lines with the same number: " + same + " identical, " + diff + " with other content; only in A: " + onlyA + ", only in B: " + onlyB);
            sb.AppendLine();
            string verdict = diff == 0 && onlyA + onlyB > 0 ? "The lines that exist in both are identical; the images differ in the numbering / the number of lines."
                : onlyA + onlyB == 0 && la.Count > 0 && diff <= Math.Max(3, la.Count / 5) ? "A similar structure (the same line numbers) but there are errors in " + diff + " line(s)."
                : onlyA + onlyB == 0 && la.Count > 0 ? "The same line numbers, but the content is different (" + diff + " of " + la.Count + " lines)."
                : "The content is different.";
            sb.AppendLine("Result: " + verdict);
            if (diffs.Count > 0) { sb.AppendLine(); sb.AppendLine("Differing lines" + (diff > diffs.Count ? " (the first " + diffs.Count + " of " + diff + ")" : "") + ":"); foreach (string d in diffs) sb.AppendLine(d); }
            return sb.ToString().Replace("\n", "\r\n").Replace("\r\r\n", "\r\n");
        }

        string CompareReport(string nameA, string pathA, string nameB, string pathB)
        {
            if (Path.GetExtension(pathA).Equals(".img", StringComparison.OrdinalIgnoreCase))
                return CompareImages(nameA, File.ReadAllBytes(pathA), nameB, File.ReadAllBytes(pathB));
            string ta = ListingOf(pathA), tb = ListingOf(pathB);
            var sb = new StringBuilder();
            var oa = ListingLines(ta);
            var ob = ListingLines(tb);
            string rawA = string.Join("\n", oa.Select(x => x.Value).ToArray());
            string rawB = string.Join("\n", ob.Select(x => x.Value).ToArray());
            int bytesA = Encoding.GetEncoding(28591).GetByteCount(rawA), bytesB = Encoding.GetEncoding(28591).GetByteCount(rawB);
            var origA = new Dictionary<int, string>(); var origB = new Dictionary<int, string>();
            foreach (var kv in oa) if (kv.Key >= 0 && !origA.ContainsKey(kv.Key)) origA[kv.Key] = kv.Value;
            foreach (var kv in ob) if (kv.Key >= 0 && !origB.ContainsKey(kv.Key)) origB[kv.Key] = kv.Value;
            // compared without the spaces outside strings and comments (they are the listing style of the machine, not the program)
            var la = oa.Select(x => new KeyValuePair<int, string>(x.Key, NormLine(x.Value))).ToList();
            var lb = ob.Select(x => new KeyValuePair<int, string>(x.Key, NormLine(x.Value))).ToList();
            string sa = string.Join("\n", la.Select(x => x.Value).ToArray());
            string sbb = string.Join("\n", lb.Select(x => x.Value).ToArray());
            Func<List<KeyValuePair<int, string>>, string> first = l => l.Count == 0 ? "-" : (l[0].Key >= 0 ? l[0].Key.ToString() : "(no number)");
            Func<List<KeyValuePair<int, string>>, string> last = l => l.Count == 0 ? "-" : (l[l.Count - 1].Key >= 0 ? l[l.Count - 1].Key.ToString() : "(no number)");
            sb.AppendLine("A: " + nameA + "   B: " + nameB);
            sb.AppendLine("Only the listings are compared (dates and the line ends are not); the spaces outside quotation marks and before a REM comment are ignored.");
            sb.AppendLine();
            if (sa == sbb)
            {
                sb.AppendLine("The listings are IDENTICAL (" + la.Count + " lines)" + (rawA == rawB ? "." : ", only the spacing differs (A " + bytesA + " B, B " + bytesB + " B)."));
                return sb.ToString().Replace("\n", "\r\n").Replace("\r\r\n", "\r\n");
            }
            sb.AppendLine("The listings are DIFFERENT:");
            sb.AppendLine("  size:           A " + bytesA + " B, B " + bytesB + " B" + (bytesA == bytesB ? " (the same)" : " (different)"));
            sb.AppendLine("  lines:          A " + la.Count + ", B " + lb.Count + (la.Count == lb.Count ? " (the same)" : " (different number of lines)"));
            sb.AppendLine("  first line no.: A " + first(la) + ", B " + first(lb) + (first(la) == first(lb) ? " (the same)" : " (different start numbering)"));
            sb.AppendLine("  last line no.:  A " + last(la) + ", B " + last(lb) + (last(la) == last(lb) ? " (the same)" : " (different end numbering)"));
            // the same line numbers (in the same order)?
            bool sameNumbers = la.Count == lb.Count;
            for (int i = 0; sameNumbers && i < la.Count; i++) if (la[i].Key != lb[i].Key) sameNumbers = false;
            sb.AppendLine("  numbering:      " + (sameNumbers ? "the same line numbers" : "different line numbers"));
            // lines with the same number: equal or not
            var mapB = new Dictionary<int, string>();
            foreach (var kv in lb) if (kv.Key >= 0 && !mapB.ContainsKey(kv.Key)) mapB[kv.Key] = kv.Value;
            int same = 0, diff = 0, onlyA = 0;
            var diffs = new List<string>();
            foreach (var kv in la)
            {
                if (kv.Key < 0) continue;
                string o;
                if (!mapB.TryGetValue(kv.Key, out o)) { onlyA++; continue; }
                if (o == kv.Value) same++;
                else
                {
                    diff++;
                    if (diffs.Count < 25)
                    {
                        int k = 0;
                        while (k < kv.Value.Length && k < o.Length && kv.Value[k] == o[k]) k++;
                        string oA, oB;
                        if (!origA.TryGetValue(kv.Key, out oA)) oA = kv.Value;
                        if (!origB.TryGetValue(kv.Key, out oB)) oB = o;
                        diffs.Add("  line " + kv.Key + " (first difference at character " + (k + 1) + " of the line without spaces)\n    A: " + oA + "\n    B: " + oB);
                    }
                }
            }
            int onlyB = 0;
            var mapA = new HashSet<int>();
            foreach (var kv in la) if (kv.Key >= 0) mapA.Add(kv.Key);
            foreach (var kv in lb) if (kv.Key >= 0 && !mapA.Contains(kv.Key)) onlyB++;
            sb.AppendLine("  lines with the same number: " + same + " identical, " + diff + " with other text; only in A: " + onlyA + ", only in B: " + onlyB);
            sb.AppendLine();
            string verdict;
            if (sameNumbers && diff > 0 && diff <= Math.Max(3, la.Count / 5)) verdict = "A similar structure (the same line numbers) but there are errors in " + diff + " line(s).";
            else if (sameNumbers && diff > 0) verdict = "The same line numbers, but the content is different (" + diff + " of " + la.Count + " lines).";
            else if (!sameNumbers && diff == 0 && same > 0) verdict = "The lines that exist in both are identical; the files differ in the numbering / the number of lines.";
            else if (same > 0 && diff + onlyA + onlyB <= Math.Max(3, la.Count / 5)) verdict = "A similar structure, but there are differences (other numbering and / or errors).";
            else verdict = "The content is different.";
            sb.AppendLine("Result: " + verdict);
            if (diffs.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Differing lines" + (diff > diffs.Count ? " (the first " + diffs.Count + " of " + diff + ")" : "") + ":");
                foreach (string d in diffs) sb.AppendLine(d);
            }
            return sb.ToString().Replace("\n", "\r\n").Replace("\r\r\n", "\r\n");
        }

        // ---- WAV: BASIC text <-> tape recording (PC-1500 and PC-1600), Play, Compare of recordings
        string UniquePath2(string path)
        {
            string dir = Path.GetDirectoryName(path), b = Path.GetFileNameWithoutExtension(path), e = Path.GetExtension(path);
            for (int i = 2; File.Exists(path); i++) path = Path.Combine(dir, b + "_" + i + e);
            return path;
        }

        void WavSelected()
        {
            if (lstFiles.SelectedItem == null) return;
            string name = (string)lstFiles.SelectedItem;
            string path = Path.Combine(txtDir.Text, name);
            string ext = Path.GetExtension(name).ToLowerInvariant();
            if (curModel > 2) { Log("WAV: the converters are for the Sharp PC-1500, PC-1600, PC-1350, PC-1360, PC-E500 and PC-E500S (choose the computer in the Target computer box)."); return; }
            if (curProf == 6 || curProf == 7) { Log("WAV: tokenization and tape format is not supported for this computer (PC-1450..75 / other)."); return; }
            try
            {
                if (ext == ".wav") WavToBasic(path);
                else if (ext == ".bas" || ext == ".txt") BasicToWav(path);
                else Log("WAV: select a BASIC text file (.bas / .txt) or a .wav recording.");
            }
            catch (Exception ex) { Log("WAV error: " + ex.Message); }
        }

        // PC-1600: MODE 0 / MODE 1 / Cancel (returns 0, 1 or -1)
        int AskTapeMode(string file)
        {
            int result = -1;
            using (var f = new Form())
            {
                f.Text = "WAV: " + file;
                f.FormBorderStyle = FormBorderStyle.FixedDialog; f.MaximizeBox = false; f.MinimizeBox = false; f.ShowInTaskbar = false;
                f.StartPosition = FormStartPosition.CenterParent;
                f.ClientSize = new Size(470, 190);
                var lbl = new Label
                {
                    Left = 14, Top = 12, Width = 442, Height = 120, AutoSize = false,
                    Text = "Choose the tape format for the PC-1600:\r\n\r\n" +
                           "MODE 0 - the PC-1600 mode (CE-1600P / CE-152): the program is an ASCII BASIC file; load it in MODE 0 with LOAD \"CAS:NAME\".\r\n\r\n" +
                           "MODE 1 - the PC-1500 compatible mode (CE-150): the program is recorded tokenized; switch the PC-1600 to MODE 1 and load it with CLOAD."
                };
                var b0 = new Button { Text = "MODE 0", Left = 14, Top = 146, Width = 100, Height = 28 };
                var b1 = new Button { Text = "MODE 1", Left = 124, Top = 146, Width = 100, Height = 28 };
                var bc = new Button { Text = "Cancel", Left = 356, Top = 146, Width = 100, Height = 28, DialogResult = DialogResult.Cancel };
                b0.Click += delegate { result = 0; f.DialogResult = DialogResult.OK; };
                b1.Click += delegate { result = 1; f.DialogResult = DialogResult.OK; };
                f.Controls.AddRange(new Control[] { lbl, b0, b1, bc });
                f.CancelButton = bc;
                f.ShowDialog(this);
            }
            return result;
        }

        void BasicToWav(string path)
        {
            string src = Encoding.GetEncoding(28591).GetString(File.ReadAllBytes(path));
            string baseName = Path.GetFileNameWithoutExtension(path);
            string tape = new string(baseName.ToUpperInvariant().Where(c => (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')).ToArray());
            if (tape.Length == 0) tape = "PROGRAM";
            if (curModel == 2)
            {
                int pc = curProf == 2 ? 1350 : curProf == 3 ? 1360 : 500;         // the model is chosen in the Target computer box
                byte[] w2;
                string suffix2;
                if (pc == 500)
                {
                    w2 = TapeWav.EncodeAscii(src, tape.Substring(0, Math.Min(8, tape.Length)), true);
                    suffix2 = "_E500_CAS";
                }
                else
                {
                    string problem2;
                    byte[] img2 = SharpTok.Tokenize(src, pc, out problem2);
                    if (img2 == null) { Log("WAV: tokenizer error: " + problem2); return; }
                    w2 = TapeWav.Encode1350(tape.Substring(0, Math.Min(7, tape.Length)), img2, pc == 1350 ? 0x70 : 0x72);
                    suffix2 = "_PC" + pc;
                }
                string out2 = UniquePath2(Path.Combine(txtDir.Text, baseName + suffix2 + ".wav"));
                File.WriteAllBytes(out2, w2);
                TapeWav.WavInfo wi2; int wo2, wl2; TapeWav.Parse(w2, out wi2, out wo2, out wl2);
                Log("WAV written: " + Path.GetFileName(out2) + " (PC-" + (pc == 500 ? "E500(S)" : pc.ToString()) + ", tape name " + tape.Substring(0, Math.Min(pc == 500 ? 8 : 7, tape.Length)) + ", " + wi2.Seconds.ToString("0") + " s).");
                RefreshFiles();
                int ix2 = lstFiles.Items.IndexOf(Path.GetFileName(out2));
                if (ix2 >= 0) lstFiles.SelectedIndex = ix2;
                return;
            }
            int mode = 1;
            if (curModel == 1)
            {
                mode = AskTapeMode(Path.GetFileName(path));
                if (mode < 0) return;
            }
            byte[] wav;
            if (mode == 0) wav = TapeWav.Encode0(src, tape.Substring(0, Math.Min(8, tape.Length)));
            else
            {
                string problem;
                byte[] img = Pc1600Basic.Tokenize(src, curModel == 0, out problem);
                if (img == null) { Log("WAV: tokenizer error: " + problem); return; }
                if (img.Length == 0 || img[img.Length - 1] != 0xFF) { Array.Resize(ref img, img.Length + 1); img[img.Length - 1] = 0xFF; }
                wav = TapeWav.Encode1(tape.Substring(0, Math.Min(7, tape.Length)), img);
            }
            string suffix = mode == 0 ? "_mode0_CE-1600P" : curModel == 1 ? "_mode1_CE-150" : "_CE-150";
            string outPath = UniquePath2(Path.Combine(txtDir.Text, baseName + suffix + ".wav"));
            File.WriteAllBytes(outPath, wav);
            TapeWav.WavInfo wi; int wo, wl; TapeWav.Parse(wav, out wi, out wo, out wl);
            Log("WAV written: " + Path.GetFileName(outPath) + " (" + (mode == 0 ? "MODE 0, CE-1600P, tape name " + tape.Substring(0, Math.Min(8, tape.Length)) : "CE-150 format, tape name " + tape.Substring(0, Math.Min(7, tape.Length))) + ", " + wi.Seconds.ToString("0") + " s).");
            RefreshFiles();
            int ix = lstFiles.Items.IndexOf(Path.GetFileName(outPath));
            if (ix >= 0) lstFiles.SelectedIndex = ix;
        }

        void WavToBasic(string path)
        {
            byte[] wav = File.ReadAllBytes(path);
            string name = null, text = null, why0 = null, why1 = null;
            string how = null;
            if (curModel == 2)
            {
                string warn2;
                if (curProf <= 3) try          // PC-1350 / PC-1360: the model in the Target computer box decides which tape format is read
                {
                    int idb;
                    byte[] prog = TapeWav.Decode1350(wav, out name, out idb, out warn2);
                    string pr2;
                    text = SharpTok.Detokenize(prog, idb == 0x70 ? 1350 : 1360, out pr2);
                    how = "PC-" + (idb == 0x70 ? "1350" : "1360");
                    if ((curProf == 2 && idb != 0x70) || (curProf == 3 && idb != 0x72)) Log("WAV: the recording is of the PC-" + (idb == 0x70 ? "1350" : "1360") + ", its tokens were used (the selected computer is the PC-" + (curProf == 2 ? "1350" : "1360") + ").");
                    if (warn2 != null) Log("WAV: " + warn2 + ".");
                    if (pr2 != null) Log("WAV: conversion problem: " + pr2);
                }
                catch (Exception ex) { why0 = ex.Message; }
                if (text == null && curProf >= 4)
                {
                    try
                    {
                        byte[] hdr, data;
                        TapeWav.DecodeTape0(wav, true, out hdr, out data);
                        name = Encoding.GetEncoding(28591).GetString(hdr, 1, 13).Trim();
                        if (hdr[0] == 0x04)
                        {
                            int e = Array.IndexOf(data, (byte)0x1A);
                            text = Encoding.GetEncoding(28591).GetString(data, 0, e < 0 ? data.Length : e);
                            how = "PC-E500(S), ASCII file";
                        }
                        else
                        {
                            byte[] img;
                            if (!ProgImage.FindLines(data, out img)) throw new Exception("no BASIC program in the recording (header type " + hdr[0].ToString("X2") + ")");
                            string pr3;
                            text = SharpTok.DetokenizeE500(img, out pr3);
                            how = "PC-E500(S), CSAVE";
                            if (pr3 != null) Log("WAV: conversion problem: " + pr3);
                        }
                    }
                    catch (Exception ex) { why1 = ex.Message; }
                }
                if (text == null) { Log("WAV: the recording cannot be decoded (PC-1350/1360: " + why0 + ") (PC-E500: " + why1 + ")."); return; }
                string base2 = SharpBase(Path.GetFileNameWithoutExtension(path));
                string out3 = UniqueSharp(base2, ".bas");
                File.WriteAllBytes(out3, Encoding.GetEncoding(28591).GetBytes(text));
                Log("WAV decoded (" + how + (string.IsNullOrEmpty(name) ? "" : ", tape name " + name) + ") -> " + Path.GetFileName(out3) + ".");
                RefreshFiles();
                int ix3 = lstFiles.Items.IndexOf(Path.GetFileName(out3));
                if (ix3 >= 0) lstFiles.SelectedIndex = ix3;
                return;
            }
            if (curModel == 1)
            {
                try { text = TapeWav.Decode0(wav, out name); how = "MODE 0 (CE-1600P)"; } catch (Exception ex) { why0 = ex.Message; }
            }
            if (text == null)
            {
                try
                {
                    byte[] prog = TapeWav.Decode1(wav, out name);
                    string problem;
                    text = Pc1600Basic.Detokenize(prog, curModel == 0, out problem);
                    if (problem != null) { Log("WAV: conversion problem: " + problem); }
                    how = "CE-150 format";
                }
                catch (Exception ex) { why1 = ex.Message; }
            }
            if (text == null) { Log("WAV: the recording cannot be decoded" + (why0 != null ? " (MODE 0: " + why0 + ")" : "") + (why1 != null ? " (CE-150 format: " + why1 + ")" : "") + "."); return; }
            string baseName = SharpBase(Path.GetFileNameWithoutExtension(path));
            string outPath = UniqueSharp(baseName, ".bas");
            File.WriteAllBytes(outPath, Encoding.GetEncoding(28591).GetBytes(text));
            Log("WAV decoded (" + how + (string.IsNullOrEmpty(name) ? "" : ", tape name " + name) + ") -> " + Path.GetFileName(outPath) + ".");
            RefreshFiles();
            int ix = lstFiles.Items.IndexOf(Path.GetFileName(outPath));
            if (ix >= 0) lstFiles.SelectedIndex = ix;
        }

        void PlaySelected()
        {
            if (lstFiles.SelectedItem == null) return;
            string path = Path.Combine(txtDir.Text, (string)lstFiles.SelectedItem);
            if (!Path.GetExtension(path).Equals(".wav", StringComparison.OrdinalIgnoreCase)) return;
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (Exception ex) { Log("Play error: " + ex.Message); }
        }

        // two recordings: only the audio data are compared, they are not decoded
        string CompareWavs(string nameA, string pathA, string nameB, string pathB)
        {
            byte[] fa = File.ReadAllBytes(pathA), fb = File.ReadAllBytes(pathB);
            TapeWav.WavInfo ia, ib; int oa, la, ob, lb;
            if (!TapeWav.Parse(fa, out ia, out oa, out la)) throw new Exception(nameA + " is not a PCM WAV file");
            if (!TapeWav.Parse(fb, out ib, out ob, out lb)) throw new Exception(nameB + " is not a PCM WAV file");
            var sb = new StringBuilder();
            sb.AppendLine("A: " + nameA + "   B: " + nameB);
            sb.AppendLine("The recordings are compared as audio data (dates are not compared, the tape is not decoded).");
            sb.AppendLine();
            bool sameFmt = ia.Rate == ib.Rate && ia.Bits == ib.Bits && ia.Channels == ib.Channels;
            int firstDiff = -1;
            if (la == lb) for (int i = 0; i < la; i++) if (fa[oa + i] != fb[ob + i]) { firstDiff = i; break; }
            if (sameFmt && la == lb && firstDiff < 0) { sb.AppendLine("The recordings are IDENTICAL (" + TapeWav.Describe(ia).Replace("\r\n", ", ") + ")."); return sb.ToString().Replace("\n", "\r\n").Replace("\r\r\n", "\r\n"); }
            sb.AppendLine("The recordings are DIFFERENT:");
            sb.AppendLine("  sampling:   A " + ia.Rate + " Hz, B " + ib.Rate + " Hz" + (ia.Rate == ib.Rate ? " (the same)" : " (different)"));
            sb.AppendLine("  resolution: A " + ia.Bits + " bit, " + ia.Channels + " ch; B " + ib.Bits + " bit, " + ib.Channels + " ch" + (ia.Bits == ib.Bits && ia.Channels == ib.Channels ? " (the same)" : " (different)"));
            sb.AppendLine("  duration:   A " + ia.Seconds.ToString("0.00") + " s, B " + ib.Seconds.ToString("0.00") + " s" + (Math.Abs(ia.Seconds - ib.Seconds) < 0.0005 ? " (the same)" : " (different)"));
            sb.AppendLine("  audio data: A " + la.ToString("N0") + " B, B " + lb.ToString("N0") + " B" + (la == lb ? " (the same size)" : " (different size)"));
            if (firstDiff >= 0 && sameFmt) sb.AppendLine("  the first different sample at " + (firstDiff / (double)(ia.Channels * (ia.Bits / 8)) / ia.Rate).ToString("0.000") + " s");
            else if (la == lb && !sameFmt) sb.AppendLine("  the audio data have the same size but another format");
            return sb.ToString().Replace("\n", "\r\n").Replace("\r\r\n", "\r\n");
        }

        void ViewSelected()
        {
            if (lstFiles.SelectedItem == null) return;
            string name = (string)lstFiles.SelectedItem;
            if (Path.GetExtension(name).Equals(".wav", StringComparison.OrdinalIgnoreCase)) return;
            try
            {
                string path = Path.Combine(txtDir.Text, name);
                byte[] data = File.ReadAllBytes(path);
                string ext = Path.GetExtension(path).ToLowerInvariant();
                string text, problem;
                string imgErr = null;
                if (ext == ".img" || (ext == ".bin" && ProgImage.CheckImage(data, out imgErr)))
                {
                    text = Pc1600Basic.Detokenize(data, radio1500.Checked, out problem);
                    if (problem != null) text += "\r\n[" + problem + "]";
                    // program image: the user chooses between the original bytes and the detokenized BASIC text
                    new PreviewForm(name + " (" + data.Length + " B)", text, HexDump(data)).Show(this);
                    return;
                }
                else
                {
                    int ctl = 0;
                    foreach (byte b in data) if (b < 9 || (b > 13 && b < 32 && b != 26) ) ctl++;
                    if (ctl > data.Length / 20) text = HexDump(data);
                    else text = Encoding.GetEncoding(28591).GetString(data).Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n").Replace("\u001A", "");
                }
                if (text.Length > 300000) text = text.Substring(0, 300000) + "\r\n[... cut]";
                new PreviewForm(name + " (" + data.Length + " B)", text).Show(this);
            }
            catch (Exception ex) { Log("View error: " + ex.Message); }
        }

        internal static string HexDump(byte[] d)
        {
            var sb = new StringBuilder();
            int n = Math.Min(d.Length, 16384);
            for (int i = 0; i < n; i += 16)
            {
                sb.Append(i.ToString("X4")).Append("  ");
                for (int j = 0; j < 16; j++) sb.Append(i + j < n ? d[i + j].ToString("X2") + " " : "   ");
                sb.Append(" ");
                for (int j = 0; j < 16 && i + j < n; j++) sb.Append(d[i + j] >= 32 && d[i + j] < 127 ? (char)d[i + j] : '.');
                sb.Append("\r\n");
            }
            if (d.Length > n) sb.Append("[... cut]");
            return sb.ToString();
        }

        // View and Send selected are available only when a file is selected (Send also not while a transfer runs)
        // Send selected and Send file are active (not grey) only while the port is open and nothing is being sent
        void UpdateSendButtons()
        {
            bool open = port != null && port.IsOpen;
            if (btnSendOther != null) btnSendOther.Enabled = open && !sending;
            SelectionChanged();
        }

        void SelectionChanged()
        {
            bool sel = lstFiles.SelectedItem != null;
            string selExt = sel ? Path.GetExtension((string)lstFiles.SelectedItem).ToLowerInvariant() : "";
            if (btnView != null) btnView.Enabled = sel && (selExt == ".bas" || selExt == ".txt" || selExt == ".wav");       // the WAV converter
            if (btnSend != null) btnSend.Enabled = sel && selExt != ".wav" && !sending && port != null && port.IsOpen;
            if (btnDelete != null) btnDelete.Enabled = sel;
            if (btnConvert != null)
            {
                string ext = sel ? Path.GetExtension((string)lstFiles.SelectedItem).ToLowerInvariant() : "";
                btnConvert.Enabled = sel && !rs232 && (ext == ".bas" || ext == ".txt" || ext == ".img" || ext == ".bin");   // Software UART mode only
            }
        }

        void EditSelected()
        {
            if (lstFiles.SelectedItem == null) return;
            string path = Path.Combine(txtDir.Text, (string)lstFiles.SelectedItem);
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".img" || ext == ".bin") { Log("A program image cannot be edited as text: use Convert to get the BASIC text (.bas), then Edit."); return; }
            try
            {
                var ed = new EditorForm(path);
                ed.FormClosed += delegate { RefreshFiles(); };
                ed.Show(this);
            }
            catch (Exception ex) { Log("Edit error: " + ex.Message); }
        }

        void RenameSelected()
        {
            if (lstFiles.SelectedItem == null) return;
            string oldName = (string)lstFiles.SelectedItem;
            string newName;
            using (var d = new PromptForm("Rename", "New name of the file:", oldName))
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                newName = d.Value;
            }
            if (newName.Length == 0 || newName == oldName) return;
            if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) { Log("Rename: the name contains characters that are not allowed in a file name."); return; }
            try
            {
                string from = Path.Combine(txtDir.Text, oldName), to = Path.Combine(txtDir.Text, newName);
                if (File.Exists(to) && !string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) { Log("Rename: a file named " + newName + " already exists."); return; }
                File.Move(from, to);
                Log("Renamed " + oldName + " to " + newName);
            }
            catch (Exception ex) { Log("Rename error: " + ex.Message); }
            RefreshFiles();
            int i = lstFiles.Items.IndexOf(newName);
            if (i >= 0) lstFiles.SelectedIndex = i;
        }

        // Port test: the port is closed first; the window sends a pattern at 300 to 115200 bps (TXD must be connected to RXD)
        void TestPort()
        {
            if (comboPorts.SelectedItem == null) { Log("Port test: no port selected."); return; }
            if (sending) { Log("Port test: wait until the transfer is finished."); return; }
            string name = comboPorts.SelectedItem.ToString();
            bool wasOpen = port != null;
            if (wasOpen) { ClosePort(); Log("Port " + name + " closed for the port test."); }
            string result;
            using (var f = new PortTestForm(name, rs232 ? comboFlow.SelectedIndex : 0))
            {
                f.ShowDialog(this);
                result = f.Summary;
            }
            if (result != null) Log("Port test " + name + ": " + result + ".");
            if (wasOpen) { comboPorts.SelectedItem = name; TogglePort(); }          // the port is open again, as it was before the test
        }

        // Renumber: the line numbers of a BASIC text file and the GOTO / GOSUB / THEN ... targets
        void RenumberSelected()
        {
            if (lstFiles.SelectedItem == null) return;
            string name = (string)lstFiles.SelectedItem;
            try
            {
                string path = Path.Combine(txtDir.Text, name);
                string text = Encoding.GetEncoding(28591).GetString(File.ReadAllBytes(path));
                int maxLine = BasicCheck.Profiles[BasicCheck.ProfileOfTarget(curModel < 0 ? 1 : curModel)].MaxLine;
                int count = BasicTools.CountNumbered(text);
                if (count == 0) { Log("Renumber " + name + ": the file has no numbered lines."); return; }
                int step, start;
                using (var d = new RenumberForm(name, count, maxLine))
                {
                    if (d.ShowDialog(this) != DialogResult.OK) return;
                    step = d.Step; start = d.Start;
                }
                RenumberResult r = BasicTools.Renumber(text, start, step, maxLine);
                if (r.Error != null) { Log("Renumber " + name + ": " + r.Error); return; }
                if (r.Overflow)
                {
                    int alt = 0;
                    foreach (int a in new[] { 5, 2, 1 })
                        if (a < step && start + (long)(r.Count - 1) * a <= maxLine) { alt = a; break; }
                    if (alt == 0)
                    {
                        Log("Renumber " + name + ": " + r.Count + " lines from " + start + " do not fit below " + maxLine + " even with step 1; nothing changed.");
                        return;
                    }
                    long lastAlt = start + (long)(r.Count - 1) * alt;
                    if (MessageBox.Show(this, "With the step " + step + " the last line would be " + r.NewLast + " (the highest allowed line number is " + maxLine + ").\r\n\r\nUse the step " + alt + " instead (the last line " + lastAlt + ")?",
                        "Renumber", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) { Log("Renumber " + name + ": cancelled (the step " + step + " is too large)."); return; }
                    step = alt;
                    r = BasicTools.Renumber(text, start, step, maxLine);
                    if (r.Error != null || r.Overflow) { Log("Renumber " + name + ": failed."); return; }
                }
                File.WriteAllBytes(path, Encoding.GetEncoding(28591).GetBytes(r.Text));
                Log("Renumber " + name + ": " + r.Count + " lines, was " + r.OldFirst + " to " + r.OldLast + ", now " + r.NewFirst + " to " + r.NewLast + " (start " + start + ", step " + step + "); " + r.RefsChanged + " line references changed.");
                if (r.Missing.Count > 0) Log("Renumber " + name + ": references to lines that do not exist were left as they are: " + string.Join(", ", r.Missing.ToArray()) + ".");
            }
            catch (Exception ex) { Log("Renumber error: " + ex.Message); }
            RefreshFiles();
            int i = lstFiles.Items.IndexOf(name);
            if (i >= 0) lstFiles.SelectedIndex = i;
        }

        // Remove REM: writes NAME_NOREM.ext without the comments
        void RemoveRemSelected()
        {
            if (lstFiles.SelectedItem == null) return;
            string name = (string)lstFiles.SelectedItem;
            try
            {
                string path = Path.Combine(txtDir.Text, name);
                string text = Encoding.GetEncoding(28591).GetString(File.ReadAllBytes(path));
int removed, kept, cut; List<string> moved;
                bool keepFirst, targets;
                using (var d = new RemoveRemForm(name))
                {
                    if (d.ShowDialog(this) != DialogResult.OK) return;
                    keepFirst = d.KeepFirst; targets = d.RemoveTargets;
                }
                string res = BasicTools.RemoveRem(text, keepFirst, targets, out removed, out kept, out cut, out moved);
                string outName = Path.GetFileNameWithoutExtension(name) + "_NOREM" + Path.GetExtension(name);
                string outPath = Path.Combine(txtDir.Text, outName);
                if (File.Exists(outPath) && MessageBox.Show(this, outName + " already exists. Replace it?", "Remove REM", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                { Log("Remove REM " + name + ": cancelled."); return; }
                byte[] bytes = Encoding.GetEncoding(28591).GetBytes(res);
                File.WriteAllBytes(outPath, bytes);
                int before = File.ReadAllBytes(path).Length;
                Log("Remove REM " + name + ": " + removed + " REM lines removed, " + cut + " comments cut off the ends of lines" + (kept > 0 ? ", " + kept + " REM lines kept" + (keepFirst ? " (the first one as it is" + (targets ? "" : ", a REM that a GOTO / GOSUB points to as a bare REM") + ")" : (targets ? "" : " as a bare REM (a GOTO / GOSUB / THEN points to them)")) : "") +
                    "; " + before + " to " + bytes.Length + " bytes. Written: " + outName + ".");
                if (moved.Count > 0) Log("Remove REM " + name + ": jumps moved to the next line: " + string.Join(", ", moved.ToArray()) + ".");
                RefreshFiles();
                int i = lstFiles.Items.IndexOf(outName);
                if (i >= 0) lstFiles.SelectedIndex = i;
            }
            catch (Exception ex) { Log("Remove REM error: " + ex.Message); }
        }

        // Check Basic: a basic check of the listing for the machine of the selected target (another machine can be chosen in the window)
        void CheckBasicSelected()
        {
            if (lstFiles.SelectedItem == null) return;
            string name = (string)lstFiles.SelectedItem;
            try
            {
                string path = Path.Combine(txtDir.Text, name);
                byte[] data = File.ReadAllBytes(path);
                string ext = Path.GetExtension(path).ToLowerInvariant();
                int profile = BasicCheck.ProfileOfTarget(curModel < 0 ? 1 : curModel);
                string text;
                if (ext == ".img" || (ext == ".bin" && ProgImage.CheckImage(data, out text)))
                {
                    string problem;
                    text = Pc1600Basic.Detokenize(data, profile == 0, out problem);
                    if (problem != null) { Log("Check Basic: the program image cannot be read (" + problem + ")."); return; }
                }
                else text = Encoding.GetEncoding(28591).GetString(data);
                new CheckBasicForm(name, text, profile).Show(this);
            }
            catch (Exception ex) { Log("Check Basic error: " + ex.Message); }
        }

        void DeleteSelected()
        {
            if (lstFiles.SelectedItem == null) { Log("Select a file in the list."); return; }
            string name = (string)lstFiles.SelectedItem;
            using (var d = new DeleteConfirmForm(name))
                if (d.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                File.Delete(Path.Combine(txtDir.Text, name));
                Log("Deleted " + name);
            }
            catch (Exception ex) { Log("Delete error: " + ex.Message); }
            RefreshFiles();
        }

        static string UniquePathIn(string dir, string name, string ext)
        {
            string path = Path.Combine(dir, name + ext);
            for (int i = 2; File.Exists(path); i++) path = Path.Combine(dir, name + "_" + i + ext);
            return path;
        }

        // forced stop of a transfer
        void Abort()
        {
            abortRequested = true;
            MarkSending(null);
            try { if (port != null && port.IsOpen) { port.DiscardOutBuffer(); port.DiscardInBuffer(); } } catch { }
            lock (rxLock) { rx.Clear(); }
            Log("ABORT: transmission stopped, buffers cleared.");
        }

        int progStart, progEnd;
        string progText;

        // keeps one log line up to date while a file is being sent: "SENDING name (n B) (37%)"
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        const int WM_SETREDRAW = 0x000B, WM_VSCROLL = 0x0115, SB_BOTTOM = 7;

        // scroll the log to its very end (the last line at the bottom edge of the box)
        void ScrollLogToEnd()
        {
            SendMessage(txtLog.Handle, WM_VSCROLL, (IntPtr)SB_BOTTOM, IntPtr.Zero);
        }

        void SetProgress(int pct)
        {
            if (txtLog.TextLength != progEnd) return;      // other messages were logged meanwhile: leave them alone
            string line = "\r\n" + DateTime.Now.ToString("HH:mm:ss") + "  " + progText + " (" + pct + "%)";
            // no redraw while the last line is replaced: otherwise the log jumps up and down (the selection scrolls it)
            SendMessage(txtLog.Handle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
            try
            {
                txtLog.Select(progStart, txtLog.TextLength - progStart);
                txtLog.SelectionFont = txtLog.Font; txtLog.SelectionColor = txtLog.ForeColor;
                txtLog.SelectedText = line;
                progEnd = txtLog.TextLength;
                txtLog.SelectionStart = txtLog.TextLength;
                ScrollLogToEnd();
            }
            finally
            {
                SendMessage(txtLog.Handle, WM_SETREDRAW, (IntPtr)1, IntPtr.Zero);
                txtLog.Invalidate();
            }
        }

        async void SendFile(string path)
        {
            if (port == null || !port.IsOpen) { Log("Open the port first."); return; }
            if (sending) return;
            if (term != null) { Log("Close the terminal first."); return; }
            if (Path.GetExtension(path).Equals(".wav", StringComparison.OrdinalIgnoreCase)) { Log("A WAV recording cannot be sent through the port (convert it to BASIC text with the WAV button first)."); return; }
            // before any sending: empty the receive buffers, so that they are ready for the answers of the Sharp
            lock (rxLock) { rx.Clear(); rxStartLogged = false; }
            try { port.DiscardInBuffer(); } catch { }
            byte[] data;
            try { data = File.ReadAllBytes(path); }
            catch (Exception ex) { Log("Read error: " + ex.Message); return; }
            bool text = rs232;
            int delay = (int)numDelay.Value;
            bool eof = chkEof.Checked;
            bool dsrFlow = rs232 && comboFlow.SelectedIndex == 3;
            int eolIdx = comboEol.SelectedIndex;
            byte[] eol = eolIdx == 1 ? new byte[] { 13, 10 } : eolIdx == 2 ? new byte[] { 10 } : new byte[] { 13 };
            if (text)
            {
                var latin = Encoding.GetEncoding(28591);
                string src = latin.GetString(data);
                if (curModel == 1)
                {
                    // the PC-1600 keeps the space after REM as the first character of the comment and its listing adds one more: send REM without it
                    string fx = FixRemSpace(src);
                    if (fx != src) { src = fx; data = latin.GetBytes(src); Log("PC-1600: the space after REM is left out (the Sharp would show two)."); }
                }
                if (chkSkipRem.Checked)
                {
                    int rm, kp;
                    src = StripRemLines(src, out rm, out kp);
                    data = latin.GetBytes(src);
                    Log("Skip REM: " + rm + " comment lines left out" + (kp > 0 ? ", " + kp + " kept (a GOTO / GOSUB points to them)" : "") + ".");
                }
                if (curModel <= 1)                       // the size the pocket computer will need (estimated with the tokenizer)
                {
                    string pr;
                    try { byte[] est = Pc1600Basic.Tokenize(src, curModel == 0, out pr); if (est != null) WarnSize(est.Length + 1, false); } catch { }
                }
            }
            if (!text)
            {
                string err;
                byte[] img;
                if (path.EndsWith(".img", StringComparison.OrdinalIgnoreCase)) { img = data; if (chkSkipRem.Checked) Log("Skip REM is not applied to program images (.img)."); }
                else
                {
                    if (!chkAuto.Checked) { Log("Auto token is off: only .img files can be sent (use Convert or switch Auto token on)."); return; }
                    // BASIC text: convert to the intermediate code of the Sharp first
                    Log("Tokenizing to the " + (radio1500.Checked ? "PC-1500" : "PC-1600") + " intermediate code format...");
                    string problem;
                    string src = Encoding.GetEncoding(28591).GetString(data);
                    if (chkSkipRem.Checked)
                    {
                        int rm, kp;
                        src = StripRemLines(src, out rm, out kp);
                        Log("Skip REM: " + rm + " comment lines left out" + (kp > 0 ? ", " + kp + " kept (a GOTO / GOSUB points to them)" : "") + ".");
                    }
                    img = Pc1600Basic.Tokenize(src, radio1500.Checked, out problem);
                    if (img == null) { Log("Tokenizer error: " + problem); return; }
                    Log("Tokenized: " + ProgImage.CountLines(img) + " lines, " + img.Length + " B (program image).");
                }
                WarnSize(img.Length + 1, true);
                if (!ProgImage.CheckImage(img, out err)) { Log("File rejected: " + err); return; }
                data = ProgImage.StreamFromImage(img);       // the Software UART sends the image + a checksum byte
            }
            abortRequested = false;
            sending = true;
            try
            {
                MarkSending(path);
                btnSend.Enabled = btnSendOther.Enabled = false;
                CheckSharpName(path);
                progText = "SENDING " + Path.GetFileName(path) + " (" + data.Length + " B)";
                progStart = txtLog.TextLength;
                Log(progText + " (0%)");
                progEnd = txtLog.TextLength;
            }
            catch (Exception ex)
            {
                // the flag must never stay on: while it is on the received data are dropped (nothing would be received any more)
                Log("Send error: " + ex.Message);
                sending = false; MarkSending(null); UpdateSendButtons();
                return;
            }
            int total = data.Length, lastPct = -1;
            Action<int> report = pct =>
            {
                if (pct == lastPct) return;
                lastPct = pct;
                try { BeginInvoke(new Action(() => SetProgress(pct))); } catch { }
            };
            try
            {
                var p = port;
                await Task.Run(() =>
                {
                  try
                  {
                    p.DiscardInBuffer();
                    if (!text)
                    {
                        // in small pieces: Abort stops between two pieces, without an aborted Write (the receiver of the Sharp
                        // does not mind small gaps: it waits up to 0.5 s for the next byte)
                        int off = 0;
                        while (off < data.Length && !abortRequested)
                        {
                            int len = Math.Min(128, data.Length - off);
                            p.Write(data, off, len);
                            off += len;
                            while (p.BytesToWrite > 256 && !abortRequested) System.Threading.Thread.Sleep(5);
                            report(100 * Math.Max(0, off - p.BytesToWrite) / total);
                        }
                        while (p.BytesToWrite > 0 && !abortRequested)
                        {
                            report(100 * Math.Max(0, total - p.BytesToWrite) / total);
                            System.Threading.Thread.Sleep(20);
                        }
                    }
                    else
                    {
                        // text: line by line, every line ends with the selected line end
                        var line = new List<byte>();
                        for (int i = 0; i <= data.Length && !abortRequested; i++)
                        {
                            if (i == data.Length || data[i] == 0x0D || data[i] == 0x0A)
                            {
                                if (line.Count > 0)
                                {
                                    line.AddRange(eol);
                                    if (dsrFlow)
                                    {
                                        // DSR/DTR: wait (up to 10 s) while the Sharp does not hold DSR
                                        for (int w = 0; w < 10000 && !p.DsrHolding && !abortRequested; w += 10) System.Threading.Thread.Sleep(10);
                                    }
                                    p.Write(line.ToArray(), 0, line.Count);
                                    line.Clear();
                                    while (p.BytesToWrite > 0 && !abortRequested) System.Threading.Thread.Sleep(5);
                                    report(100 * i / total);
                                    for (int w = 0; w < delay && !abortRequested; w += 10) System.Threading.Thread.Sleep(10);
                                }
                            }
                            else line.Add(data[i]);
                        }
                    }
                    if (abortRequested) return;
                    if (text && eof) p.Write(new byte[] { 0x1A }, 0, 1);
                    while (p.BytesToWrite > 0) System.Threading.Thread.Sleep(10);
                    report(100);
                  }
                  catch (Exception)
                  {
                    // Abort discards the buffers while Write waits: the port then throws an IOException ("the I/O operation has been
                    // aborted ..."). That is expected after Abort and must not stop the program; any other error is reported
                    if (!abortRequested) throw;
                  }
                });
                if (abortRequested) Log("Transmission aborted."); else Log(text ? "Sent." : "Sent. The PC-1600 finishes after 0.5 s of silence; check the program with LIST.");
            }
            catch (Exception ex) { Log("Send error: " + ex.Message); }
            finally
            {
                // PC-1600 over the UART: 1 s pause after the end of the transmission, before the port is reset (the controls stay locked)
                if (text && curModel == 1 && port != null && !abortRequested)
                {
                    try { await Task.Delay(1000); } catch { }
                }
                lock (rxLock) { rx.Clear(); }
                sending = false;
                MarkSending(null);
                UpdateSendButtons();
                if (text && curModel == 1 && port != null) ResetPort();      // PC-1600 over the UART: the port is closed and opened again after the transmission (or Abort)
            }
        }
    }

    // Program image (.img): the BASIC program as it is in the memory of the Sharp (intermediate code), like the .img of the
    // Pocket Tools: lines [line number 2 B][length 1 B][content, ends with 0D], no header, no checksum; a trailing FFh
    // (end marker) is accepted. The Software UART (Saver/Loader) stream is the image + 1 checksum byte (sum mod 256).
    public static class ProgImage
    {
        // the length of the image without a trailing end marker
        static int Len(byte[] s) { return s.Length > 0 && s[s.Length - 1] == 0xFF ? s.Length - 1 : s.Length; }

        // image only (no checksum)
        public static bool CheckImage(byte[] s, out string err)
        {
            err = null;
            int n = Len(s);
            if (n < 4) { err = "image too short (empty program?)"; return false; }
            int p = 0;
            while (p < n)
            {
                if (p + 3 > n) { err = "truncated line"; return false; }
                int len = s[p + 2];
                if (len < 2 || p + 3 + len > n) { err = "bad line length at byte " + p; return false; }
                if (s[p + 2 + len] != 0x0D) { err = "missing CR at the end of the line at byte " + p; return false; }
                p += 3 + len;
            }
            return true;
        }

        // the stream of the Software UART: image + checksum
        public static bool CheckStream(byte[] s, out string err)
        {
            err = null;
            if (s.Length < 2) { err = "stream too short (empty program?)"; return false; }
            int n = s.Length - 1, sum = 0;
            for (int i = 0; i < n; i++) sum += s[i];
            if ((sum & 0xFF) != s[n]) { err = "bad checksum"; return false; }
            var img = new byte[n];
            Array.Copy(s, img, n);
            return CheckImage(img, out err);
        }

        // a stream that is not an image + checksum: the lines up to an end marker FFh (at the place of a line number) are the program
        public static bool Salvage(byte[] s, out byte[] img)
        {
            img = null;
            int p = 0, lines = 0;
            while (p + 3 <= s.Length && s[p] != 0xFF)
            {
                int len = s[p + 2];
                if (len < 2 || p + 3 + len > s.Length || s[p + 2 + len] != 0x0D) return false;
                p += 3 + len; lines++;
            }
            if (lines == 0 || p >= s.Length || s[p] != 0xFF) return false;
            img = new byte[p];
            Array.Copy(s, img, p);
            return true;
        }

        // a stream of program lines [number 2 B][length 1 B][content, ends with 0D] found anywhere in the first 64 bytes (a text mode SAVE
        // without ,A sends the program as it is in the memory); at least 3 lines that fit exactly, up to the end marker FFh or the end of the data
        public static bool FindLines(byte[] s, out byte[] img)
        {
            img = null;
            for (int start = 0; start <= Math.Min(64, s.Length - 8); start++)
            {
                int p = start, lines = 0;
                while (p + 3 <= s.Length && s[p] != 0xFF)
                {
                    int len = s[p + 2];
                    if (len < 2 || p + 3 + len > s.Length || s[p + 2 + len] != 0x0D) break;
                    p += 3 + len; lines++;
                }
                bool ended = p >= s.Length || s[p] == 0xFF || s[p] == 0x1A || s[p] == 0x00;
                if (lines >= 3 && ended)
                {
                    img = new byte[p - start];
                    Array.Copy(s, start, img, 0, img.Length);
                    return true;
                }
            }
            return false;
        }

        public static byte[] ImageFromStream(byte[] s)
        {
            var img = new byte[s.Length - 1];
            Array.Copy(s, img, img.Length);
            return img;
        }

        public static byte[] StreamFromImage(byte[] img)
        {
            int n = Len(img), sum = 0;
            var s = new byte[n + 1];
            for (int i = 0; i < n; i++) { s[i] = img[i]; sum += img[i]; }
            s[n] = (byte)(sum & 255);
            return s;
        }

        public static int CountLines(byte[] s)
        {
            int n = Len(s), p = 0, c = 0;
            while (p + 3 <= n) { p += 3 + s[p + 2]; c++; }
            return c;
        }

        // name taken from the first line that starts with REM (token F1 AB)
        public static string NameFromRem(byte[] s)
        {
            int n = Len(s), p = 0;
            while (p + 3 <= n)
            {
                int len = s[p + 2];
                int c = p + 3;
                if (len >= 3 && s[c] == 0xF1 && s[c + 1] == 0xAB)
                {
                    var sb = new StringBuilder();
                    for (int i = c + 2; i < p + 2 + len; i++) sb.Append((char)s[i]);
                    string t = Clean(sb.ToString());
                    if (t.Length > 0) return t;
                }
                p += 3 + len;
            }
            return null;
        }

        // name taken from the first "[number] REM text" line of an ASCII listing
        public static string NameFromText(byte[] s)
        {
            var line = new StringBuilder();
            for (int i = 0; i <= s.Length; i++)
            {
                if (i == s.Length || s[i] == 0x0D || s[i] == 0x0A)
                {
                    string t = line.ToString().TrimStart(' ', '0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
                    line.Length = 0;
                    if (t.StartsWith("REM", StringComparison.OrdinalIgnoreCase))
                    {
                        string name = Clean(t.Substring(3).TrimStart(' ', '\''));
                        if (name.Length > 0) return name;
                    }
                }
                else line.Append((char)s[i]);
            }
            return null;
        }

        public static string Clean(string t)
        {
            var bad = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder();
            foreach (char ch in t.Trim())
                sb.Append(ch < 32 || ch > 126 || Array.IndexOf(bad, ch) >= 0 ? '_' : ch);
            string r = sb.ToString().Trim(' ', '.', '_');
            if (r.Length > 40) r = r.Substring(0, 40).TrimEnd(' ', '.');
            return r;
        }
    }

    // Small icons drawn in code (no image files needed)
    public static class Icons
    {
        static Bitmap Make(Action<Graphics, Pen, Brush> draw)
        {
            var b = new Bitmap(20, 20);
            using (var g = Graphics.FromImage(b))
            using (var pen = new Pen(Color.FromArgb(40, 70, 120), 2f))
            using (var br = new SolidBrush(Color.FromArgb(40, 70, 120)))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                draw(g, pen, br);
            }
            return b;
        }

        public static Bitmap Refresh() { return Make((g, p, b) => { g.DrawArc(p, 4, 4, 12, 12, 30, 290); g.FillPolygon(b, new[] { new Point(18, 3), new Point(18, 11), new Point(10, 8) }); }); }
        public static Bitmap Plug() { return Make((g, p, b) => { g.FillRectangle(b, 5, 9, 10, 7); g.DrawLine(p, 8, 3, 8, 9); g.DrawLine(p, 12, 3, 12, 9); }); }
        public static Bitmap Cross() { return Make((g, p, b) => { g.DrawLine(p, 5, 5, 15, 15); g.DrawLine(p, 15, 5, 5, 15); }); }
        public static Bitmap Folder() { return Make((g, p, b) => { g.FillRectangle(b, 2, 6, 16, 11); g.FillRectangle(b, 2, 4, 7, 3); }); }
        public static Bitmap Terminal() { return Make((g, p, b) => { g.DrawRectangle(p, 2, 3, 16, 13); g.DrawLine(p, 5, 7, 8, 10); g.DrawLine(p, 5, 13, 8, 10); g.DrawLine(p, 10, 13, 14, 13); }); }
        public static Bitmap Flush() { return Make((g, p, b) => { g.FillPolygon(b, new[] { new Point(3, 3), new Point(17, 3), new Point(11, 10), new Point(11, 14), new Point(9, 14), new Point(9, 10) }); g.FillEllipse(b, 8, 15, 4, 4); }); }   // funnel + drop
        public static Bitmap Plane() { return Make((g, p, b) => { g.FillPolygon(b, new[] { new Point(2, 10), new Point(18, 3), new Point(13, 17), new Point(10, 12) }); }); }
        public static Bitmap Note() { return Make((g, p, b) => { g.TranslateTransform(10, 8.5f); g.ScaleTransform(0.8f, 0.8f); g.TranslateTransform(-10, -10); g.FillEllipse(b, 4, 12, 8, 6); g.DrawLine(p, 11, 14, 11, 3); g.DrawLine(p, 11, 3, 16, 6); }); }   // 20 % smaller
        public static Bitmap File() { return Make((g, p, b) => { g.DrawRectangle(p, 5, 2, 10, 16); g.DrawLine(p, 8, 8, 12, 8); g.DrawLine(p, 8, 12, 12, 12); }); }
        // shooting star (yellow star head, curved light-blue forked tail) on a navy sky, drawn at any size
        public static Bitmap Comet(int size)
        {
            var bmp = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                g.ScaleTransform(size / 32F, size / 32F);
                using (var b = new SolidBrush(Color.FromArgb(18, 40, 80))) g.FillEllipse(b, 0, 0, 31, 31);
                using (var b = new SolidBrush(Color.White)) { g.FillRectangle(b, 5, 22, 1, 1); g.FillRectangle(b, 24, 5, 1, 1); g.FillRectangle(b, 12, 26, 1, 1); }

                using (var tail = new GraphicsPath())
                {
                    tail.AddBezier(3, 9, 10, 4, 19, 7, 22, 19);        // upper edge
                    tail.AddLine(22, 19, 21, 23);
                    tail.AddBezier(21, 23, 17, 12, 10, 12, 6, 18);     // lower edge
                    tail.AddLine(6, 18, 8, 12.5F);                      // fork
                    tail.CloseFigure();
                    using (var b = new SolidBrush(Color.FromArgb(135, 205, 255))) g.FillPath(b, tail);
                }

                var star = new PointF[10];
                for (int i = 0; i < 10; i++)
                {
                    double ang = -Math.PI / 2 + i * Math.PI / 5 + 0.2;
                    double r = i % 2 == 0 ? 7.0 : 3.0;
                    star[i] = new PointF((float)(23 + r * Math.Cos(ang)), (float)(22 + r * Math.Sin(ang)));
                }
                using (var b = new SolidBrush(Color.FromArgb(255, 226, 100))) g.FillPolygon(b, star);
            }
            return bmp;
        }

        // application icon (window title bar and taskbar)
        public static Icon AppIcon()
        {
            using (var bmp = Comet(32)) return Icon.FromHandle(bmp.GetHicon());
        }
    }

    // Small status indicator: green LED + "Open" / dark (off) LED + "Closed"
    public class LedLabel : Control
    {
        bool open;
        public bool IsOpen { get { return open; } set { open = value; Invalidate(); } }

        public LedLabel() { DoubleBuffered = true; }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(4, (Height - 16) / 2, 16, 16);
            using (var b = new SolidBrush(open ? Color.FromArgb(60, 225, 90) : Color.FromArgb(15, 15, 15))) g.FillEllipse(b, r);
            using (var p = new Pen(open ? Color.FromArgb(20, 120, 40) : Color.FromArgb(110, 110, 110), 1.5f)) g.DrawEllipse(p, r);
            if (open) using (var b = new SolidBrush(Color.FromArgb(200, 255, 210))) g.FillEllipse(b, 8, r.Top + 3, 4, 4);   // highlight
            using (var f = new Font("Segoe UI", 9F, FontStyle.Bold))
            using (var b = new SolidBrush(open ? Color.FromArgb(20, 120, 40) : Color.FromArgb(60, 60, 60)))
                g.DrawString(open ? "Open" : "Closed", f, b, 26, (Height - 16) / 2);
        }
    }

    // Banner: PC <-> PC-1600
    public class BannerPanel : Panel
    {
        public bool PortOpen, Sending, Receiving, Blink, FlashWhite;   // Blink toggles ~5x per second while data flows
        Bitmap cometImg;
        public string ModelName = "Sharp PC-1600";
        public int Prof = 1;                  // the profile (see NProf in Form1): the Sharp (other) models have their own pictures
        public int Model = 1;                 // 0 PC-1500, 1 PC-1600, 2 other Sharp (15-pin, drawing of a PC-E500), 3 Casio FX-850/870, 4 TRS-80 / NEC, 5 other UART computer

        public BannerPanel() { DoubleBuffered = true; ResizeRedraw = true; }

        static GraphicsPath Round(float x, float y, float w, float h, float r)
        {
            var gp = new GraphicsPath();
            gp.AddArc(x, y, 2 * r, 2 * r, 180, 90);
            gp.AddArc(x + w - 2 * r, y, 2 * r, 2 * r, 270, 90);
            gp.AddArc(x + w - 2 * r, y + h - 2 * r, 2 * r, 2 * r, 0, 90);
            gp.AddArc(x, y + h - 2 * r, 2 * r, 2 * r, 90, 90);
            gp.CloseFigure();
            return gp;
        }

        static void FillRound(Graphics g, Color c, float x, float y, float w, float h, float r)
        {
            using (var gp = Round(x, y, w, h, r)) using (var b = new SolidBrush(c)) g.FillPath(b, gp);
        }

        static void Text(Graphics g, string s, Font f, Color c, float cx, float y)
        {
            using (var b = new SolidBrush(c))
            {
                SizeF sz = g.MeasureString(s, f);
                g.DrawString(s, f, b, cx - sz.Width / 2, y);
            }
        }

        static void Arrow(Graphics g, int x1, int x2, int y, bool right, bool active, bool blink, bool white, bool open)
        {
            Color c = white ? Color.White : active && blink ? Color.FromArgb(255, 150, 30) : open ? Color.FromArgb(102, 162, 215) : Color.FromArgb(46, 87, 135);   // port open: lighter blue, closed: darker blue; orange flash while data flows
            Point[] pts = right
                ? new[] { new Point(x1, y - 5), new Point(x2 - 22, y - 5), new Point(x2 - 22, y - 12), new Point(x2, y), new Point(x2 - 22, y + 12), new Point(x2 - 22, y + 5), new Point(x1, y + 5) }
                : new[] { new Point(x2, y - 5), new Point(x1 + 22, y - 5), new Point(x1 + 22, y - 12), new Point(x1, y), new Point(x1 + 22, y + 12), new Point(x1 + 22, y + 5), new Point(x2, y + 5) };
            using (var b = new SolidBrush(c)) g.FillPolygon(b, pts);
        }

        static void Led(Graphics g, Color c, int cx, int y)
        {
            using (var b = new SolidBrush(c)) g.FillEllipse(b, cx - 5, y, 10, 10);
        }

        // Sharp PC-1500: light body, a brown band over the whole width with a narrow one-line display and the logo,
        // a row of small keys, the QWERTY keys and a numeric pad with a red CL key
        static void DrawPc1500(Graphics g, int kx, int top)
        {
            var state = g.Save();
            g.TranslateTransform(kx, top + 60);
            g.ScaleTransform(0.7F, 0.7F);
            g.TranslateTransform(-kx, -(top + 60));
            FillRound(g, Color.FromArgb(150, 150, 146), kx - 86, top + 10, 172, 82, 6);
            FillRound(g, Color.FromArgb(226, 226, 220), kx - 85, top + 8, 170, 82, 6);
            using (var b = new SolidBrush(Color.FromArgb(92, 66, 52))) g.FillRectangle(b, kx - 85, top + 12, 170, 24);        // brown band
            using (var b = new SolidBrush(Color.FromArgb(176, 194, 164))) g.FillRectangle(b, kx - 66, top + 19, 116, 10);   // one-line display
            using (var f = new Font("Arial", 5.5F, FontStyle.Bold)) Text(g, "SHARP", f, Color.FromArgb(235, 230, 220), kx + 68, top + 18);
            using (var bg = new SolidBrush(Color.FromArgb(190, 190, 184)))
            using (var bl = new SolidBrush(Color.FromArgb(246, 246, 240)))
            using (var bd = new SolidBrush(Color.FromArgb(80, 80, 84)))
            using (var br = new SolidBrush(Color.FromArgb(200, 50, 45)))
            using (var bo = new SolidBrush(Color.FromArgb(225, 130, 40)))
            {
                for (int i = 0; i < 6; i++) g.FillRectangle(bg, kx - 76 + i * 13, top + 41, 10, 4);                // row of small keys
                g.FillRectangle(bd, kx + 34, top + 41, 12, 4);
                g.FillRectangle(br, kx + 66, top + 41, 12, 4);
                int[] cnt = { 10, 10, 9 };
                for (int row = 0; row < 3; row++)
                    for (int col = 0; col < cnt[row]; col++)
                        g.FillRectangle(bl, kx - 78 + col * 10 + row * 3, top + 50 + row * 9, 8, 6);               // QWERTY keys
                g.FillRectangle(bd, kx - 52, top + 77, 44, 5);                                                      // space bar
                for (int row = 0; row < 4; row++)
                    for (int col = 0; col < 4; col++)
                        g.FillRectangle(col == 3 && row == 0 ? bo : bl, kx + 28 + col * 12, top + 50 + row * 8, 9, 6);   // numeric pad
            }
            g.Restore(state);
        }

        // Sharp pocket computers with the 15-pin connector (drawing of a PC-E500): dark grey body, a large display,
        // function keys under it, the QWERTY keys and a numeric pad with light keys
        // TRS-80 Model 100 / NEC PC-8201: a wide LCD above a keyboard
        static void DrawTrs80(Graphics g, int kx, int top)
        {
            FillRound(g, Color.FromArgb(25, 25, 28), kx - 86, top + 10, 172, 82, 6);
            FillRound(g, Color.FromArgb(150, 152, 150), kx - 85, top + 8, 170, 82, 6);
            FillRound(g, Color.FromArgb(50, 50, 54), kx - 78, top + 13, 156, 38, 3);                          // display frame
            using (var b = new SolidBrush(Color.FromArgb(160, 176, 150))) g.FillRectangle(b, kx - 74, top + 17, 148, 30);   // LCD 40 x 8
            using (var b = new SolidBrush(Color.FromArgb(60, 78, 56)))
                for (int row = 0; row < 4; row++) for (int c = 0; c < 36; c++) g.FillRectangle(b, kx - 70 + c * 4, top + 20 + row * 6, 3, 3);
            using (var f = new Font("Arial", 6F, FontStyle.Bold)) Text(g, "TRS-80", f, Color.FromArgb(40, 40, 44), kx, top + 53);
            using (var bk = new SolidBrush(Color.FromArgb(40, 40, 44)))
            using (var bf = new SolidBrush(Color.FromArgb(215, 215, 210)))
            {
                for (int i = 0; i < 8; i++) g.FillRectangle(bf, kx - 76 + i * 19, top + 62, 15, 5);               // function keys
                for (int row = 0; row < 3; row++)
                    for (int col = 0; col < 13; col++) g.FillRectangle(bk, kx - 76 + col * 11 + row * 3, top + 70 + row * 6, 9, 4);
                g.FillRectangle(bk, kx - 40, top + 87, 70, 3);
            }
        }

        // Casio FX-850P / FX-870P: a wide display on the left, coloured function keys and a numeric block with red BS / BRK on the right
        static void DrawCasio(Graphics g, int kx, int top)
        {
            var state = g.Save();
            g.TranslateTransform(kx, top + 60);
            g.ScaleTransform(0.7F, 0.7F);
            g.TranslateTransform(-kx, -(top + 60));
            FillRound(g, Color.FromArgb(70, 70, 74), kx - 87, top + 10, 174, 82, 5);
            FillRound(g, Color.FromArgb(196, 198, 200), kx - 86, top + 8, 172, 82, 5);                                  // light grey body
            using (var b = new SolidBrush(Color.FromArgb(60, 60, 64))) g.FillRectangle(b, kx - 82, top + 12, 112, 36);     // display frame
            using (var b = new SolidBrush(Color.FromArgb(150, 165, 150))) g.FillRectangle(b, kx - 76, top + 16, 100, 28);  // LCD
            using (var b = new SolidBrush(Color.FromArgb(70, 85, 70)))
                for (int c = 0; c < 70; c += 4) g.FillRectangle(b, kx - 68 + c, top + 24, 3, 5);
            using (var f = new Font("Arial", 5.5F, FontStyle.Bold)) Text(g, "CASIO", f, Color.FromArgb(30, 30, 34), kx + 36, top + 12);
            using (var bk = new SolidBrush(Color.FromArgb(40, 40, 44)))
            using (var bg = new SolidBrush(Color.FromArgb(232, 232, 228)))
            using (var bs = new SolidBrush(Color.FromArgb(120, 124, 128)))
            using (var bb = new SolidBrush(Color.FromArgb(60, 110, 200)))
            using (var bo = new SolidBrush(Color.FromArgb(235, 150, 40)))
            using (var br = new SolidBrush(Color.FromArgb(190, 40, 45)))
            {
                g.FillRectangle(bo, kx + 36, top + 20, 9, 6); g.FillRectangle(bb, kx + 47, top + 20, 9, 6);                // CASL / Fx keys
                for (int row = 0; row < 3; row++)
                    for (int col = 0; col < 5; col++) g.FillRectangle(bs, kx + 36 + col * 10, top + 29 + row * 7, 8, 5);   // scientific keys
                for (int i = 0; i < 8; i++) g.FillRectangle(bk, kx - 80 + i * 14, top + 52, 11, 5);                         // function row
                for (int row = 0; row < 3; row++)
                    for (int col = 0; col < 10; col++) g.FillRectangle(bk, kx - 80 + col * 11 + row * 3, top + 60 + row * 8, 9, 5);   // QWERTY
                g.FillRectangle(bk, kx - 52, top + 84, 40, 4);
                for (int row = 0; row < 4; row++)
                    for (int col = 0; col < 4; col++)
                        g.FillRectangle(col == 3 && row == 0 ? br : col == 2 && row == 0 ? br : bg, kx + 38 + col * 11, top + 50 + row * 9, 9, 7);   // numeric block, BS / BRK red
            }
            g.Restore(state);
        }

        // a generic pocket computer (for any computer with a UART): a one-line display, a key matrix and a cable on the side
        static void DrawGenericPocket(Graphics g, int kx, int top)
        {
            var state = g.Save();
            g.TranslateTransform(kx, top + 60);
            g.ScaleTransform(0.7F, 0.7F);
            g.TranslateTransform(-kx, -(top + 60));
            FillRound(g, Color.FromArgb(20, 20, 24), kx - 86, top + 10, 172, 82, 7);
            FillRound(g, Color.FromArgb(58, 62, 72), kx - 85, top + 8, 170, 82, 7);                                    // dark body
            using (var b = new SolidBrush(Color.FromArgb(30, 32, 36))) g.FillRectangle(b, kx - 78, top + 14, 156, 28);    // display frame
            using (var b = new SolidBrush(Color.FromArgb(170, 188, 160))) g.FillRectangle(b, kx - 74, top + 18, 148, 20); // LCD
            using (var b = new SolidBrush(Color.FromArgb(62, 80, 58)))
                for (int c = 0; c < 128; c += 5) g.FillRectangle(b, kx - 68 + c, top + 24, 3, 7);
            using (var bk = new SolidBrush(Color.FromArgb(205, 208, 212)))
            using (var bo = new SolidBrush(Color.FromArgb(235, 150, 40)))
            {
                for (int row = 0; row < 4; row++)
                    for (int col = 0; col < 12; col++)
                        g.FillRectangle(row == 3 && col == 11 ? bo : bk, kx - 76 + col * 13, top + 48 + row * 10, 10, 6);
            }
            using (var p = new Pen(Color.FromArgb(230, 200, 70), 2.5F)) g.DrawLine(p, kx - 85, top + 52, kx - 100, top + 52);   // the cable (TX / RX)
            g.Restore(state);
        }

        // Sharp PC-1350 / PC-1360 (as on the photo of the PC-1360): silver body, a dark panel with a wide display on the left, a small dark
        // key block on the right, dark QWERTY keys under the display and a numeric block with grey keys
        static void DrawPc1360(Graphics g, int kx, int top)
        {
            FillRound(g, Color.FromArgb(25, 25, 28), kx - 86, top + 10, 172, 82, 6);
            FillRound(g, Color.FromArgb(190, 192, 194), kx - 85, top + 8, 170, 82, 6);                               // silver body
            FillRound(g, Color.FromArgb(30, 30, 33), kx - 82, top + 10, 110, 31, 3);                                 // dark panel with the display
            using (var b = new SolidBrush(Color.FromArgb(150, 168, 148))) g.FillRectangle(b, kx - 79, top + 19, 104, 17);   // wide display
            using (var b = new SolidBrush(Color.FromArgb(72, 88, 68)))
                for (int c = 0; c < 24; c++) g.FillRectangle(b, kx - 76 + c * 4, top + 24, 3, 5);
            using (var f = new Font("Arial", 5F, FontStyle.Bold)) Text(g, "SHARP", f, Color.White, kx - 62, top + 11);
            FillRound(g, Color.FromArgb(30, 30, 33), kx + 31, top + 10, 52, 31, 3);                                  // key block, top right
            using (var bk = new SolidBrush(Color.FromArgb(40, 40, 44)))
            using (var bg = new SolidBrush(Color.FromArgb(150, 152, 150)))
            using (var bw = new SolidBrush(Color.FromArgb(215, 215, 210)))
            using (var br = new SolidBrush(Color.FromArgb(205, 50, 50)))
            using (var bo = new SolidBrush(Color.FromArgb(225, 150, 40)))
            {
                for (int row = 0; row < 2; row++)
                    for (int col = 0; col < 4; col++) g.FillRectangle(col == 3 && row == 1 ? br : bg, kx + 35 + col * 11, top + 14 + row * 9, 9, 6);
                g.FillRectangle(bw, kx + 35, top + 32, 44, 5);
                int[] cnt = { 10, 9, 7 };
                for (int row = 0; row < 3; row++)
                    for (int col = 0; col < cnt[row]; col++)
                        g.FillRectangle(row == 0 && col == 0 ? bo : bk, kx - 80 + col * 9 + row * 3, top + 49 + row * 8, 7, 5);   // QWERTY keys
                g.FillRectangle(bk, kx - 52, top + 74, 34, 4);                                                       // the space bar
                for (int row = 0; row < 4; row++)
                    for (int col = 0; col < 5; col++)
                        g.FillRectangle(bg, kx + 31 + col * 10, top + 47 + row * 9, 8, 6);                           // numeric block
            }
        }

        // Sharp PC-1450 ... PC-1475 (as on the photo of the PC-1450): silver body, a dark panel with a wide display on the left, dark scientific keys
        // at the top right, a row of function keys, dark QWERTY keys and a block of dark numeric keys
        static void DrawPc1450(Graphics g, int kx, int top)
        {
            FillRound(g, Color.FromArgb(25, 25, 28), kx - 86, top + 10, 172, 82, 6);
            FillRound(g, Color.FromArgb(196, 192, 178), kx - 85, top + 8, 170, 82, 6);                               // silver-gold body
            FillRound(g, Color.FromArgb(34, 32, 32), kx - 70, top + 10, 98, 27, 3);                                  // dark panel with the display
            using (var b = new SolidBrush(Color.FromArgb(168, 178, 160))) g.FillRectangle(b, kx - 66, top + 17, 90, 15);   // display
            using (var b = new SolidBrush(Color.FromArgb(52, 62, 48)))
                for (int c = 0; c < 18; c++) g.FillRectangle(b, kx - 63 + c * 5, top + 21, 4, 7);
            using (var f = new Font("Arial", 5F, FontStyle.Bold)) Text(g, "SHARP", f, Color.White, kx - 48, top + 10);
            using (var bd = new SolidBrush(Color.FromArgb(62, 62, 64)))
            using (var bk = new SolidBrush(Color.FromArgb(46, 46, 50)))
            using (var br = new SolidBrush(Color.FromArgb(205, 50, 50)))
            using (var bo = new SolidBrush(Color.FromArgb(225, 150, 40)))
            using (var bg = new SolidBrush(Color.FromArgb(40, 70, 60)))
            {
                g.FillRectangle(bg, kx - 83, top + 17, 8, 6);                                                         // the power switch, left
                for (int row = 0; row < 3; row++)                                                                    // scientific keys, top right
                    for (int col = 0; col < 5; col++)
                        g.FillRectangle(row == 0 && col == 4 ? br : bd, kx + 33 + col * 10, top + 11 + row * 8, 8, 6);
                for (int col = 0; col < 3; col++) g.FillRectangle(bd, kx - 28 + col * 12, top + 41, 9, 4);          // the cursor keys
                g.FillRectangle(bo, kx + 14, top + 41, 11, 4);
                int[] cnt = { 10, 9, 7 };
                for (int row = 0; row < 3; row++)
                    for (int col = 0; col < cnt[row]; col++)
                        g.FillRectangle(bk, kx - 80 + col * 9 + row * 3, top + 49 + row * 8, 7, 5);                 // QWERTY keys
                g.FillRectangle(bk, kx - 52, top + 74, 34, 4);
                for (int row = 0; row < 4; row++)
                    for (int col = 0; col < 5; col++)
                        g.FillRectangle(col == 4 && row == 1 ? bo : bk, kx + 31 + col * 10, top + 47 + row * 9, 8, 6);   // numeric block
            }
        }

        static void DrawPc15pin(Graphics g, int kx, int top, string brand)
        {
            var state = g.Save();
            g.TranslateTransform(kx, top + 60);
            g.ScaleTransform(0.7F, 0.7F);
            g.TranslateTransform(-kx, -(top + 60));
            FillRound(g, Color.FromArgb(25, 25, 28), kx - 86, top + 10, 172, 82, 6);
            FillRound(g, Color.FromArgb(70, 70, 74), kx - 85, top + 8, 170, 82, 6);
            FillRound(g, Color.FromArgb(40, 40, 44), kx - 82, top + 11, 118, 36, 3);                                  // display frame
            using (var b = new SolidBrush(Color.FromArgb(172, 188, 150))) g.FillRectangle(b, kx - 78, top + 15, 110, 28);   // large display
            using (var b = new SolidBrush(Color.FromArgb(60, 78, 56)))
                for (int c = 0; c < 80; c += 3) g.FillRectangle(b, kx - 60 + c, top + 26, 2, 4);                       // a line of text
            using (var f = new Font("Arial", 5.5F, FontStyle.Bold)) Text(g, brand, f, Color.FromArgb(220, 220, 220), kx + 62, top + 12);
            using (var bk = new SolidBrush(Color.FromArgb(34, 34, 38)))
            using (var bg = new SolidBrush(Color.FromArgb(210, 210, 206)))
            using (var bs = new SolidBrush(Color.FromArgb(165, 170, 175)))
            using (var br = new SolidBrush(Color.FromArgb(200, 50, 45)))
            using (var bo = new SolidBrush(Color.FromArgb(225, 130, 40)))
            {
                for (int i = 0; i < 5; i++) g.FillRectangle(bk, kx - 78 + i * 23, top + 51, 19, 5);                       // PF1..PF5
                for (int row = 0; row < 3; row++)                                                                           // scientific keys, top right
                    for (int col = 0; col < 5; col++)
                        g.FillRectangle(row == 0 && col == 4 ? br : bs, kx + 40 + col * 8, top + 13 + row * 8, 6, 5);
                int[] cnt = { 10, 9, 9 };
                for (int row = 0; row < 3; row++)
                    for (int col = 0; col < cnt[row]; col++)
                        g.FillRectangle(bk, kx - 80 + col * 10 + row * 4, top + 59 + row * 8, 8, 5);                    // QWERTY keys
                g.FillRectangle(bk, kx - 50, top + 83, 40, 4);
                for (int row = 0; row < 4; row++)
                    for (int col = 0; col < 4; col++)
                        g.FillRectangle(col == 3 && row == 2 ? bo : bg, kx + 40 + col * 11, top + 51 + row * 9, 9, 7);   // numeric pad
            }
            g.Restore(state);
        }

        // Sharp PC-1600 (as on the photo): light body, LCD on the left (2/3), dark brown panel with the logo on the right (1/3),
        // dark QWERTY keys under the LCD, dark numeric keypad under the brown panel
        static void DrawPc1600(Graphics g, int kx, int top)
        {
            var state = g.Save();
            g.TranslateTransform(kx, top + 60);          // whole icon scaled to 70 %
            g.ScaleTransform(0.7F, 0.7F);
            g.TranslateTransform(-kx, -(top + 60));
            FillRound(g, Color.FromArgb(150, 150, 146), kx - 86, top + 10, 172, 82, 6);      // shadow edge
            FillRound(g, Color.FromArgb(226, 226, 220), kx - 85, top + 8, 170, 82, 6);       // body
            using (var b = new SolidBrush(Color.FromArgb(178, 194, 122))) g.FillRectangle(b, kx - 81, top + 12, 108, 30);   // LCD, no bezel
            using (var p = new Pen(Color.FromArgb(130, 145, 90))) g.DrawRectangle(p, kx - 81, top + 12, 108, 30);
            using (var b = new SolidBrush(Color.FromArgb(70, 50, 42))) g.FillRectangle(b, kx + 31, top + 12, 51, 21);       // brown panel (30 % lower)
            using (var f = new Font("Arial", 5.5F, FontStyle.Bold)) Text(g, "SHARP", f, Color.FromArgb(230, 225, 215), kx + 56, top + 16);

            Color dark = Color.FromArgb(48, 48, 54), dot = Color.FromArgb(215, 215, 210);
            using (var bd = new SolidBrush(dark))
            using (var bl = new SolidBrush(dot))
            using (var br = new SolidBrush(Color.FromArgb(190, 50, 45)))
            using (var bg = new SolidBrush(Color.FromArgb(190, 190, 184)))
            {
                // QWERTY rows under the LCD (left 2/3)
                int[] cnt = { 10, 9, 10 };
                int[] off = { 0, 5, 0 };
                for (int row = 0; row < 3; row++)
                    for (int col = 0; col < cnt[row]; col++)
                    {
                        int x = kx - 80 + off[row] + col * 10;
                        g.FillRectangle(bd, x, top + 47 + row * 9, 8, 6);
                        g.FillRectangle(bl, x + 3, top + 49 + row * 9, 2, 2);
                    }
                // bottom row: red key, light keys, wide dark space bar
                g.FillRectangle(br, kx - 80, top + 74, 8, 6);
                for (int i = 0; i < 3; i++) g.FillRectangle(bg, kx - 70 + i * 10, top + 74, 8, 6);
                g.FillRectangle(bd, kx - 38, top + 74, 30, 6);
                for (int i = 0; i < 3; i++) g.FillRectangle(bg, kx - 6 + i * 10, top + 74, 8, 6);

                // keypad under the brown panel (right 1/3): small top row + 4 rows x 5 keys
                for (int col = 0; col < 5; col++)
                    g.FillRectangle(col == 1 ? br : bd, kx + 32 + col * 10, top + 45, 8, 6);
                for (int row = 0; row < 4; row++)
                    for (int col = 0; col < 5; col++)
                    {
                        int x = kx + 32 + col * 10, y = top + 54 + row * 8;
                        g.FillRectangle(row == 0 && col == 4 ? br : bd, x, y, 8, 6);
                        g.FillRectangle(bl, x + 3, y + 2, 2, 2);
                    }
            }
            g.Restore(state);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            if (Width < 100 || Height < 60) return;
            var r = ClientRectangle;
            using (var br = new LinearGradientBrush(r, Color.FromArgb(14, 38, 72), Color.FromArgb(30, 70, 120), LinearGradientMode.Horizontal))
                g.FillRectangle(br, r);

            int pcx = Width * 22 / 100, kx = Width * 78 / 100, top = 12;

            // PC
            FillRound(g, Color.FromArgb(214, 208, 190), pcx - 48, top, 96, 70, 8);
            using (var b = new SolidBrush(Color.FromArgb(10, 25, 30))) g.FillRectangle(b, pcx - 40, top + 8, 80, 50);
            using (var f = new Font("Consolas", 16F, FontStyle.Bold)) Text(g, "PC", f, Color.FromArgb(90, 230, 120), pcx, top + 20);
            using (var b = new SolidBrush(Color.FromArgb(190, 184, 168))) g.FillRectangle(b, pcx - 14, top + 70, 28, 8);
            FillRound(g, Color.FromArgb(200, 194, 178), pcx - 56, top + 80, 112, 16, 4);
            using (var b = new SolidBrush(Color.FromArgb(120, 115, 105)))
                for (int i = 0; i < 10; i++) { g.FillRectangle(b, pcx - 50 + i * 10, top + 84, 7, 3); g.FillRectangle(b, pcx - 48 + i * 10, top + 89, 7, 3); }

            if (Model == 0) DrawPc1500(g, kx, top);
            else if (Model == 2 && (Prof == 2 || Prof == 3)) DrawPc1360(g, kx, top);
            else if (Model == 2 && Prof == 6) DrawPc1450(g, kx, top);
            else if (Model == 2) DrawPc15pin(g, kx, top, "SHARP");
            else if (Model == 3) DrawCasio(g, kx, top);
            else if (Model == 4) DrawTrs80(g, kx, top);
            else if (Model == 5) DrawGenericPocket(g, kx, top);
            else DrawPc1600(g, kx, top);

            // captions
            using (var f = new Font("Segoe UI", 9F)) { Text(g, "Windows PC", f, Color.White, pcx, top + 100); Text(g, ModelName, f, Color.White, kx, top + 100); }

            // centre: title, arrows, LEDs
            int mid = Width / 2, ax1 = pcx + 70, ax2 = kx - 92;
            using (var f = new Font("Segoe UI", 14F, FontStyle.Bold))
            {
                Text(g, "SerComet", f, Color.White, mid, top - 4);
                if (cometImg == null) cometImg = Icons.Comet(28);
                g.DrawImage(cometImg, mid - g.MeasureString("SerComet", f).Width / 2 - 32, top - 6);
            }
            Arrow(g, ax1, ax2, top + 44, true, Sending, Blink, FlashWhite, PortOpen);
            Arrow(g, ax1, ax2, top + 74, false, Receiving, Blink, FlashWhite, PortOpen);
            Color ok = PortOpen ? Color.FromArgb(70, 220, 90) : Color.FromArgb(90, 90, 100);
            Led(g, ok, mid - 18, top + 96);
            Led(g, ok, mid, top + 96);
            Led(g, Sending || Receiving ? Color.FromArgb(240, 60, 50) : Color.FromArgb(90, 90, 100), mid + 18, top + 96);
        }
    }

    // Simple terminal (like TeraTerm): typed characters go out at once, received data is shown live
    public class TerminalForm : Form
    {
        readonly TextBox txt;
        readonly CheckBox chkEcho;
        readonly ComboBox comboNl;
        readonly Action<byte[]> send;
        bool lastCr;

        public TerminalForm(string title, Action<byte[]> send)
        {
            this.send = send;
            Text = title;
            ClientSize = new Size(640, 420);
            StartPosition = FormStartPosition.CenterParent;
            try { Icon = Icons.AppIcon(); } catch { }

            var top = new Panel { Dock = DockStyle.Top, Height = 34 };
            chkEcho = new CheckBox { Text = "Local echo", Left = 8, Top = 8, Width = 90 };
            var lbl = new Label { Text = "Enter sends:", Left = 110, Top = 11, Width = 72 };
            comboNl = new ComboBox { Left = 185, Top = 6, Width = 70, DropDownStyle = ComboBoxStyle.DropDownList };
            comboNl.Items.AddRange(new object[] { "CR", "CR+LF", "LF" });
            comboNl.SelectedIndex = 0;
            var btnClear = new Button { Text = "Clear", Left = 270, Top = 5, Width = 70, Height = 24 };
            btnClear.Click += delegate { txt.Clear(); txt.Focus(); };
            top.Controls.AddRange(new Control[] { chkEcho, lbl, comboNl, btnClear });
            var tt = new WrapToolTip { AutoPopDelay = 9000, InitialDelay = 400, ShowAlways = true };
            tt.SetToolTip(chkEcho, "Show typed characters locally (use it when the Sharp does not echo them).");
            tt.SetToolTip(comboNl, "Characters sent when you press Enter.");
            tt.SetToolTip(btnClear, "Clear the terminal window.");

            txt = new TextBox { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 10F), ReadOnly = true, WordWrap = true };
            txt.BackColor = Color.Black;
            txt.ForeColor = Color.Gainsboro;
            txt.KeyPress += OnKeyPress;
            txt.KeyDown += OnKeyDown;
            Controls.Add(txt);
            Controls.Add(top);
            Shown += delegate { txt.Focus(); };
        }

        string Newline() { return comboNl.SelectedIndex == 1 ? "\r\n" : comboNl.SelectedIndex == 2 ? "\n" : "\r"; }

        void SendChars(string t)
        {
            var b = new List<byte>();
            foreach (char c in t) b.Add(c < 256 ? (byte)c : (byte)'?');
            if (b.Count == 0) return;
            send(b.ToArray());
            if (chkEcho.Checked) Rx(b.ToArray());
        }

        void OnKeyPress(object sender, KeyPressEventArgs e)
        {
            e.Handled = true;
            char c = e.KeyChar;
            if (c == '\r') SendChars(Newline());
            else if (c == '\b' || c == '\t' || c >= 32) SendChars(c.ToString());
        }

        void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.V && Clipboard.ContainsText())
            {
                e.SuppressKeyPress = true;
                SendChars(Clipboard.GetText().Replace("\r\n", "\n").Replace("\n", Newline()));
            }
        }

        // received bytes -> text (CR or LF = new line, BS erases, other control codes are ignored)
        public void Rx(byte[] data)
        {
            var sb = new StringBuilder();
            foreach (byte c in data)
            {
                if (c == 13) { sb.Append("\r\n"); lastCr = true; continue; }
                if (c == 10) { if (!lastCr) sb.Append("\r\n"); lastCr = false; continue; }
                lastCr = false;
                if (c == 8) { if (sb.Length > 0) sb.Length--; }
                else if (c == 9 || c >= 32) sb.Append((char)c);
            }
            txt.AppendText(sb.ToString());
            if (txt.TextLength > 200000) { txt.Text = txt.Text.Substring(txt.TextLength - 100000); txt.SelectionStart = txt.TextLength; txt.ScrollToCaret(); }
        }
    }

    // About window
    public class AboutForm : Form
    {
        const int W = 420;
        public AboutForm()
        {
            Text = "About SerComet";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(W, 488);

            Controls.Add(new PictureBox { Image = Icons.Comet(72), SizeMode = PictureBoxSizeMode.AutoSize, Left = (W - 72) / 2, Top = 16 });
            AddLine("SerComet " + AppInfo.Version, 100, new Font("Segoe UI", 16F, FontStyle.Bold), Color.FromArgb(30, 70, 120));
            AddLine("File transfer tool for PC and Sharp pocket computers", 138, new Font("Segoe UI", 10F, FontStyle.Bold), Color.Black);
            AddLine("Author: Alex Matczewski F4VTS", 168, new Font("Segoe UI", 9.5F, FontStyle.Bold), Color.Black);
            AddLine("Copyright \u00A9 2026", 190, new Font("Segoe UI", 9.5F, FontStyle.Bold), Color.Black);
            AddLine("Free for personal, non-commercial and educational use.", 218, new Font("Segoe UI", 9F, FontStyle.Bold), Color.Black);
            AddLine("License: PolyForm Noncommercial License 1.0.0", 240, new Font("Segoe UI", 8.5F), Color.Black);
            var tt = new WrapToolTip { AutoPopDelay = 9000, InitialDelay = 400, ShowAlways = true };
            AddLink("Program page: ante1000.github.io", "https://ante1000.github.io/sercomet/", 14, 20, 262, tt, "Open the SerComet page in the browser.");
            AddLink("Web: github.com/Ante1000", "https://github.com/Ante1000", 5, 19, 284, tt, "Open the project page in the browser.");
            AddLink("YouTube: youtube.com/@anteamber", "https://www.youtube.com/@anteamber", 9, 24, 306, tt, "Open the YouTube channel in the browser.");
            AddLine("Bug reports, suggestions, questions, contact:", 336, new Font("Segoe UI", 8.5F), Color.Black);
            var mail = new LinkLabel { Text = "alex.f4vts@gmail.com", Left = 0, Top = 356, Width = W, Height = 20, TextAlign = ContentAlignment.MiddleCenter, Font = new Font("Segoe UI", 10F) };
            mail.Links.Add(0, 20, "mailto:alex.f4vts@gmail.com");
            mail.LinkClicked += OpenLink;
            Controls.Add(mail);
            tt.SetToolTip(mail, "Write an e-mail.");
            AddLine("BASIC token tables: \"Das Systemhandbuch f\u00FCr den PC-1600\"", 388, new Font("Segoe UI", 8.5F), Color.Black);
            AddLine("by David von Oheimb. Thank you!", 408, new Font("Segoe UI", 8.5F), Color.Black);
            var ok = new Button { Text = "OK", Left = (W - 80) / 2, Top = 448, Width = 80, DialogResult = DialogResult.OK };
            Controls.Add(ok);
            AcceptButton = ok;
            tt.SetToolTip(ok, "Close this window.");
        }

        static void OpenLink(object s, LinkLabelLinkClickedEventArgs e)
        {
            try { System.Diagnostics.Process.Start((string)e.Link.LinkData); } catch { }
        }

        void AddLink(string text, string url, int start, int length, int top, WrapToolTip tt, string tip)
        {
            var l = new LinkLabel { Text = text, Left = 0, Top = top, Width = W, Height = 20, TextAlign = ContentAlignment.MiddleCenter, Font = new Font("Segoe UI", 10F) };
            l.Links.Add(start, length, url);
            l.LinkClicked += OpenLink;
            Controls.Add(l);
            tt.SetToolTip(l, tip);
        }

        void AddLine(string text, int top, Font font, Color color)
        {
            Controls.Add(new Label { Text = text, Left = 0, Top = top, Width = W, Height = 24, TextAlign = ContentAlignment.MiddleCenter, Font = font, ForeColor = color });
        }
    }

    // PC-1600 BASIC program <-> text. Written from scratch using the token tables of
    // "Das Systemhandbuch fuer den PC-1600" by David von Oheimb (appendix 5, Tokentabelle) and checked against
    // programs saved by a real PC-1600. Program format: [line no. hi, lo][length][tokens, text, 0D]..., end FFh.
    // Tokens are two bytes (E3/E6/E7/E8/F0/F1/F2 + code); a line number constant is stored as 1Fh hi lo 00h.
    public static class Pc1600Basic
    {
        static readonly object[] TokenList = new object[] {
            "PAPER", 0xE381, "CSIZE", 0xE680, "GRAPH", 0xE681, "GLCURSOR", 0xE682, "SORGN", 0xE684, "ROTATE", 0xE685,
            "TEXT", 0xE686, "RMT", 0xE7A9, "DEV$", 0xE857, "COM$", 0xE858, "INSTAT", 0xE859, "RINKEY$", 0xE85A,
            "OUTSTAT", 0xE880, "SETCOM", 0xE882, "TERMINAL", 0xE883, "DTE", 0xE884, "TRANSMIT", 0xE885,
            "SETDEV", 0xE886, "ERN", 0xF052, "ERL", 0xF053, "SPACE$", 0xF061, "CURSOR", 0xF084, "USING", 0xF085,
            "WDTH", 0xF087, "CLS", 0xF088, "CLOAD", 0xF089, "MERGE", 0xF08F, "LIST", 0xF090, "INPUT", 0xF091,
            "GCURSOR", 0xF093, "CSAVE", 0xF095, "PRINT", 0xF097, "FILES", 0xF098, "LINE", 0xF099, "PRESET", 0xF09A,
            "PSET", 0xF09B, "GPRINT", 0xF09F, "LFILES", 0xF0A0, "PITCH", 0xF0A4, "LCURSOR", 0xF0A5, "FEED", 0xF0B0,
            "CONSOLE", 0xF0B1, "CHAIN", 0xF0B2, "BREAK", 0xF0B3, "ZONE", 0xF0B4, "COLOR", 0xF0B5, "LF", 0xF0B6,
            "LLINE", 0xF0B7, "LLIST", 0xF0B8, "LPRINT", 0xF0B9, "RLINE", 0xF0BA, "TAB", 0xF0BB, "TEST", 0xF0BC,
            "AND", 0xF150, "OR", 0xF151, "MEM", 0xF158, "TIME", 0xF15B, "INKEY$", 0xF15C, "PI", 0xF15D,
            "ASC", 0xF160, "STR$", 0xF161, "VAL", 0xF162, "CHR$", 0xF163, "LEN", 0xF164, "DEG", 0xF165,
            "DMS", 0xF166, "STATUS", 0xF167, "POINT", 0xF168, "SQR", 0xF16B, "NOT", 0xF16D, "XPEEK#", 0xF16E,
            "XPEEK", 0xF16F, "ABS", 0xF170, "INT", 0xF171, "RIGHT$", 0xF172, "ASN", 0xF173, "ACS", 0xF174,
            "ATN", 0xF175, "LN", 0xF176, "LOG", 0xF177, "EXP", 0xF178, "SGN", 0xF179, "LEFT$", 0xF17A,
            "MID$", 0xF17B, "RND", 0xF17C, "SIN", 0xF17D, "COS", 0xF17E, "TAN", 0xF17F, "AREAD", 0xF180,
            "ARUN", 0xF181, "BEEP", 0xF182, "CONT", 0xF183, "GRAD", 0xF186, "CLEAR", 0xF187, "XCALL", 0xF18A,
            "DIM", 0xF18B, "DEGREE", 0xF18C, "DATA", 0xF18D, "END", 0xF18E, "GOTO", 0xF192, "GOSUB", 0xF194,
            "IF", 0xF196, "LET", 0xF198, "RETURN", 0xF199, "NEXT", 0xF19A, "NEW", 0xF19B, "ON", 0xF19C,
            "OPN", 0xF19D, "OFF", 0xF19E, "XPOKE#", 0xF1A0, "XPOKE", 0xF1A1, "PAUSE", 0xF1A2, "RUN", 0xF1A4,
            "FOR", 0xF1A5, "READ", 0xF1A6, "RESTORE", 0xF1A7, "RANDOM", 0xF1A8, "RADIAN", 0xF1AA, "REM", 0xF1AB,
            "STOP", 0xF1AC, "STEP", 0xF1AD, "THEN", 0xF1AE, "TRON", 0xF1AF, "TROFF", 0xF1B0, "TO", 0xF1B1,
            "WAIT", 0xF1B3, "ERROR", 0xF1B4, "LOCK", 0xF1B5, "UNLOCK", 0xF1B6, "EOF", 0xF271, "LOC", 0xF272,
            "LOF", 0xF273, "DSKF", 0xF274, "MOD", 0xF250, "XOR", 0xF251, "RXD$", 0xF256, "DATE$", 0xF257,
            "TIME$", 0xF258, "AIN", 0xF25A, "ALARM$", 0xF25C, "WAKE$", 0xF261, "ACNV$", 0xF262, "HEX$", 0xF265,
            "INP", 0xF266, "INSTR", 0xF267, "PEEK", 0xF26D, "PEEK#", 0xF26E, "ADIN", 0xF280, "CALL", 0xF282,
            "ELSE", 0xF283, "KBUFF$", 0xF284, "KEY", 0xF285, "KEYSTAT", 0xF286, "KILL", 0xF287, "MAXFILES", 0xF288,
            "OUT", 0xF28A, "POWER", 0xF28B, "POKE", 0xF28C, "RESUME", 0xF28D, "RETI", 0xF28E, "BLOAD", 0xF290,
            "BSAVE", 0xF291, "CLOSE", 0xF292, "COPY", 0xF293, "INIT", 0xF294, "LOAD", 0xF295, "OPEN", 0xF296,
            "NAME", 0xF297, "SET", 0xF298, "SAVE", 0xF299, "PHONE", 0xF2A0, "SNDBRK", 0xF2A1, "SNDSTAT", 0xF2A2,
            "COM", 0xF2A3, "RCVSTAT", 0xF2A4, "PCONSOLE", 0xF2B1, "MODE", 0xF2B3, "PZONE", 0xF2B4, "RENUM", 0xF2B5,
            "AUTO", 0xF2B6, "ERASE", 0xF2B7, "PASS", 0xF2B8, "DELETE", 0xF2B9, "TITLE", 0xF2BA, "AOFF", 0xF2BC,
            "AS", 0xF2BD, "OUTPUT", 0xF2BE, "APPEND", 0xF2BF,
        };

        // PC-1500 (and CE-150 / CE-158): the PC-1500 token table, from the table in "Tips PC-1500" ("SHARP PC 1500(A) und CE 150
        // TOKEN-Tabelle"), completed with the interface commands from the PC-1600 handbook. The PC-1500 uses the same codes for
        // most words; PEEK/POKE/CALL are F16F/F1A1/F18A there (X-versions in the PC-1600).
        static readonly object[] TokenList1500 = new object[] {
            "CSIZE", 0xE680, "GRAPH", 0xE681, "GLCURSOR", 0xE682, "LCURSOR", 0xE683, "SORGN", 0xE684,
            "ROTATE", 0xE685, "TEXT", 0xE686, "RMT", 0xE7A9, "DEV$", 0xE857, "INSTAT", 0xE859, "RINKEY$", 0xE85A,
            "OUTSTAT", 0xE880, "SETCOM", 0xE882, "TERMINAL", 0xE883, "DTE", 0xE884, "TRANSMIT", 0xE885,
            "SETDEV", 0xE886, "ERN", 0xF052, "ERL", 0xF053, "SPACE$", 0xF061, "CURSOR", 0xF084, "USING", 0xF085,
            "CLS", 0xF088, "CLOAD", 0xF089, "MERGE", 0xF08F, "LIST", 0xF090, "INPUT", 0xF091, "GCURSOR", 0xF093,
            "CSAVE", 0xF095, "PRINT", 0xF097, "GPRINT", 0xF09F, "FEED", 0xF0B0, "CONSOLE", 0xF0B1, "CHAIN", 0xF0B2,
            "BREAK", 0xF0B3, "ZONE", 0xF0B4, "COLOR", 0xF0B5, "LF", 0xF0B6, "LINE", 0xF0B7, "LLIST", 0xF0B8,
            "LPRINT", 0xF0B9, "RLINE", 0xF0BA, "TAB", 0xF0BB, "TEST", 0xF0BC, "AND", 0xF150, "OR", 0xF151,
            "MEM", 0xF158, "TIME", 0xF15B, "INKEY$", 0xF15C, "PI", 0xF15D, "ASC", 0xF160, "STR$", 0xF161,
            "VAL", 0xF162, "CHR$", 0xF163, "LEN", 0xF164, "DEG", 0xF165, "DMS", 0xF166, "STATUS", 0xF167,
            "POINT", 0xF168, "SQR", 0xF16B, "NOT", 0xF16D, "PEEK#", 0xF16E, "PEEK", 0xF16F, "ABS", 0xF170,
            "INT", 0xF171, "RIGHT$", 0xF172, "ASN", 0xF173, "ACS", 0xF174, "ATN", 0xF175, "LN", 0xF176,
            "LOG", 0xF177, "EXP", 0xF178, "SGN", 0xF179, "LEFT$", 0xF17A, "MID$", 0xF17B, "RND", 0xF17C,
            "SIN", 0xF17D, "COS", 0xF17E, "TAN", 0xF17F, "AREAD", 0xF180, "ARUN", 0xF181, "BEEP", 0xF182,
            "CONT", 0xF183, "GRAD", 0xF186, "CLEAR", 0xF187, "CALL", 0xF18A, "DIM", 0xF18B, "DEGREE", 0xF18C,
            "DATA", 0xF18D, "END", 0xF18E, "GOTO", 0xF192, "GOSUB", 0xF194, "IF", 0xF196, "LET", 0xF198,
            "RETURN", 0xF199, "NEXT", 0xF19A, "NEW", 0xF19B, "ON", 0xF19C, "OPN", 0xF19D, "OFF", 0xF19E,
            "POKE#", 0xF1A0, "POKE", 0xF1A1, "PAUSE", 0xF1A2, "RUN", 0xF1A4, "FOR", 0xF1A5, "READ", 0xF1A6,
            "RESTORE", 0xF1A7, "RANDOM", 0xF1A8, "RADIAN", 0xF1AA, "REM", 0xF1AB, "STOP", 0xF1AC, "STEP", 0xF1AD,
            "THEN", 0xF1AE, "TRON", 0xF1AF, "TROFF", 0xF1B0, "TO", 0xF1B1, "WAIT", 0xF1B3, "ERROR", 0xF1B4,
            "LOCK", 0xF1B5, "UNLOCK", 0xF1B6,
        };

        // the keywords of the token table (PC-1500 or PC-1600)
        public static List<string> Keywords(bool pc1500)
        {
            var l = new List<string>();
            foreach (var kv in (pc1500 ? T1500 : T1600).ByLength) l.Add(kv.Key);
            return l;
        }

        sealed class Tab
        {
            public readonly Dictionary<int, string> ByCode = new Dictionary<int, string>();
            public readonly List<KeyValuePair<string, int>> ByLength = new List<KeyValuePair<string, int>>();
            public Tab(object[] list)
            {
                for (int i = 0; i < list.Length; i += 2)
                {
                    string n = (string)list[i];
                    int c = (int)list[i + 1];
                    ByCode[c] = n;
                    ByLength.Add(new KeyValuePair<string, int>(n, c));
                }
                ByLength.Sort((x, y) => y.Key.Length != x.Key.Length ? y.Key.Length.CompareTo(x.Key.Length) : string.CompareOrdinal(x.Key, y.Key));
            }
        }

        static readonly Tab T1600 = new Tab(TokenList);
        static readonly Tab T1500 = new Tab(TokenList1500);
        // keywords after which a line number constant (binary form 1F hi lo 00 on the PC-1600) can follow
        static readonly HashSet<string> LineKeywords = new HashSet<string> { "GOTO", "GOSUB", "THEN", "ELSE", "RESTORE", "RUN", "RESUME", "LIST", "LLIST", "DELETE", "RENUM", "AUTO" };

        // ---------------------------------------------------------------- program image -> text
        // data: the program image (.img); a trailing FFh is accepted
        public static string Detokenize(byte[] data, out string problem) { return Detokenize(data, false, out problem); }

        // the image is the same for both computers: lines only (the table of tokens differs)
        public static string Detokenize(byte[] data, bool pc1500, out string problem)
        {
            problem = null;
            Tab t = pc1500 ? T1500 : T1600;
            int n = data.Length;
            string err;
            if (n > 0 && data[n - 1] == 0xFF) n--;                     // image with the end marker
            var sb = new StringBuilder();
            int p = 0;
            while (p < n)
            {
                if (p + 3 > n) { problem = "truncated line at byte " + p; break; }
                int no = data[p] * 256 + data[p + 1];
                int len = data[p + 2];
                if (len < 1 || p + 3 + len > n) { problem = "bad line length at byte " + p; break; }
                sb.Append(no).Append(' ').Append(DetokenizeLine(t, data, p + 3, p + 3 + len)).Append("\r\n");
                p += 3 + len;
            }
            return sb.ToString();
        }

        static string DetokenizeLine(Tab t, byte[] d, int p, int end)
        {
            var sb = new StringBuilder();
            bool inString = false;
            while (p < end)
            {
                byte b = d[p];
                if (b == 0x0D) break;
                if (inString)
                {
                    sb.Append((char)b); p++;
                    if (b == 0x22) inString = false;
                    continue;
                }
                if (b == 0x22) { inString = true; sb.Append('"'); p++; continue; }
                if (b == 0x1F && p + 3 < end + 1)                       // line number constant
                {
                    sb.Append(d[p + 1] * 256 + d[p + 2]);
                    p += 3;
                    if (p < end && d[p] == 0x00) p++;
                    continue;
                }
                if (b >= 0xE0 && p + 1 < end)
                {
                    int code = b * 256 + d[p + 1];
                    string name;
                    if (!t.ByCode.TryGetValue(code, out name)) name = "<" + code.ToString("X4") + ">";
                    p += 2;
                    // a space before the keyword when the previous character would run into it
                    if (sb.Length > 0 && (char.IsLetterOrDigit(sb[sb.Length - 1]) || sb[sb.Length - 1] == ')' || sb[sb.Length - 1] == '$' || sb[sb.Length - 1] == '"' || sb[sb.Length - 1] == '#')) sb.Append(' ');
                    sb.Append(name);
                    if (name == "REM" || name == "DATA")
                    {
                        sb.Append(' ');
                        while (p < end && d[p] != 0x0D) { sb.Append((char)d[p]); p++; }
                        break;
                    }
                    // a space after the keyword when the next thing is a name, number, string or another keyword
                    if (p < end)
                    {
                        byte nb = d[p];
                        if (nb >= 0xE0 || nb == 0x1F || nb == 0x22 || char.IsLetterOrDigit((char)nb) || nb == 0x26) sb.Append(' ');
                    }
                    continue;
                }
                sb.Append((char)b); p++;
            }
            return sb.ToString();
        }

        // ---------------------------------------------------------------- text -> program image (.img)
        public static byte[] Tokenize(string text, out string problem) { return Tokenize(text, false, out problem); }

        // PC-1600: line numbers after GOTO etc. in binary form (1F hi lo 00). PC-1500: numbers stay ASCII. No end marker, no checksum.
        public static byte[] Tokenize(string text, bool pc1500, out string problem)
        {
            problem = null;
            Tab t = pc1500 ? T1500 : T1600;
            var lines = new SortedDictionary<int, byte[]>();
            int ln = 0;
            foreach (string raw in text.Split('\n'))
            {
                ln++;
                string s = raw.TrimEnd('\r', ' ', '\t', (char)0x1A);
                if (s.Trim().Length == 0) continue;
                int i = 0;
                while (i < s.Length && s[i] == ' ') i++;
                int start = i;
                while (i < s.Length && char.IsDigit(s[i])) i++;
                if (i == start) { problem = "text line " + ln + ": no line number"; return null; }
                int no = int.Parse(s.Substring(start, i - start));
                if (no < 1 || no > 65279) { problem = "text line " + ln + ": line number out of range"; return null; }
                var body = new List<byte>();
                string err = TokenizeBody(t, !pc1500, s.Substring(i), body);
                if (err != null) { problem = "line " + no + ": " + err; return null; }
                body.Add(0x0D);
                if (body.Count > 255) { problem = "line " + no + ": too long (" + body.Count + " bytes)"; return null; }
                var line = new List<byte> { (byte)(no >> 8), (byte)(no & 255), (byte)body.Count };
                line.AddRange(body);
                lines[no] = line.ToArray();                              // a repeated number replaces the earlier line
            }
            var all = new List<byte>();
            foreach (var kv in lines) all.AddRange(kv.Value);
            return all.ToArray();
        }

        static string TokenizeBody(Tab t, bool binaryNumbers, string s, List<byte> o)
        {
            int i = 0;
            bool lineMode = false;                  // a line number constant is expected here (after GOTO, THEN, ...)
            while (i < s.Length)
            {
                char c = s[i];
                if (c == ' ' || c == '\t') { i++; continue; }
                if (c == '"')                                            // string: copied as it is
                {
                    o.Add(0x22); i++;
                    while (i < s.Length) { char q = s[i++]; o.Add((byte)q); if (q == '"') break; }
                    lineMode = false;
                    continue;
                }
                if (binaryNumbers && lineMode && char.IsDigit(c))
                {
                    int j = i;
                    while (j < s.Length && char.IsDigit(s[j])) j++;
                    int v;
                    if (!int.TryParse(s.Substring(i, j - i), out v) || v > 65535) return "bad line number " + s.Substring(i, j - i);
                    o.Add(0x1F); o.Add((byte)(v >> 8)); o.Add((byte)(v & 255)); o.Add(0x00);
                    i = j;
                    // another number may follow after a comma (ON ... GOTO 10,20,30)
                    int k = i;
                    while (k < s.Length && s[k] == ' ') k++;
                    if (k < s.Length && s[k] == ',') { o.Add((byte)','); i = k + 1; }
                    else lineMode = false;
                    continue;
                }
                // keyword: longest match, case-insensitive (like the PC-1600, also inside longer names)
                string kw = null; int code = 0;
                foreach (var kv in t.ByLength)
                {
                    if (i + kv.Key.Length <= s.Length && string.Compare(s, i, kv.Key, 0, kv.Key.Length, StringComparison.OrdinalIgnoreCase) == 0)
                    { kw = kv.Key; code = kv.Value; break; }
                }
                if (kw != null)
                {
                    o.Add((byte)(code >> 8)); o.Add((byte)(code & 255));
                    i += kw.Length;
                    if (kw == "REM" || kw == "DATA")                     // the rest of the line is text
                    {
                        if (i < s.Length && s[i] == ' ') i++;
                        // the PC-1500 / PC-1600 drop the spaces of a DATA statement outside quotation marks (DATA 1, 2 is stored as 1,2);
                        // with the spaces kept, READ of a number fails with ERROR 1
                        bool inQuote = false;
                        for (; i < s.Length; i++)
                        {
                            if (s[i] == '"') inQuote = !inQuote;
                            if (kw == "DATA" && !inQuote && s[i] == ' ') continue;
                            o.Add((byte)s[i]);
                        }
                        return null;
                    }
                    lineMode = LineKeywords.Contains(kw);
                    continue;
                }
                lineMode = false;
                o.Add((byte)(c >= 'a' && c <= 'z' ? c - 32 : c));
                i++;
            }
            return null;
        }
    }


    // ====================================================================================================================
    // Check Basic: a basic check of a BASIC listing (text) for a chosen machine. The keyword lists of the PC-1500 and PC-1600 are
    // the token tables of the tokenizer; the lists of the other machines are approximate (an unknown word is only a warning).
    // ====================================================================================================================
    public sealed class CheckItem
    {
        public int Level;          // 0 OK, 1 warning, 2 fault, 3 information
        public string Text;
        public CheckItem(int level, string text) { Level = level; Text = text; }
    }

    public sealed class CheckProfile
    {
        public string Name;
        public int MinLine, MaxLine, MaxLen, MaxVar;     // allowed line numbers, longest line (characters), longest variable name (0 = any)
        public bool LowerOk, NeedThen, Tokenizer1500, Tokenizer1600;
        public HashSet<string> Kw = new HashSet<string>();
        public List<string> KwSorted;
        public override string ToString() { return Name; }
    }

    public static class BasicCheck
    {
        // free BASIC memory without a memory expansion (see Form1.WarnSize): the part taken by the Saver/Loader
        public const int Mem1500 = 5946, Mem1600 = 11834, Sl1500 = 571, Sl1600 = 3648;

        const string Core = "LET PRINT INPUT GOTO GOSUB IF THEN ELSE FOR TO STEP NEXT RETURN END STOP REM READ DATA RESTORE DIM ON CLS CLEAR BEEP WAIT PAUSE RUN LIST NEW CONT " +
            "PEEK POKE CALL AND OR NOT XOR MOD INT ABS SGN SQR SIN COS TAN ASN ACS ATN LOG LN EXP RND LEN LEFT$ RIGHT$ MID$ ASC CHR$ STR$ VAL TAB USING LPRINT LLIST " +
            "CSAVE CLOAD MERGE CHAIN RANDOM TRON TROFF PI DEG DMS RADIAN DEGREE GRAD ERROR MEM TIME INKEY$ AREAD ARUN LOCK UNLOCK STATUS";
        const string Sharp1350 = " CURSOR GCURSOR GPRINT LINE PSET PRESET POINT LF COLOR CSIZE GRAPH TEXT ROTATE LCURSOR RLINE LLINE SORGN GLCURSOR TEST FEED ZONE BREAK OFF OPN";
        const string Sharp1360 = " RENUM AUTO DELETE FILES SAVE LOAD INIT KILL NAME OPEN CLOSE BSAVE BLOAD COPY SET TIME$ DATE$ INSTR HEX$ INP OUT KEY MODE LOF LOC EOF DSKF TITLE PASS";
        const string SharpE500 = " DEF FN ELSE SWITCH CIRCLE PAINT LINE PSET PRESET POINT GPRINT GCURSOR CURSOR LOCATE KEY INSTR HEX$ BIN$ DATE$ TIME$ OPEN CLOSE INPUT# PRINT# SAVE LOAD FILES KILL NAME " +
            "COPY INIT RENUM AUTO DELETE MODE ON ERROR RESUME OUT INP BSAVE BLOAD VERIFY CHAIN APPEND OUTPUT AS SET USING BEEP ARUN STR$ FRAC CLEAR TITLE PASS LF MID$ LEFT$ RIGHT$ DMS DEG " +
            "ERN ERL UBOUND RESTORE LOCK UNLOCK EOF LOC LOF ROTATE DEGREE RADIAN GRAD";
        const string Casio = " LOCATE SCREEN OPEN CLOSE INPUT# PRINT# LINE KEY ON COM OFF INSTR HEX$ BIN$ DATE$ TIME$ TIME LOF EOF LOC SAVE LOAD FILES KILL NAME ERASE INIT FRAC SET OUT INP OUTPUT APPEND AS PEN";
        const string Model100 = " CALL CLS CLOSE COM CSAVE CLOAD CSRLIN DATE$ DAY$ DSKI$ DSKO$ EOF FILES FRE HEX$ INSTR IPL KEY KILL LINE LOF LPOS MAXFILES MENU MOTOR NAME OCT$ OPEN OUT INP POS PRESET " +
            "PSET RENUM RESUME RUN SAVE LOAD SCREEN SOUND SPACE$ STRING$ TIME$ TAB USING VARPTR WIDTH ERL ERR ERROR LPRINT LLIST POINT SGN CINT CSNG CDBL FIX LOG INPUT$ LINE";

        static readonly List<CheckProfile> profiles = new List<CheckProfile>();
        public static List<CheckProfile> Profiles { get { return profiles; } }

        static void Add(string name, int min, int max, int maxLen, int maxVar, bool lowerOk, bool needThen, bool t1500, bool t1600, string kw)
        {
            var p = new CheckProfile { Name = name, MinLine = min, MaxLine = max, MaxLen = maxLen, MaxVar = maxVar, LowerOk = lowerOk, NeedThen = needThen, Tokenizer1500 = t1500, Tokenizer1600 = t1600 };
            foreach (string k in kw.Split(' ')) if (k.Length > 0) p.Kw.Add(k);
            p.KwSorted = new List<string>(p.Kw);
            p.KwSorted.Sort((a, b) => b.Length != a.Length ? b.Length.CompareTo(a.Length) : string.CompareOrdinal(a, b));
            profiles.Add(p);
        }

        static BasicCheck()
        {
            string k1500 = string.Join(" ", Pc1600Basic.Keywords(true)), k1600 = string.Join(" ", Pc1600Basic.Keywords(false));
            Add("Sharp PC-1500(A)", 1, 65279, 80, 2, false, false, true, false, k1500);
            Add("Sharp PC-1600", 1, 65279, 255, 2, true, false, false, true, k1600);
            Add("Sharp PC-1350", 1, 65279, 80, 2, false, true, false, false, Core + Sharp1350);
            Add("Sharp PC-1360 and 1360K", 1, 65279, 80, 2, false, true, false, false, Core + Sharp1350 + Sharp1360);
            Add("Sharp PC-E500", 1, 65279, 255, 0, true, true, false, false, Core + SharpE500);
            Add("Sharp PC-E500S", 1, 65279, 255, 0, true, true, false, false, Core + SharpE500 + " CLS LOCATE FRE");
            Add("Casio FX-850P / FX-870P", 1, 65535, 255, 0, true, true, false, false, Core + Casio);
            Add("NEC PC-8201 / TRS-80 Model 100", 0, 65529, 255, 0, true, true, false, false, Core + Model100);
            Add("Other computer (general check)", 0, 65535, 0, 0, true, false, false, false, Core + Sharp1350 + Sharp1360 + SharpE500 + Casio + Model100);
        }

        // profile of the target computer of SerComet: 0 PC-1500, 1 PC-1600, 2 other Sharp, 3 Casio, 4 TRS-80 / NEC, 5 other UART
        public static int ProfileOfTarget(int model)
        {
            return model == 0 ? 0 : model == 1 ? 1 : model == 2 ? 3 : model == 3 ? 6 : model == 4 ? 7 : 8;
        }

        static readonly Regex RxStr = new Regex("\"[^\"]*\"?", RegexOptions.Compiled);
        static readonly Regex RxHex = new Regex("&[0-9A-Fa-f]+", RegexOptions.Compiled);
        static readonly Regex RxWord = new Regex("[A-Za-z][A-Za-z0-9]*\\$?", RegexOptions.Compiled);
        static readonly Regex RxTarget = new Regex("(?:GOTO|GOSUB|THEN|ELSE|RESTORE|RUN)\\s*(\\d+(?:\\s*,\\s*\\d+)*)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        static readonly Regex RxNumbered = new Regex("^\\s*(\\d+)\\s?(.*)$", RegexOptions.Compiled);

        sealed class Line { public int Number = -1; public int Index; public string Raw, Body, Code; public bool IsRem; }

        // the code of a statement line without strings, REM / DATA texts and hexadecimal numbers
        static string MaskCode(string body, out bool isRem)
        {
            isRem = false;
            string t = body.TrimStart();
            if (t.StartsWith("'") || Regex.IsMatch(t, "^REM", RegexOptions.IgnoreCase)) { isRem = true; return ""; }
            string s = RxStr.Replace(body, "\"\"");
            Match m = Regex.Match(s, "(?:^|:)\\s*(?:REM|DATA|')", RegexOptions.IgnoreCase);
            if (m.Success) s = s.Substring(0, m.Index + (s[m.Index] == ':' ? 1 : 0));
            return RxHex.Replace(s, "0");
        }

        // can the word be split into keywords and variable names? (the keywords found are collected)
        static bool Split(string w, int pos, CheckProfile p, List<string> found, Dictionary<int, bool> failed, bool lastVar)
        {
            if (pos >= w.Length) return true;
            int key = pos * 2 + (lastVar ? 1 : 0);
            if (failed.ContainsKey(key)) return false;
            if (char.IsDigit(w[pos]))                         // a number after a keyword (TO2, THEN100)
            {
                int e = pos; while (e < w.Length && char.IsDigit(w[e])) e++;
                if (Split(w, e, p, found, failed, false)) return true;
                failed[key] = true; return false;
            }
            foreach (string k in p.KwSorted)
            {
                if (pos + k.Length <= w.Length && string.CompareOrdinal(w, pos, k, 0, k.Length) == 0)
                {
                    int mark = found.Count;
                    found.Add(k);
                    if (Split(w, pos + k.Length, p, found, failed, false)) return true;
                    found.RemoveRange(mark, found.Count - mark);
                }
            }
            // a variable name: a letter, then letters / digits (at most MaxVar characters), then an optional $
            int max = p.MaxVar == 0 ? w.Length - pos : p.MaxVar;
            for (int len = Math.Min(max, w.Length - pos); len >= 1 && !lastVar; len--)         // two variable names cannot follow each other
            {
                string v = w.Substring(pos, len);
                if (!char.IsLetter(v[0])) continue;
                int next = pos + len;
                if (next < w.Length && w[next] == '$') next++;
                if (Split(w, next, p, found, failed, true)) return true;
            }
            failed[key] = true;
            return false;
        }

        static string Lines(List<int> n, int max)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < n.Count && i < max; i++) { if (i > 0) sb.Append(", "); sb.Append(n[i]); }
            if (n.Count > max) sb.Append(" ... (" + n.Count + " in all)");
            return sb.ToString();
        }

        public static List<CheckItem> Check(string text, CheckProfile p, out int faults, out int warnings)
        {
            var res = new List<CheckItem>();
            faults = 0; warnings = 0;
            Action<int, string> add = delegate (int lv, string t) { res.Add(new CheckItem(lv, t)); };

            var lines = new List<Line>();
            var unnumbered = new List<int>();
            int idx = 0;
            foreach (string raw0 in text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\u001A", "").Split('\n'))
            {
                idx++;
                string raw = raw0.TrimEnd();
                if (raw.Trim().Length == 0) continue;
                var ln = new Line { Index = idx, Raw = raw };
                Match m = RxNumbered.Match(raw);
                if (m.Success && m.Groups[1].Value.Length < 10) { ln.Number = int.Parse(m.Groups[1].Value); ln.Body = m.Groups[2].Value; }
                else { ln.Body = raw; unnumbered.Add(idx); }
                bool rem;
                ln.Code = MaskCode(ln.Body, out rem);
                ln.IsRem = rem;
                lines.Add(ln);
            }
            add(3, "Machine: " + p.Name + ". " + lines.Count + " lines.");
            if (lines.Count == 0) { add(2, "The listing is empty: no lines."); faults++; return res; }

            // 1. every line has a number
            if (unnumbered.Count == 0) add(0, "All lines have a line number: OK");
            else { add(2, "All lines have a line number: Fail (file lines " + Lines(unnumbered, 8) + ")"); faults++; }

            // 2. no repeated numbers
            var seen = new Dictionary<int, int>(); var dups = new List<int>();
            foreach (Line l in lines) if (l.Number >= 0) { if (seen.ContainsKey(l.Number)) { if (!dups.Contains(l.Number)) dups.Add(l.Number); } else seen[l.Number] = 1; }
            if (dups.Count == 0) add(0, "No repeated line numbers: OK");
            else { add(2, "No repeated line numbers: Fail (repeated: " + Lines(dups, 10) + ")"); faults++; }

            // 3. range of the line numbers
            var bad = new List<int>();
            foreach (Line l in lines) if (l.Number >= 0 && (l.Number < p.MinLine || l.Number > p.MaxLine)) bad.Add(l.Number);
            if (bad.Count == 0) add(0, "Line numbers in the allowed range (" + p.MinLine + " to " + p.MaxLine + "): OK");
            else { add(2, "Line numbers in the allowed range (" + p.MinLine + " to " + p.MaxLine + "): Fail (" + Lines(bad, 10) + ")"); faults++; }

            // 4. order
            bool asc = true; int prev = -1;
            foreach (Line l in lines) if (l.Number >= 0) { if (l.Number < prev) asc = false; prev = l.Number; }
            if (asc) add(0, "Line numbers in ascending order: OK");
            else { add(1, "Line numbers in ascending order: Fail (the computer sorts the lines when they are entered, but check the file)"); warnings++; }

            // 5. GOTO / GOSUB / THEN / RESTORE targets
            var missing = new List<string>(); var toRem = new List<int>(); var remLines = new HashSet<int>();
            foreach (Line l in lines) if (l.IsRem && l.Number >= 0) remLines.Add(l.Number);
            foreach (Line l in lines)
            {
                foreach (Match m in RxTarget.Matches(l.Code))
                    foreach (string t in m.Groups[1].Value.Split(','))
                    {
                        int tn; if (!int.TryParse(t.Trim(), out tn)) continue;
                        if (!seen.ContainsKey(tn)) missing.Add(l.Number + " -> " + tn);
                        else if (remLines.Contains(tn) && !toRem.Contains(tn)) toRem.Add(tn);
                    }
            }
            if (missing.Count == 0) add(0, "All GOTO / GOSUB / THEN / RESTORE targets exist: OK");
            else { add(2, "All GOTO / GOSUB / THEN / RESTORE targets exist: Fail (line -> missing target: " + string.Join("; ", missing.GetRange(0, Math.Min(8, missing.Count)).ToArray()) + (missing.Count > 8 ? " ... " + missing.Count + " in all" : "") + ")"); faults++; }
            if (toRem.Count > 0) add(3, "Jumps point at REM lines " + Lines(toRem, 8) + ": do not use Skip REM for this program, or the REM lines are kept automatically.");

            // 6. characters
            var ctl = new List<int>(); var lower = new List<int>(); var hi = new List<int>();
            foreach (Line l in lines)
            {
                foreach (char c in l.Raw) if (c < 32 && c != 9) { ctl.Add(l.Number >= 0 ? l.Number : -l.Index); break; }
                foreach (char c in l.Raw) if (c > 126) { hi.Add(l.Number >= 0 ? l.Number : -l.Index); break; }
                if (!l.IsRem) foreach (char c in l.Code) if (c >= 'a' && c <= 'z') { lower.Add(l.Number >= 0 ? l.Number : -l.Index); break; }
            }
            if (ctl.Count == 0 && hi.Count == 0) add(0, "Characters allowed (no control or non-ASCII characters): OK");
            else { add(2, "Characters allowed (no control or non-ASCII characters): Fail (lines " + Lines(ctl.Count > 0 ? ctl : hi, 8) + ")"); faults++; }
            if (lower.Count == 0) add(0, "Capital letters in the program text: OK");
            else if (p.LowerOk) add(0, "Small and capital letters: lowercase letters in " + lower.Count + " lines (allowed on this machine)");
            else { add(1, "Capital letters in the program text: Fail (lowercase letters outside strings in " + lower.Count + " lines, e.g. " + Lines(lower, 4) + "; SerComet converts them to capitals, a typed-in program needs capitals)"); warnings++; }

            // 7. line length
            if (p.MaxLen > 0)
            {
                var longL = new List<int>();
                foreach (Line l in lines) if (l.Raw.Trim().Length > p.MaxLen) longL.Add(l.Number >= 0 ? l.Number : -l.Index);
                if (longL.Count == 0) add(0, "Line length within " + p.MaxLen + " characters: OK");
                else { add(2, "Line length within " + p.MaxLen + " characters: Fail (lines " + Lines(longL, 8) + ")"); faults++; }
            }

            // 8. quotation marks and parentheses
            var quote = new List<int>(); var paren = new List<int>();
            foreach (Line l in lines)
            {
                if (l.IsRem) continue;
                string t = l.Body; Match mm = Regex.Match(t, "(?:^|:)\\s*(?:REM|DATA)", RegexOptions.IgnoreCase);
                int q = 0; foreach (char c in (mm.Success ? t.Substring(0, mm.Index) : t)) if (c == '"') q++;
                if (q % 2 == 1) quote.Add(l.Number);
                int depth = 0; bool neg = false;
                foreach (char c in l.Code) { if (c == '(') depth++; else if (c == ')') { depth--; if (depth < 0) neg = true; } }
                if (depth != 0 || neg) paren.Add(l.Number);
            }
            if (quote.Count == 0) add(0, "Quotation marks paired: OK");
            else { add(1, "Quotation marks paired: Fail (an unclosed string is allowed at the end of a line; lines " + Lines(quote, 8) + ")"); warnings++; }
            if (paren.Count == 0) add(0, "Parentheses paired: OK");
            else { add(2, "Parentheses paired: Fail (lines " + Lines(paren, 8) + ")"); faults++; }

            // 9. words: statements, functions, variables
            var unknown = new List<string>(); var counts = new Dictionary<string, int>();
            var longVar = new List<string>();
            foreach (Line l in lines)
            {
                foreach (Match m in RxWord.Matches(l.Code))
                {
                    string w = m.Value.ToUpperInvariant();
                    if (w.Length == 1 || (w.Length == 2 && w[1] == '$')) continue;
                    var found = new List<string>();
                    if (Split(w, 0, p, found, new Dictionary<int, bool>(), false))
                    { foreach (string k in found) { int c; counts.TryGetValue(k, out c); counts[k] = c + 1; } }
                    else if (!unknown.Contains(w + " (" + (l.Number >= 0 ? l.Number.ToString() : "?") + ")") && unknown.Count < 200) unknown.Add(w + " (" + (l.Number >= 0 ? l.Number.ToString() : "?") + ")");
                }
            }
            if (unknown.Count == 0) add(0, "All statements and functions are known on this machine: OK");
            else
            {
                string kind = p.Tokenizer1500 || p.Tokenizer1600 ? "not a keyword or a variable name of this machine" : "not in the keyword list of this machine (the list is approximate)";
                add(p.Tokenizer1500 || p.Tokenizer1600 ? 2 : 1, "All statements and functions are known on this machine: Fail (" + kind + ": " + string.Join(", ", unknown.GetRange(0, Math.Min(8, unknown.Count)).ToArray()) + (unknown.Count > 8 ? " ..." : "") + ")");
                if (p.Tokenizer1500 || p.Tokenizer1600) faults++; else warnings++;
            }

            // 10. structure: FOR / NEXT, GOSUB / RETURN, READ / DATA, IF without THEN
            int nFor = 0, nNext = 0, nGosub = 0, nReturn = 0, nRead = 0, nData = 0, nIf = 0, nThen = 0;
            foreach (Line l in lines)
            {
                foreach (Match m in RxWord.Matches(l.Code))
                {
                    var found = new List<string>();
                    if (!Split(m.Value.ToUpperInvariant(), 0, p, found, new Dictionary<int, bool>(), false)) continue;
                    foreach (string k in found)
                    {
                        if (k == "FOR") nFor++; else if (k == "NEXT") nNext++; else if (k == "GOSUB") nGosub++; else if (k == "RETURN") nReturn++;
                        else if (k == "READ") nRead++; else if (k == "IF") nIf++; else if (k == "THEN" || k == "GOTO") nThen++;
                    }
                }
                if (Regex.IsMatch(l.Body, "(?:^|:)\\s*DATA", RegexOptions.IgnoreCase)) nData++;
            }
            if (nFor == nNext || (nNext > nFor && false)) add(0, "FOR and NEXT match (" + nFor + " / " + nNext + "): OK");
            else { add(1, "FOR and NEXT match (" + nFor + " FOR / " + nNext + " NEXT): Fail (a NEXT can serve several FORs; check the loops)"); warnings++; }
            if ((nGosub > 0) == (nReturn > 0)) add(0, "GOSUB and RETURN present together: OK");
            else { add(1, "GOSUB and RETURN present together: Fail (" + nGosub + " GOSUB / " + nReturn + " RETURN)"); warnings++; }
            if (nRead > 0 && nData == 0) { add(2, "READ has DATA to read: Fail (no DATA statement)"); faults++; }
            else add(0, "READ has DATA to read: OK");
            if (p.NeedThen && nIf > nThen) { add(2, "Every IF has a THEN or GOTO: Fail (" + nIf + " IF / " + nThen + " THEN or GOTO)"); faults++; }
            else if (p.NeedThen) add(0, "Every IF has a THEN or GOTO: OK");

            // 11. the tokenizer of SerComet (PC-1500 / PC-1600): the program as the machine will store it
            if (p.Tokenizer1500 || p.Tokenizer1600)
            {
                string pr; byte[] img = null;
                try { img = Pc1600Basic.Tokenize(string.Join("\r\n", lines.ConvertAll(l => l.Raw).ToArray()), p.Tokenizer1500, out pr); }
                catch (Exception ex) { pr = ex.Message; }
                if (img == null) { add(2, "Tokenizer (the program in the machine's own code): Fail (" + pr + ")"); faults++; }
                else
                {
                    int size = img.Length + 1;
                    string sizes = size + " B in the machine's own code";
                    int free = p.Tokenizer1500 ? Mem1500 : Mem1600, sl = p.Tokenizer1500 ? Sl1500 : Sl1600;
                    if (size > free) { add(1, "Program size: " + sizes + ", more than the free memory of the basic machine without a RAM card (about " + free + " B): check it against MEM of your machine"); warnings++; }
                    else if (size > free - sl) { add(1, "Program size: " + sizes + " fits (about " + free + " B free) but not together with the Saver/Loader (about " + (free - sl) + " B)"); warnings++; }
                    else add(0, "Program size: " + sizes + ", fits the free memory (about " + (free - sl) + " B with the Saver/Loader): OK");
                }
            }

            // 12. information
            int remCount = 0, remBytes = 0;
            foreach (Line l in lines) if (l.IsRem) { remCount++; remBytes += l.Raw.Length + 1; }
            if (remCount > 0) add(3, "REM lines: " + remCount + " (about " + remBytes + " B); Skip REM leaves them out when the program is sent.");
            if (p.Tokenizer1500 || p.Tokenizer1600)
                add(3, "Spaces in DATA statements outside quotation marks are removed by the machine (DATA 1, 2 is stored as 1,2); SerComet does the same.");
            if (p.Tokenizer1500) add(3, "PC-1500(A): the IF statement may have no THEN (IF A=1 GOTO 100 or IF A=1 PRINT 1).");
            if (p.Name.StartsWith("Sharp PC-1360")) add(3, "PC-1360: the PC-1350 statements plus the file and disk statements (OPEN, SAVE, LOAD ...).");
            return res;
        }
    }

    // the result window of Check Basic: a machine can be chosen and the check repeated
    public class CheckBasicForm : Form
    {
        readonly string text, fileName;
        readonly ComboBox combo;
        readonly RichTextBox box;
        public CheckBasicForm(string fileName, string text, int defaultProfile)
        {
            this.text = text; this.fileName = fileName;
            Text = "Check Basic: " + fileName;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(760, 520);
            MinimumSize = new Size(560, 320);
            ShowInTaskbar = false;
            try { Icon = Icons.AppIcon(); } catch { }
            var lbl = new Label { Text = "Machine:", Left = 10, Top = 14, Width = 60 };
            combo = new ComboBox { Left = 72, Top = 10, Width = 280, DropDownStyle = ComboBoxStyle.DropDownList };
            foreach (var p in BasicCheck.Profiles) combo.Items.Add(p);
            combo.SelectedIndex = defaultProfile;
            var again = new Button { Text = "Check again", Left = 362, Top = 8, Width = 100, Height = 26 };
            var copy = new Button { Text = "Copy result", Left = 470, Top = 8, Width = 100, Height = 26 };
            var close = new Button { Text = "Close", Left = 650, Top = 8, Width = 100, Height = 26, DialogResult = DialogResult.Cancel, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            box = new RichTextBox { Left = 10, Top = 44, Width = 740, Height = 466, ReadOnly = true, Font = new Font("Consolas", 9.5F), BackColor = Color.White, DetectUrls = false,
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right };
            Controls.AddRange(new Control[] { lbl, combo, again, copy, close, box });
            CancelButton = close;
            close.Click += delegate { Close(); };
            combo.SelectedIndexChanged += delegate { Run(); };
            again.Click += delegate { Run(); };
            copy.Click += delegate { try { Clipboard.SetText(box.Text); } catch { } };
            Shown += delegate { Run(); };
        }

        void Run()
        {
            var p = (CheckProfile)combo.SelectedItem;
            int faults, warns;
            var items = BasicCheck.Check(text, p, out faults, out warns);
            box.Clear();
            string head = faults == 0 ? "Basic listing seems OK" + (warns > 0 ? " (" + warns + " warning" + (warns > 1 ? "s" : "") + ")" : "")
                                      : "Basic listing has faults (" + faults + (faults == 1 ? " fault" : " faults") + (warns > 0 ? ", " + warns + (warns == 1 ? " warning" : " warnings") : "") + ")";
            Append(head + "\r\n", new Font(box.Font.FontFamily, 12F, FontStyle.Bold), faults == 0 ? Color.FromArgb(20, 130, 40) : Color.FromArgb(190, 30, 30));
            Append(fileName + " checked for " + p.Name + "\r\n\r\n", box.Font, Color.Black);
            foreach (var it in items)
            {
                string tag = it.Level == 0 ? "[ OK ]  " : it.Level == 1 ? "[WARN]  " : it.Level == 2 ? "[FAULT] " : "[info]  ";
                Color c = it.Level == 0 ? Color.FromArgb(20, 130, 40) : it.Level == 1 ? Color.FromArgb(190, 110, 0) : it.Level == 2 ? Color.FromArgb(190, 30, 30) : Color.FromArgb(70, 70, 120);
                Append(tag, new Font(box.Font, FontStyle.Bold), c);
                Append(it.Text + "\r\n", box.Font, Color.Black);
            }
            box.SelectionStart = 0; box.ScrollToCaret();
        }

        void Append(string s, Font f, Color c)
        {
            box.SelectionStart = box.TextLength; box.SelectionLength = 0;
            box.SelectionFont = f; box.SelectionColor = c;
            box.AppendText(s);
        }
    }

    // a simple editor (like Notepad) for BASIC text files; the bytes are kept as they are (Latin-1)
    public class EditorForm : Form
    {
        readonly string path;
        readonly TextBox txt;
        bool modified;
        static readonly Encoding Latin = Encoding.GetEncoding(28591);
        public EditorForm(string path)
        {
            this.path = path;
            Text = "Edit: " + Path.GetFileName(path);
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(760, 560);
            MinimumSize = new Size(420, 260);
            try { Icon = Icons.AppIcon(); } catch { }
            string content = Latin.GetString(File.ReadAllBytes(path)).Replace("\u001A", "");
            content = content.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");
            txt = new TextBox { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = new Font("Consolas", 10F), AcceptsTab = true, Text = content, MaxLength = 0 };
            var menu = new MenuStrip();
            var file = new ToolStripMenuItem("File");
            var save = new ToolStripMenuItem("Save", null, delegate { Save(); }) { ShortcutKeys = Keys.Control | Keys.S };
            var close = new ToolStripMenuItem("Close", null, delegate { Close(); });
            file.DropDownItems.Add(save); file.DropDownItems.Add(close);
            menu.Items.Add(file);
            Controls.Add(txt); Controls.Add(menu); MainMenuStrip = menu;
            txt.Select(0, 0);
            txt.TextChanged += delegate { if (!modified) { modified = true; Text = "Edit: " + Path.GetFileName(path) + " *"; } };
            FormClosing += delegate (object s, FormClosingEventArgs e)
            {
                if (!modified) return;
                var r = MessageBox.Show(this, "Save the changes to " + Path.GetFileName(path) + "?", "SerComet", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (r == DialogResult.Cancel) e.Cancel = true;
                else if (r == DialogResult.Yes && !Save()) e.Cancel = true;
            };
        }

        bool Save()
        {
            try
            {
                File.WriteAllBytes(path, Latin.GetBytes(txt.Text));
                modified = false; Text = "Edit: " + Path.GetFileName(path);
                return true;
            }
            catch (Exception ex) { MessageBox.Show(this, "Cannot save: " + ex.Message, "SerComet", MessageBoxButtons.OK, MessageBoxIcon.Error); return false; }
        }
    }

    // a small question with one text field (Rename)
    public class PromptForm : Form
    {
        readonly TextBox box;
        public string Value { get { return box.Text.Trim(); } }
        public PromptForm(string title, string label, string value)
        {
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false; ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(380, 100);
            try { Icon = Icons.AppIcon(); } catch { }
            Controls.Add(new Label { Text = label, Left = 12, Top = 12, Width = 356 });
            box = new TextBox { Left = 12, Top = 34, Width = 356, Text = value };
            var ok = new Button { Text = "OK", Left = 198, Top = 64, Width = 80, DialogResult = DialogResult.OK };
            var cancel = new Button { Text = "Cancel", Left = 288, Top = 64, Width = 80, DialogResult = DialogResult.Cancel };
            Controls.AddRange(new Control[] { box, ok, cancel });
            AcceptButton = ok; CancelButton = cancel;
            Shown += delegate { box.Focus(); int dot = value.LastIndexOf('.'); box.Select(0, dot > 0 ? dot : value.Length); };
        }
    }

    // ToolTip that wraps its text at about 30 characters per line
    public class WrapToolTip : ToolTip
    {
        const int Width = 30;

        public new void SetToolTip(Control control, string caption)
        {
            base.SetToolTip(control, Wrap(caption));
        }

        public static string Wrap(string text)
        {
            if (text == null) return null;
            var result = new StringBuilder();
            foreach (string para in text.Replace("\r\n", "\n").Split('\n'))
            {
                int col = 0;
                foreach (string word in para.Split(' '))
                {
                    if (word.Length == 0) { if (col > 0) { result.Append(' '); col++; } continue; }
                    if (col > 0 && col + 1 + word.Length > Width) { result.Append("\r\n"); col = 0; }
                    else if (col > 0) { result.Append(' '); col++; }
                    result.Append(word);
                    col += word.Length;
                }
                result.Append("\r\n");
            }
            return result.ToString().TrimEnd('\r', '\n');
        }
    }

    // Window for the folders of the programs (one per target computer)
    public class LocationsForm : Form
    {
        readonly TextBox[] boxes = new TextBox[11];
        public string[] Result;

        public LocationsForm(string[] current, string[] defaults)
        {
            Text = "Locations of the programs";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(560, 12 + 11 * 32 + 52);
            try { Icon = Icons.AppIcon(); } catch { }
            var tt = new WrapToolTip { AutoPopDelay = 9000, InitialDelay = 400, ShowAlways = true };
            string[] names = { "PC-1500", "PC-1600", "PC-1350", "PC-1360", "PC-E500", "PC-E500S", "PC-1450..75", "Sharp other", "FX-850/870", "TRS-80", "UART Other" };
            for (int i = 0; i < 11; i++)
            {
                int row = i;
                Controls.Add(new Label { Text = names[i] + ":", Left = 12, Top = 17 + i * 32, Width = 85 });
                boxes[i] = new TextBox { Left = 100, Top = 13 + i * 32, Width = 365, Text = current[i] };
                var browse = new Button { Text = "Browse...", Left = 472, Top = 12 + i * 32, Width = 78, Height = 24 };
                browse.Click += delegate
                {
                    using (var f = new FolderBrowserDialog())
                    {
                        f.SelectedPath = boxes[row].Text;
                        if (f.ShowDialog() == DialogResult.OK) boxes[row].Text = f.SelectedPath;
                    }
                };
                tt.SetToolTip(boxes[i], "Folder of the programs for this computer. It is selected together with the target computer.");
                tt.SetToolTip(browse, "Choose the folder.");
                Controls.AddRange(new Control[] { boxes[i], browse });
            }
            var defs = new Button { Text = "Defaults", Left = 12, Top = 12 + 11 * 32 + 14, Width = 90, Height = 26 };
            defs.Click += delegate { for (int i = 0; i < 11; i++) boxes[i].Text = defaults[i]; };
            tt.SetToolTip(defs, "Restore the standard folders (in Documents).");
            var ok = new Button { Text = "OK", Left = 370, Top = 12 + 11 * 32 + 14, Width = 85, Height = 26, DialogResult = DialogResult.OK };
            var cancel = new Button { Text = "Cancel", Left = 465, Top = 12 + 11 * 32 + 14, Width = 85, Height = 26, DialogResult = DialogResult.Cancel };
            Controls.AddRange(new Control[] { defs, ok, cancel });
            AcceptButton = ok;
            CancelButton = cancel;
            FormClosing += delegate
            {
                if (DialogResult != DialogResult.OK) return;
                Result = new string[11];
                for (int i = 0; i < 11; i++) Result[i] = boxes[i].Text.Trim().Length > 0 ? boxes[i].Text.Trim() : defaults[i];
            };
        }
    }

    // Confirmation before a file is deleted: "Delete file" / "Cancel"
    public class DeleteConfirmForm : Form
    {
        public DeleteConfirmForm(string fileName)
        {
            Text = "Delete file";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(360, 120);
            try { Icon = Icons.AppIcon(); } catch { }
            Controls.Add(new Label { Text = "Delete this file permanently?", Left = 14, Top = 14, Width = 330, Height = 20 });
            Controls.Add(new Label { Text = fileName, Left = 14, Top = 38, Width = 330, Height = 36, Font = new Font("Segoe UI", 9F, FontStyle.Bold) });
            var del = new Button { Text = "Delete file", Left = 150, Top = 82, Width = 95, Height = 26, DialogResult = DialogResult.OK };
            var cancel = new Button { Text = "Cancel", Left = 252, Top = 82, Width = 95, Height = 26, DialogResult = DialogResult.Cancel };
            Controls.AddRange(new Control[] { del, cancel });
            AcceptButton = cancel;      // Enter = the safe choice
            CancelButton = cancel;
        }
    }

    // Preview window (read only). With an original text too (program images) two radio buttons switch the view.
    public class PreviewForm : Form
    {
        readonly TextBox box;

        public PreviewForm(string title, string text) : this(title, text, null) { }

        public PreviewForm(string title, string text, string original)
        {
            Text = title;
            ClientSize = new Size(640, 460);
            StartPosition = FormStartPosition.CenterParent;
            try { Icon = Icons.AppIcon(); } catch { }
            box = new TextBox
            {
                Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false,
                Font = new Font("Consolas", 9.5F), Text = text, BackColor = Color.White, SelectionStart = 0
            };
            Controls.Add(box);
            if (original != null)
            {
                var top = new Panel { Dock = DockStyle.Top, Height = 28 };
                var rbOrig = new RadioButton { Text = "Original content", Left = 8, Top = 5, AutoSize = true };
                var rbDetok = new RadioButton { Text = "Detokenized", Left = 140, Top = 5, AutoSize = true, Checked = true };
                rbOrig.CheckedChanged += delegate { if (rbOrig.Checked) { box.Text = original; box.SelectionStart = 0; } };
                rbDetok.CheckedChanged += delegate { if (rbDetok.Checked) { box.Text = text; box.SelectionStart = 0; } };
                top.Controls.AddRange(new Control[] { rbOrig, rbDetok });
                var tt = new WrapToolTip { AutoPopDelay = 9000, InitialDelay = 400, ShowAlways = true };
                tt.SetToolTip(rbOrig, "The bytes of the file (hex dump).");
                tt.SetToolTip(rbDetok, "The program as BASIC text.");
                Controls.Add(top);
            }
        }
    }

    // Round button with a question mark (opens the About window)
    public class RoundHelpButton : Control
    {
        bool hover;

        public RoundHelpButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, true);
            Cursor = Cursors.Hand;
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            using (var gp = new System.Drawing.Drawing2D.GraphicsPath())
            {
                gp.AddEllipse(0, 0, Width - 1, Height - 1);
                Region = new Region(gp);                       // clicks only inside the circle
            }
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            var r = new Rectangle(1, 1, Width - 3, Height - 3);
            // system colours: the same as the window background, so the button is discreet
            using (var b = new SolidBrush(hover ? SystemColors.ControlLightLight : SystemColors.Control)) g.FillEllipse(b, r);
            using (var p = new Pen(hover ? SystemColors.ControlDarkDark : SystemColors.ControlDark, 1.5f)) g.DrawEllipse(p, r);
            using (var f = new Font("Segoe UI", 9.2F, FontStyle.Bold))
            {
                SizeF sz = g.MeasureString("?", f);
                g.DrawString("?", f, SystemBrushes.ControlText, (Width - sz.Width) / 2 + 0.5f, (Height - sz.Height) / 2);
            }
        }
    }

    // ---- BASIC text tools: Renumber and Remove REM (text listings, one numbered line per row)
    public sealed class RenumberResult
    {
        public string Text, Error;
        public int Count, OldFirst, OldLast, NewFirst, NewLast, RefsChanged, MaxStepNeeded;
        public bool Overflow;
        public List<string> Missing = new List<string>();
    }

    public static class BasicTools
    {
        static readonly Regex RefRx = new Regex(@"\G(?:GOTO|GOSUB|THEN|ELSE|RESTORE|RUN)\s*\d+(?:\s*,\s*\d+)*", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex RemRx = new Regex(@"\GREM\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex DataRx = new Regex(@"\GDATA\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex LineRx = new Regex(@"^\s*(\d+)(.*)$", RegexOptions.Compiled);

        static string[] SplitLines(string text, out string nl)
        {
            nl = text.Contains("\r\n") || !text.Contains("\n") ? "\r\n" : "\n";
            var l = new List<string>(text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'));
            while (l.Count > 0 && l[l.Count - 1].Trim().Length == 0) l.RemoveAt(l.Count - 1);
            return l.ToArray();
        }

        // the last line of a renumbering with the given start and step (0 lines -> start)
        public static int CountNumbered(string text)
        {
            string nl; int n = 0;
            foreach (string l in SplitLines(text, out nl)) if (LineRx.IsMatch(l)) n++;
            return n;
        }

        public static RenumberResult Renumber(string text, int start, int step, int maxLine)
        {
            var res = new RenumberResult();
            string nl; string[] lines = SplitLines(text, out nl);
            var nums = new List<int>(); var bodies = new List<string>();
            var map = new Dictionary<int, int>();
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Trim().Length == 0) continue;
                var m = LineRx.Match(lines[i]);
                if (!m.Success) { res.Error = "line " + (i + 1) + " of the file has no line number."; return res; }
                int no = int.Parse(m.Groups[1].Value.Length > 9 ? "999999999" : m.Groups[1].Value);
                if (map.ContainsKey(no)) { res.Error = "the line number " + no + " is repeated."; return res; }
                map[no] = -1; nums.Add(no); bodies.Add(m.Groups[2].Value);
            }
            res.Count = nums.Count;
            if (res.Count == 0) { res.Error = "the file has no numbered lines."; return res; }
            res.OldFirst = nums[0]; res.OldLast = nums[nums.Count - 1];
            long last = start + (long)(res.Count - 1) * step;
            if (last > maxLine) { res.Overflow = true; res.NewLast = (int)Math.Min(last, int.MaxValue); return res; }
            for (int i = 0; i < nums.Count; i++) map[nums[i]] = start + i * step;
            res.NewFirst = start; res.NewLast = (int)last;
            var sb = new StringBuilder(); var missing = new HashSet<string>();
            for (int i = 0; i < nums.Count; i++)
            {
                sb.Append(map[nums[i]]).Append(Fix(bodies[i], map, missing, ref res.RefsChanged)).Append(nl);
            }
            res.Text = sb.ToString();
            foreach (string s in missing) res.Missing.Add(s);
            res.Missing.Sort((a, b) => a.Length != b.Length ? a.Length.CompareTo(b.Length) : string.CompareOrdinal(a, b));
            return res;
        }

        // the line numbers after GOTO, GOSUB, THEN, ELSE, RESTORE, RUN (also the lists of ON ... GOTO) outside strings, REM and DATA
        static string Fix(string body, Dictionary<int, int> map, HashSet<string> missing, ref int changed)
        {
            var sb = new StringBuilder();
            int i = 0, ch = 0;
            while (i < body.Length)
            {
                char c = body[i];
                if (c == '"')
                {
                    int e = body.IndexOf('"', i + 1); if (e < 0) e = body.Length - 1;
                    sb.Append(body, i, e - i + 1); i = e + 1; continue;
                }
                bool startOk = i == 0 || !char.IsLetter(body[i - 1]);
                if (c == '\'' || (startOk && RemRx.IsMatch(body, i))) { sb.Append(body, i, body.Length - i); break; }
                if (startOk && DataRx.IsMatch(body, i))
                {
                    int j = i; bool q = false;
                    while (j < body.Length && !(body[j] == ':' && !q)) { if (body[j] == '"') q = !q; j++; }
                    sb.Append(body, i, j - i); i = j; continue;
                }
                if (startOk)
                {
                    var m = RefRx.Match(body, i);
                    if (m.Success)
                    {
                        int mc = 0; var miss = missing;
                        string r = Regex.Replace(m.Value, @"\d+", delegate (Match d)
                        {
                            int old;
                            if (d.Value.Length < 10 && int.TryParse(d.Value, out old) && map.ContainsKey(old)) { mc++; return map[old].ToString(); }
                            miss.Add(d.Value); return d.Value;
                        });
                        ch += mc; sb.Append(r); i += m.Length; continue;
                    }
                }
                sb.Append(c); i++;
            }
            changed += ch;
            return sb.ToString();
        }

        // Remove REM: whole REM lines are dropped; a comment after a colon or an apostrophe at the end of a line is cut off.
        // keepFirst: the first REM line (e.g. the title of the program) stays as it is.
        // retarget: a REM line that GOTO / GOSUB / THEN ... points to is removed too and the jumps go to the next line that stays (what the
        // program executed after the REM anyway); without it such a REM stays as a bare "REM". A REM at the very end (no line after it) always stays.
        public static string RemoveRem(string text, bool keepFirst, bool retarget, out int removedLines, out int keptLines, out int cutComments, out List<string> moved)
        {
            removedLines = keptLines = cutComments = 0; moved = new List<string>();
            string nl; string[] lines = SplitLines(text, out nl);
            int n = lines.Length;
            var no = new string[n]; var body = new string[n]; var cmt = new int[n];
            var refs = new HashSet<string>();
            for (int i = 0; i < n; i++)
            {
                var m = LineRx.Match(lines[i]);
                if (!m.Success) { cmt[i] = -2; continue; }
                no[i] = m.Groups[1].Value; body[i] = m.Groups[2].Value; cmt[i] = CommentStart(body[i]);
                string code = cmt[i] == 0 ? "" : cmt[i] > 0 ? body[i].Substring(0, cmt[i]) : body[i];
                foreach (Match r in Regex.Matches(code, @"(?:GOTO|GOSUB|THEN|ELSE|RESTORE|RUN)\s*(\d+(?:\s*,\s*\d+)*)", RegexOptions.IgnoreCase))
                    foreach (string x in r.Groups[1].Value.Split(',')) refs.Add(x.Trim());
            }
            // a line that only had a trailing comment and nothing else counts as a REM line as well
            var isRem = new bool[n];
            for (int i = 0; i < n; i++)
            {
                if (cmt[i] == -2) continue;
                string code = cmt[i] > 0 ? body[i].Substring(0, cmt[i]).TrimEnd().TrimEnd(':').Trim() : body[i];
                isRem[i] = cmt[i] == 0 || (cmt[i] > 0 && code.Length == 0);
            }
            int keepIdx = -1;
            if (keepFirst) for (int i = 0; i < n; i++) if (isRem[i]) { keepIdx = i; break; }
            var remove = new bool[n]; var map = new Dictionary<int, int>();
            string nextKept = null;
            for (int i = n - 1; i >= 0; i--)
            {
                if (cmt[i] == -2) continue;                     // a line without a number stays
                bool ref1 = refs.Contains(no[i]);
                if (isRem[i] && i != keepIdx && (!ref1 || (retarget && nextKept != null)))
                {
                    remove[i] = true;
                    if (ref1) { int ov; if (nextKept.Length < 10 && int.TryParse(no[i], out ov)) map[ov] = int.Parse(nextKept); moved.Add(no[i] + "->" + nextKept); }
                    continue;
                }
                nextKept = no[i];
            }
            moved.Reverse();
            var sb = new StringBuilder(); var dummy = new HashSet<string>(); int chg = 0;
            for (int i = 0; i < n; i++)
            {
                if (cmt[i] == -2) { sb.Append(lines[i]).Append(nl); continue; }
                if (remove[i]) { removedLines++; continue; }
                if (i == keepIdx) { keptLines++; sb.Append(lines[i]).Append(nl); continue; }
                string b = body[i];
                if (isRem[i]) { keptLines++; sb.Append(no[i]).Append(" REM").Append(nl); continue; }      // referenced REM that has to stay
                if (cmt[i] > 0) { b = b.Substring(0, cmt[i]).TrimEnd(); if (b.EndsWith(":")) b = b.Substring(0, b.Length - 1).TrimEnd(); cutComments++; }
                if (map.Count > 0) b = Fix(b, map, dummy, ref chg);
                sb.Append(no[i]).Append(b).Append(nl);
            }
            return sb.ToString();
        }

        // index of the start of a comment in a line body (outside strings and DATA): 0 = the body is a comment; -1 = none.
        // A trailing comment starts at the colon before REM (or at the apostrophe).
        static int CommentStart(string body)
        {
            int i = 0; bool q = false;
            while (i < body.Length && body[i] == ' ') i++;
            int first = i;
            if (i < body.Length && (body[i] == '\'' || RemRx.IsMatch(body, i))) return 0;
            for (; i < body.Length; i++)
            {
                char c = body[i];
                if (c == '"') { q = !q; continue; }
                if (q) continue;
                bool startOk = i == 0 || !char.IsLetter(body[i - 1]);
                if (startOk && DataRx.IsMatch(body, i))
                {
                    while (i < body.Length && !(body[i] == ':' && !q)) { if (body[i] == '"') q = !q; i++; }
                    if (i >= body.Length) return -1;
                    continue;
                }
                if (c == '\'') return i;
                if (c == ':')
                {
                    int j = i + 1; while (j < body.Length && body[j] == ' ') j++;
                    if (j < body.Length && (body[j] == '\'' || RemRx.IsMatch(body, j))) return i;
                }
            }
            return -1;
        }
    }

    // Remove REM: the two options
    public class RemoveRemForm : Form
    {
        readonly CheckBox chkFirst, chkTarget;
        public bool KeepFirst { get { return chkFirst.Checked; } }
        public bool RemoveTargets { get { return chkTarget.Checked; } }
        public RemoveRemForm(string fileName)
        {
            Text = "Remove REM";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false; ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(470, 172);
            try { Icon = Icons.AppIcon(); } catch { }
            Controls.Add(new Label { Text = fileName + ": the REM lines and the comments at the ends of the lines are removed; the result is written to a new file (NAME_NOREM).", Left = 12, Top = 10, Width = 446, Height = 34 });
            chkFirst = new CheckBox { Text = "Keep first REM (e.g. Program title)", Left = 14, Top = 50, Width = 440, Checked = true };
            chkTarget = new CheckBox { Text = "Remove also target REM lines and renumber accordingly program skips (GOTO, GOSUB, THEN ... go to the next line)", Left = 14, Top = 76, Width = 446, Height = 34 };
            var ok = new Button { Text = "Remove REM", Left = 270, Top = 132, Width = 94, DialogResult = DialogResult.OK };
            var cancel = new Button { Text = "Cancel", Left = 372, Top = 132, Width = 86, DialogResult = DialogResult.Cancel };
            Controls.AddRange(new Control[] { chkFirst, chkTarget, ok, cancel });
            AcceptButton = ok; CancelButton = cancel;
        }
    }

    // asks for the step and the first line number of Renumber
    public class RenumberForm : Form
    {
        readonly TextBox txtStep, txtStart;
        public int Step, Start;
        public RenumberForm(string fileName, int lines, int maxLine)
        {
            Text = "Renumber";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false; ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(360, 150);
            try { Icon = Icons.AppIcon(); } catch { }
            Controls.Add(new Label { Text = fileName + ": " + lines + " numbered lines. GOTO, GOSUB, THEN, ELSE, RESTORE, RUN and ON ... GOTO / GOSUB are renumbered too (the file is changed).", Left = 12, Top = 10, Width = 336, Height = 44 });
            Controls.Add(new Label { Text = "Step (distance between the lines):", Left = 12, Top = 62, Width = 200 });
            txtStep = new TextBox { Left = 216, Top = 58, Width = 60, Text = "10" };
            Controls.Add(new Label { Text = "First line number:", Left = 12, Top = 90, Width = 200 });
            txtStart = new TextBox { Left = 216, Top = 86, Width = 60, Text = "10" };
            var ok = new Button { Text = "Renumber", Left = 178, Top = 116, Width = 84 };
            var cancel = new Button { Text = "Cancel", Left = 268, Top = 116, Width = 80, DialogResult = DialogResult.Cancel };
            Controls.AddRange(new Control[] { txtStep, txtStart, ok, cancel });
            AcceptButton = ok; CancelButton = cancel;
            ok.Click += delegate
            {
                int s, f;
                if (!int.TryParse(txtStep.Text.Trim(), out s) || s < 1 || s > maxLine || !int.TryParse(txtStart.Text.Trim(), out f) || f < 1 || f > maxLine)
                { MessageBox.Show(this, "Enter whole numbers from 1 to " + maxLine + ".", "Renumber", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                Step = s; Start = f; DialogResult = DialogResult.OK;
            };
            Shown += delegate { txtStep.Focus(); txtStep.SelectAll(); };
        }
    }

    // Port test: TXD must be connected to RXD (and the flow control lines, if selected); a pattern is sent at 300 to 115200 bps and must come back
    public class PortTestForm : Form
    {
        readonly string portName; readonly int flow;
        readonly RichTextBox box; readonly Button start, close;
        volatile bool abort; System.Threading.Thread th;
        public string Summary;
        static readonly int[] Bauds = { 300, 600, 1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200 };

        public PortTestForm(string portName, int flow)
        {
            this.portName = portName; this.flow = flow;
            Text = "Port test: " + portName;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(560, 440);
            MinimumSize = new Size(480, 360);
            ShowInTaskbar = false;
            try { Icon = Icons.AppIcon(); } catch { }
            string how;
            switch (flow)
            {
                case 2: how = "RTS/CTS: connect also RTS with CTS (D-sub 9: pin 7 with pin 8)."; break;
                case 3: how = "DSR/DTR: connect also DTR with DSR (D-sub 9: pin 4 with pin 6)."; break;
                case 1: how = "XON/XOFF: no more wires (the test data do not contain the XON / XOFF codes 11H and 13H)."; break;
                default: how = "Flow control: none, no more wires."; break;
            }
            var info = new Label
            {
                Left = 10, Top = 8, Width = 540, Height = 88, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Text = "1. Disconnect the cable to the pocket computer from the port " + portName + ".\r\n" +
                       "2. Connect RXD with TXD (D-sub 9: pin 2 with pin 3; D-sub 25: pin 3 with pin 2, or the RX and TX pins of a TTL adapter).\r\n" +
                       "3. " + how + "\r\n" +
                       "4. Press 'Start test': 100 bytes are sent and read back at every speed from 300 to 115200 bps."
            };
            start = new Button { Text = "Start test", Left = 10, Top = 100, Width = 110, Height = 28 };
            close = new Button { Text = "Close", Left = 450, Top = 100, Width = 100, Height = 28, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            box = new RichTextBox { Left = 10, Top = 136, Width = 540, Height = 294, ReadOnly = true, Font = new Font("Consolas", 10F), BackColor = Color.White, DetectUrls = false,
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right };
            Controls.AddRange(new Control[] { info, start, close, box });
            CancelButton = close;
            close.Click += delegate { Close(); };
            start.Click += delegate { StartTest(); };
            FormClosing += delegate { abort = true; try { if (th != null && th.IsAlive) th.Join(3000); } catch { } };
        }

        void Add(string s, Color c, bool bold, float size)
        {
            if (IsDisposed) return;
            Action a = delegate
            {
                box.SelectionStart = box.TextLength; box.SelectionLength = 0;
                box.SelectionFont = new Font(box.Font.FontFamily, size, bold ? FontStyle.Bold : FontStyle.Regular);
                box.SelectionColor = c; box.AppendText(s); box.ScrollToCaret();
            };
            try { if (InvokeRequired) Invoke(a); else a(); } catch { }
        }

        void StartTest()
        {
            start.Enabled = false; box.Clear(); Summary = null; abort = false;
            th = new System.Threading.Thread(Run) { IsBackground = true };
            th.Start();
        }

        static readonly Color Green = Color.FromArgb(20, 130, 40), Red = Color.FromArgb(190, 30, 30);

        void Run()
        {
            int failed = 0, done = 0;
            Add("Port " + portName + ", 8N1, " + FlowName() + "\r\n", Color.Black, false, 10F);
            byte[] pt = Pattern(); var hex = new StringBuilder();
            for (int k = 0; k < 8; k++) hex.Append(pt[k].ToString("X2")).Append(' ');
            Add("Sending string: SerComet port test pattern (" + pt.Length + " bytes: " + hex + "...)\r\n\r\n", Color.Black, false, 10F);
            foreach (int baud in Bauds)
            {
                if (abort) break;
                Add(baud.ToString().PadLeft(7) + " bps: ", Color.Black, false, 10F);     // shown when the test of this speed starts
                string err = One(baud);
                done++;
                if (err == null) Add("OK\r\n", Green, true, 10F);
                else { failed++; Add("FAIL  " + err + "\r\n", Red, true, 10F); }
            }
            if (!abort)
            {
                Summary = failed == 0 ? "PORT TEST OK" : "PORT TEST FAIL (" + failed + " of " + done + " speeds failed)";
                Add("\r\n" + (failed == 0 ? "PORT TEST OK" : "PORT TEST FAIL") + "\r\n", failed == 0 ? Green : Red, true, 14F);
                if (failed > 0) Add("Check the wires (TXD-RXD and the flow control lines). A USB adapter may not support every speed.\r\n", Color.Black, false, 10F);
            }
            else Summary = "test cancelled";
            try { Invoke((Action)delegate { start.Enabled = true; }); } catch { }
        }

        string FlowName() { return flow == 2 ? "RTS/CTS" : flow == 3 ? "DSR/DTR" : flow == 1 ? "XON/XOFF" : "no flow control"; }

        byte[] Pattern()
        {
            var p = new byte[100];
            byte[] head = { 0x55, 0xAA, 0x00, 0xFF, 0x0F, 0xF0, 0x01, 0x80 };
            for (int i = 0; i < p.Length; i++)
            {
                int v = i < head.Length ? head[i] : (i * 73 + 41) & 0xFF;
                if (flow == 1 && (v == 0x11 || v == 0x13)) v++;
                p[i] = (byte)v;
            }
            return p;
        }

        string One(int baud)
        {
            System.IO.Ports.SerialPort p = null;
            try
            {
                p = new System.IO.Ports.SerialPort(portName, baud, System.IO.Ports.Parity.None, 8, System.IO.Ports.StopBits.One);
                p.ReadBufferSize = 4096; p.WriteTimeout = 3000;
                p.Handshake = flow == 2 ? System.IO.Ports.Handshake.RequestToSend : flow == 1 ? System.IO.Ports.Handshake.XOnXOff : System.IO.Ports.Handshake.None;
                p.Open();
                if (flow == 3)
                {
                    p.DtrEnable = true; System.Threading.Thread.Sleep(50);
                    if (!p.DsrHolding) return "DSR is not high: connect DTR with DSR";
                }
                System.Threading.Thread.Sleep(20);
                p.DiscardInBuffer();
                byte[] pat = Pattern();
                try { p.Write(pat, 0, pat.Length); }
                catch (TimeoutException) { return flow == 2 ? "the data are not sent: CTS is low (connect RTS with CTS)" : "the data cannot be sent (time-out)"; }
                var got = new List<byte>();
                int limit = Environment.TickCount + (int)(pat.Length * 10.0 / baud * 1000 * 2) + 800;
                while (got.Count < pat.Length && limit - Environment.TickCount > 0 && !abort)
                {
                    int n = p.BytesToRead;
                    if (n > 0) { var b = new byte[n]; int r = p.Read(b, 0, n); for (int k = 0; k < r; k++) got.Add(b[k]); }
                    else System.Threading.Thread.Sleep(3);
                }
                if (got.Count == 0) return "nothing received (connect TXD with RXD)";
                for (int k = 0; k < Math.Min(got.Count, pat.Length); k++)
                    if (got[k] != pat[k]) return "wrong data at byte " + (k + 1) + " (sent " + pat[k].ToString("X2") + "H, received " + got[k].ToString("X2") + "H)";
                if (got.Count < pat.Length) return "only " + got.Count + " of " + pat.Length + " bytes received";
                return null;
            }
            catch (Exception ex) { return ex.Message; }
            finally { try { if (p != null) { if (p.IsOpen) p.Close(); p.Dispose(); } } catch { } }
        }
    }

    // While a file is being sent the user must not change the port, the target computer, the transmission mode (with its baud rate, delay, EOL and flow)
    // and the check boxes. The controls are NOT disabled (they keep their normal look); they only ignore the user: a click, a key, the mouse wheel
    // or a typed number changes nothing. The program itself can still set them (SetTarget, ApplyCfg).
    public static class InputLock
    {
        static volatile bool active;
        public static event Action Changed;               // the lock was switched on or off
        public static Action Notify;                      // told when the user tries to change something (the Log message is rate limited)
        public static bool Active
        {
            get { return active; }
            set { if (active == value) return; active = value; var h = Changed; if (h != null) h(); }
        }
        public static bool Block() { if (!active) return false; var n = Notify; if (n != null) n(); return true; }
    }

    public class LockRadio : RadioButton
    {
        protected override void OnClick(EventArgs e) { if (InputLock.Block()) return; base.OnClick(e); }
        protected override bool ProcessMnemonic(char charCode) { return !InputLock.Active && base.ProcessMnemonic(charCode); }
    }

    public class LockCheck : CheckBox
    {
        protected override void OnClick(EventArgs e) { if (InputLock.Block()) return; base.OnClick(e); }
        protected override bool ProcessMnemonic(char charCode) { return !InputLock.Active && base.ProcessMnemonic(charCode); }
    }

    public class LockCombo : ComboBox
    {
        const int WM_KEYDOWN = 0x100, WM_CHAR = 0x102, WM_SYSKEYDOWN = 0x104, WM_LBUTTONDOWN = 0x201, WM_LBUTTONDBLCLK = 0x203, WM_MOUSEWHEEL = 0x20A;
        protected override void WndProc(ref Message m)
        {
            if (InputLock.Active)
                switch (m.Msg)
                {
                    case WM_LBUTTONDOWN: case WM_LBUTTONDBLCLK: case WM_KEYDOWN: case WM_SYSKEYDOWN: case WM_CHAR: case WM_MOUSEWHEEL:
                        if (m.Msg == WM_LBUTTONDOWN || m.Msg == WM_KEYDOWN) InputLock.Block();
                        return;
                }
            base.WndProc(ref m);
        }
    }

    public class LockNumeric : NumericUpDown
    {
        // the box stays white (like an enabled one); while the lock is on its text is read-only and the arrows / wheel do nothing
        public LockNumeric()
        {
            BackColor = SystemColors.Window;
            InputLock.Changed += delegate { try { ReadOnly = InputLock.Active; } catch { } };
        }
        public override void UpButton() { if (InputLock.Block()) return; base.UpButton(); }
        public override void DownButton() { if (InputLock.Block()) return; base.DownButton(); }
        protected override void OnMouseWheel(MouseEventArgs e) { if (InputLock.Active) { var h = e as HandledMouseEventArgs; if (h != null) h.Handled = true; return; } base.OnMouseWheel(e); }
    }

    // BASIC tokens (intermediate codes) of the Sharp PC-1350, PC-1360 and PC-E500(S): the table word = code is a fact about the ROM of these computers,
    // the tokenizer, the detokenizer and the tape routines are written from the formats (checked against recordings made by the computers).
    //   PC-1350: one byte tokens (ID 70H on the tape); PC-1360: two byte tokens FExx and line numbers after GOTO ... as 1F hi lo (ID 72H);
    //   PC-E500: two byte tokens FExx, numbers as 1D + 7 BCD bytes, line numbers 1F hi lo, 1E = a jump length (REM, IF).
    public static class SharpTok
    {
        const string S1350 = "ABS=99;ACS=9E;AHC=8E;AHS=8D;AHT=8F;AND=A1;AREAD=E1;ASC=A4;ASN=9D;ATN=9F;BASIC=EC;BEEP=C4;CALL=CC;CHAIN=E5;CHR$=A8;CLEAR=C9;CLOAD=B7;CLOSE=BC;CLS=CE;CONSOLE=BF;CONT=B2;COS=96;CSAVE=B6;CUR=89;CURSOR=CF;DATA=DC;DECI=84;DEG=9B;DEGREE=C1;DIM=CB;DMS=9C;END=D8;EQU#=B9;EXP=93;FACT=90;FOR=D5;GCURSOR=E6;GOSUB=E0;GOTO=C6;GPRINT=E7;GRAD=C3;HCS=8B;HEX=85;HSN=8A;HTN=8C;IF=D4;INKEY$=AD;INPUT=DF;INT=98;LEFT$=AB;LEN=A6;LET=D6;LINE=E8;LIST=B4;LLIST=B5;LN=91;LOAD=BE;LOG=92;LPRINT=E2;MDF=80;MEM=AF;MEM#=BA;MERGE=B8;MID$=AA;NEW=B1;NEXT=D9;NOT=A3;ON=D3;OPEN=BB;OPEN$=EE;OR=A2;PASS=B3;PAUSE=DD;PEEK=A7;PI=AE;POINT=E9;POKE=CD;POL=82;PRESET=EB;PRINT=DE;PSET=EA;RADIAN=C2;RANDOM=C0;RCP=87;READ=DB;REC=81;REM=D7;RESTORE=E4;RETURN=E3;RIGHT$=AC;RND=A0;ROT=83;RUN=B0;SAVE=BD;SGN=9A;SIN=95;SQR=94;SQU=88;STEP=D1;STOP=DA;STR$=A9;TAN=97;TEN=86;TEXT=ED;THEN=D2;TO=D0;TROFF=C8;TRON=C7;USING=CA;VAL=A5;WAIT=C5";
        const string S1360 = "ABS=FE99;ACS=FE9E;AHC=FE8E;AHS=FE8D;AHT=FE8F;AKCNV$=FEE0;AND=FEA1;APPEND=FE72;AREAD=FE63;ARUN=FE74;AS=FE73;ASC=FED0;ASN=FE9D;ATN=FE9F;AUTO=FE1A;AUTOGOTO=FE75;BASIC=FE36;BEEP=FE29;CALL=FE31;CHAIN=FE67;CHR$=FEF0;CIRCLE=FE6F;CLEAR=FE2E;CLOAD=FE16;CLOSE=FE22;CLS=FE50;COLOR=FE44;CONSOLE=FE24;CONT=FE12;CONVERT=FE1E;COPY=FE3D;COS=FE96;CROTATE=FE6E;CSAVE=FE20;CSIZE=FE43;CUB=FEBF;CUR=FE89;CURSOR=FE51;DATA=FE5E;DECI=FE84;DEFDBL=FE46;DEFSNG=FE47;DEG=FE9B;DEGREE=FE26;DELETE=FE1B;DIM=FE30;DMS=FE9C;DSKF=FEB1;END=FE5A;EOF=FEB0;ERASE=FE3A;ERL=FEC1;ERN=FEC0;ERROR=FE78;EXP=FE93;FACT=FE90;FIELD=FE48;FILES=FE1C;FOR=FE57;GCURSOR=FE68;GET=FE4A;GLCURSOR=FE6C;GOSUB=FE62;GOTO=FE2B;GPRINT=FE33;GRAD=FE28;GRAPH=FE41;HCS=FE8B;HEX=FE85;HEX$=FEF2;HSN=FE8A;HTN=FE8C;IF=FE56;INIT=FE1D;INKEY$=FEE9;INPUT=FE61;INT=FE98;JIS$=FEE2;KACNV$=FEE1;KILL=FE3C;KLEFT$=FEEE;KLEN=FED3;KMID$=FEED;KRIGHT$=FEEF;LEFT$=FEEB;LEN=FED2;LET=FE58;LF=FE42;LFILES=FE3B;LINE=FE69;LIST=FE14;LLINE=FE6A;LLIST=FE15;LN=FE91;LOAD=FE18;LOC=FEB3;LOCATE=FE51;LOF=FEB2;LOG=FE92;LPRINT=FE64;LSET=FE4B;LTEXT=FE40;MDF=FE80;MEM=FEAF;MERGE=FE17;MID$=FEEA;NAME=FE3E;NCR=FEB6;NEW=FE11;NEXT=FE5B;NOT=FEA3;NPR=FEB7;ON=FE55;OPEN=FE21;OPEN$=FEE8;OR=FEA2;OUTPUT=FE71;PAINT=FE70;PASS=FE13;PAUSE=FE5F;PEEK=FEA4;PI=FEAE;POINT=FEAD;POKE=FE32;POL=FE82;PRESET=FE35;PRINT=FE60;PSET=FE34;PUT=FE49;RADIAN=FE27;RANDOM=FE25;RANDOMIZE=FE25;RCP=FE87;READ=FE5D;REC=FE81;REM=FE59;RENUM=FE19;RESTORE=FE66;RETURN=FE65;RIGHT$=FEEC;RLINE=FE6B;RND=FEA0;ROT=FE83;RSET=FE4C;RUN=FE10;SAVE=FE23;SET=FE3F;SGN=FE9A;SIN=FE95;SORGN=FE6D;SQR=FE94;SQU=FE88;STEP=FE53;STOP=FE5C;STR$=FEF1;TAN=FE97;TEN=FE86;TEXT=FE37;THEN=FE54;TO=FE52;TROFF=FE2D;TRON=FE2C;USING=FE2F;VAL=FED1;WAIT=FE2A;WIDTH=FE38;XOR=FEA5";
        const string SE500 = "ABS=FE99;ACS=FE9E;AER=FEBE;AHC=FE8E;AHS=FE8D;AHT=FE8F;AKCNV$=FEE0;AND=FEA1;APPEND=FE72;ARUN=FE74;AS=FE73;ASC=FED0;ASN=FE9D;ATN=FE9F;AUTO=FE1A;AUTOGOTO=FE75;BASIC=FE36;BDATA$=FE0C;BEEP=FE29;BTEXT$=FE0B;CALL=FE31;CASE=FE7D;CHAIN=FE67;CHR$=FEF0;CIRCLE=FE6F;CLEAR=FE2E;CLOAD=FE16;CLOSE=FE22;CLS=FE50;COLOR=FE44;CONSOLE=FE24;CONT=FE12;CONVERT=FE1E;COPY=FE3D;COS=FE96;CROTATE=FE6E;CSAVE=FE20;CSIZE=FE43;CUB=FEBF;CUR=FE89;CURSOR=FE51;DATA=FE5E;DECI=FE84;DEFAULT=FE7E;DEFDBL=FE46;DEFSNG=FE47;DEG=FE9B;DEGREE=FE26;DELETE=FE1B;DIM=FE30;DMS=FE9C;DSKF=FEB1;ELSE=FE76;END=FE5A;ENDIF=FE4D;ENDSWITCH=FE7F;EOF=FEB0;ERASE=FE3A;ERL=FEC1;ERN=FEC0;ERROR=FE78;EVAL=FEA7;EXP=FE93;FACT=FE90;FIELD=FE48;FILES=FE1C;FOR=FE57;FRE=FEAF;GCURSOR=FE68;GET=FE4A;GLCURSOR=FE6C;GOSUB=FE62;GOTO=FE2B;GPRINT=FE33;GRAD=FE28;GRAPH=FE41;HCS=FE8B;HEX=FE85;HEX$=FEF2;HSN=FE8A;HTN=FE8C;IF=FE56;INIT=FE1D;INKEY$=FEE9;INPUT=FE61;INT=FE98;JIS$=FEE2;KACNV$=FEE1;KEY=FE79;KILL=FE3C;KLEFT$=FEEE;KLEN=FED3;KMID$=FEED;KRIGHT$=FEEF;LEFT$=FEEB;LEN=FED2;LET=FE58;LF=FE42;LFILES=FE3B;LINE=FE69;LIST=FE14;LLINE=FE6A;LLIST=FE15;LN=FE91;LOAD=FE18;LOC=FEB3;LOCATE=FE51;LOF=FEB2;LOG=FE92;LPRINT=FE64;LSET=FE4B;LTEXT=FE40;MDF=FE80;MEM$=FE0D;MERGE=FE17;MID$=FEEA;NAME=FE3E;NCR=FEB6;NEW=FE11;NEXT=FE5B;NOT=FEA3;NPR=FEB7;ON=FE55;OPEN=FE21;OPEN$=FEE8;OR=FEA2;OUTPUT=FE71;PAINT=FE70;PASS=FE13;PAUSE=FE5F;PEEK=FEA4;PI=FEAE;POINT=FEAD;POKE=FE32;POL=FE82;PRESET=FE35;PRINT=FE60;PSET=FE34;PUT=FE49;RADIAN=FE27;RANDOM=FE25;RANDOMIZE=FE25;RCP=FE87;READ=FE5D;REC=FE81;REM=FE59;RENUM=FE19;REPEAT=FE4E;RESERVED=FE00;RESTORE=FE66;RESUME=FE77;RETURN=FE65;RIGHT$=FEEC;RLINE=FE6B;RND=FEA0;ROT=FE83;RSET=FE4C;RUN=FE10;SAVE=FE23;SET=FE3F;SGN=FE9A;SIN=FE95;SORGN=FE6D;SQR=FE94;SQU=FE88;STEP=FE53;STOP=FE5C;STR$=FEF1;SWITCH=FE7C;TAN=FE97;TEN=FE86;TEXT=FE37;THEN=FE54;TO=FE52;TROFF=FE2D;TRON=FE2C;UNTIL=FE4F;USING=FE2F;VAL=FED1;WAIT=FE2A;WEND=FE7B;WHILE=FE7A;WIDTH=FE38;XOR=FEA5";

        sealed class Table
        {
            public readonly Dictionary<string, int> Code = new Dictionary<string, int>();
            public readonly Dictionary<int, string> Name = new Dictionary<int, string>();
            public readonly List<string> Words = new List<string>();
            public Table(string spec, params string[] prefer)
            {
                var all = new List<string>(spec.Split(';'));
                all.Sort(StringComparer.Ordinal);
                foreach (string item in all)
                {
                    int k = item.LastIndexOf('=');
                    string w = item.Substring(0, k);
                    int v = Convert.ToInt32(item.Substring(k + 1), 16);
                    Code[w] = v;
                    if (!Name.ContainsKey(v)) Name[v] = w;          // for a code with several words the first one (alphabetically) is listed
                }
                foreach (string pw in prefer) { int pv; if (Code.TryGetValue(pw, out pv)) Name[pv] = pw; }
                Words.AddRange(Code.Keys);
                Words.Sort((a, b) => b.Length != a.Length ? b.Length - a.Length : string.CompareOrdinal(a, b));
            }
        }

        static Table t1350, t1360, tE500;
        static Table Get(int pc)
        {
            if (pc == 1350) return t1350 ?? (t1350 = new Table(S1350));
            if (pc == 1360) return t1360 ?? (t1360 = new Table(S1360));
            return tE500 ?? (tE500 = new Table(SE500, "LOCATE"));
        }

        // codes after which a line number may follow (PC-1360 / PC-E500): GOSUB GOTO LIST RESTORE RESUME RUN THEN ARUN AUTO AUTOGOTO ELSE
        static readonly int[] Lbl = { 0xFE62, 0xFE2B, 0xFE14, 0xFE66, 0xFE77, 0xFE10, 0xFE54, 0xFE74, 0xFE1A, 0xFE75, 0xFE76 };
        // ... and lists of numbers: DELETE LLIST RENUM CHAIN
        static readonly int[] LbLst = { 0xFE1B, 0xFE15, 0xFE19, 0xFE67 };
        const int OnCode = 0xFE55;

        static void AddCode(List<byte> o, int v) { if (v > 255) o.Add((byte)(v >> 8)); o.Add((byte)v); }

        static List<byte> Line(string body, Table t, bool two)
        {
            var o = new List<byte>();
            int i = 0, n = body.Length;
            bool on = false;
            while (i < n)
            {
                char c = body[i];
                if (c == ' ') { i++; continue; }
                if (c == '"')
                {
                    int j = body.IndexOf('"', i + 1);
                    if (j < 0) j = n - 1;
                    for (int k = i; k <= j; k++) o.Add((byte)body[k]);
                    i = j + 1;
                    continue;
                }
                string u = body.Substring(i).ToUpperInvariant();
                string hit = null;
                foreach (string w in t.Words) if (u.StartsWith(w, StringComparison.Ordinal)) { hit = w; break; }
                if (hit != null)
                {
                    int v = t.Code[hit];
                    AddCode(o, v);
                    i += hit.Length;
                    if (two && v == OnCode) on = true;
                    if (two && (Array.IndexOf(Lbl, v) >= 0 || Array.IndexOf(LbLst, v) >= 0))
                    {
                        bool lst = Array.IndexOf(LbLst, v) >= 0 || on;
                        while (true)
                        {
                            while (i < n && body[i] == ' ') i++;
                            int k = i;
                            while (k < n && char.IsDigit(body[k])) k++;
                            if (k == i) break;
                            int num;
                            if (!int.TryParse(body.Substring(i, k - i), out num) || num > 65279) break;
                            o.Add(0x1F); o.Add((byte)(num >> 8)); o.Add((byte)num);
                            i = k;
                            int m = i;
                            while (m < n && body[m] == ' ') m++;
                            if (lst && m < n && body[m] == ',') { o.Add((byte)','); i = m + 1; continue; }
                            break;
                        }
                    }
                    if (hit == "REM")
                    {
                        string rest = body.Substring(i).TrimStart(' ');
                        foreach (char ch in rest) o.Add((byte)ch);
                        i = n;
                    }
                    else if (hit == "DATA")
                    {
                        string rest = body.Substring(i).TrimStart(' ');
                        bool q = false; int k = 0;
                        while (k < rest.Length)
                        {
                            if (rest[k] == '"') q = !q;
                            if (rest[k] == ':' && !q) break;
                            k++;
                        }
                        for (int m = 0; m < k; m++) o.Add((byte)rest[m]);
                        i = n - (rest.Length - k);
                    }
                    continue;
                }
                o.Add((byte)(c >= 'a' && c <= 'z' ? c - 32 : c));
                i++;
            }
            return o;
        }

        // pc = 1350 or 1360: the BASIC text -> the program image (lines [number 2 B][length][tokens ... 0D]) without the end marker
        public static byte[] Tokenize(string text, int pc, out string problem)
        {
            problem = null;
            Table t = Get(pc);
            bool two = pc != 1350;
            var lines = new SortedDictionary<int, byte[]>();
            foreach (string raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                string s = raw.Trim().TrimEnd('\u001A');
                if (s.Length == 0) continue;
                int i = 0;
                while (i < s.Length && char.IsDigit(s[i])) i++;
                int no;
                if (i == 0 || !int.TryParse(s.Substring(0, i), out no)) { problem = "no line number: " + (s.Length > 30 ? s.Substring(0, 30) : s); return null; }
                if (no < 1 || no > 65279) { problem = "line number out of range: " + no; return null; }
                List<byte> b = Line(s.Substring(i).TrimStart(), t, two);
                b.Add(0x0D);
                if (b.Count > 255) { problem = "line " + no + " is too long (" + b.Count + " bytes)"; return null; }
                var l = new List<byte> { (byte)(no >> 8), (byte)no, (byte)b.Count };
                l.AddRange(b);
                lines[no] = l.ToArray();
            }
            var o = new List<byte>();
            foreach (var kv in lines) o.AddRange(kv.Value);
            return o.ToArray();
        }

        // pc = 1350 or 1360: the program image -> BASIC text
        public static string Detokenize(byte[] img, int pc, out string problem)
        {
            problem = null;
            Table t = Get(pc);
            bool two = pc != 1350;
            var sb = new StringBuilder();
            int p = 0;
            while (p + 3 <= img.Length && img[p] != 0xFF)
            {
                int no = img[p] * 256 + img[p + 1], ln = img[p + 2];
                if (p + 3 + ln > img.Length) { problem = "the last line is cut"; break; }
                int end = p + 3 + ln;
                if (end > p + 3 && img[end - 1] == 0x0D) end--;
                var s = new StringBuilder();
                bool q = false;
                int i = p + 3;
                while (i < end)
                {
                    int b = img[i];
                    if (q) { s.Append((char)b); i++; if (b == 34) q = false; continue; }
                    if (b == 34) { q = true; s.Append('"'); i++; continue; }
                    if (two && b == 0x1F && i + 2 < end) { s.Append(img[i + 1] * 256 + img[i + 2]); i += 3; continue; }
                    int v = -1;
                    if (two) { if (i + 1 < end && b >= 0xE0) { int c2 = (b << 8) | img[i + 1]; if (t.Name.ContainsKey(c2)) { v = c2; i += 2; } } }
                    else if (t.Name.ContainsKey(b)) { v = b; i++; }
                    if (v >= 0)
                    {
                        string w = t.Name[v];
                        if (s.Length > 0 && char.IsLetterOrDigit(s[s.Length - 1])) s.Append(' ');
                        s.Append(w).Append(' ');
                        if (w == "REM" || w == "DATA") { while (i < end) s.Append((char)img[i++]); }
                        continue;
                    }
                    s.Append((char)b); i++;
                }
                sb.Append(no).Append(' ').Append(s.ToString().TrimEnd()).Append("\r\n");
                p += 3 + ln;
            }
            return sb.ToString();
        }

        // PC-E500: the number 1D + exponent (2 BCD bytes) + mantissa (5 BCD bytes) -> text; null if the bytes are no BCD
        static string E500Number(byte[] b, int i)
        {
            var d = new StringBuilder();
            for (int k = 0; k < 7; k++) { int hi = b[i + k] >> 4, lo = b[i + k] & 15; if (hi > 9 || lo > 9) return null; d.Append(hi).Append(lo); }
            string all = d.ToString();
            int ex = int.Parse(all.Substring(0, 4));
            if (ex >= 5000) ex -= 10000;
            string ds = all.Substring(4).TrimEnd('0');
            if (ds.Length == 0) return "0";
            if (ex >= 0)
            {
                int ip = ex + 1;
                if (ds.Length <= ip) return ds.PadRight(ip, '0');
                return ds.Substring(0, ip) + "." + ds.Substring(ip);
            }
            return "." + new string('0', -ex - 1) + ds;
        }

        // PC-E500(S): the program image (CSAVE) -> BASIC text; the lines are "number: statements"
        public static string DetokenizeE500(byte[] img, out string problem)
        {
            problem = null;
            Table t = Get(500);
            var sb = new StringBuilder();
            int p = 0;
            while (p + 3 <= img.Length && img[p] != 0xFF)
            {
                int no = img[p] * 256 + img[p + 1], ln = img[p + 2];
                if (p + 3 + ln > img.Length) { problem = "the last line is cut"; break; }
                int end = p + 3 + ln;
                if (end > p + 3 && img[end - 1] == 0x0D) end--;
                var s = new StringBuilder();
                bool q = false;
                int i = p + 3;
                while (i < end)
                {
                    int b = img[i];
                    if (q) { s.Append((char)b); i++; if (b == 34) q = false; continue; }
                    if (b == 34) { q = true; s.Append('"'); i++; continue; }
                    if (b == 0xFE && i + 1 < end)
                    {
                        int v = 0xFE00 | img[i + 1];
                        string w;
                        if (t.Name.TryGetValue(v, out w))
                        {
                            i += 2;
                            if (s.Length > 0 && char.IsLetterOrDigit(s[s.Length - 1])) s.Append(' ');
                            s.Append(w);
                            if (w == "REM")
                            {
                                s.Append(' ');
                                while (i < end && img[i] != 0x1E) s.Append((char)img[i++]);          // spaces before the jump length
                                if (i + 1 < end) i += 2;                                              // 1E, the length of the comment
                                while (i < end) s.Append((char)img[i++]);
                            }
                            else s.Append(' ');
                            continue;
                        }
                    }
                    if (b == 0x1F && i + 2 < end) { s.Append(img[i + 1] * 256 + img[i + 2]); i += 3; continue; }
                    if (b == 0x1E && i + 1 < end) { i += 2; continue; }
                    if (b == 0x1D && i + 7 < end)
                    {
                        string num = E500Number(img, i + 1);
                        if (num != null) { s.Append(num); i += 8; continue; }
                    }
                    s.Append((char)b); i++;
                }
                sb.Append(no).Append(": ").Append(s.ToString().Trim()).Append("\r\n");
                p += 3 + ln;
            }
            return sb.ToString();
        }
    }

    // Tape recordings (WAV) of BASIC programs, ported from the converters of the repositories
    // Sharp_PC-1600_Software_UART / Sharp_PC-1600_I2C_interface (tools/pc1600_bas2wav.py, pc1600_cas_bas2wav.py) and Sharp_PC-1500_I2C_interface:
    //   MODE 1: the PC-1500 / CE-150 tape format (PC-1500 and the PC-1600 in MODE 1), the program is the tokenized image,
    //   MODE 0: the PC-1600 format for the CE-1600P (LOAD "CAS:NAME"), the program is an ASCII BASIC file.
    // 44100 Hz, 8 bit, mono. Decoding reads back what the encoders write (and the recordings of the same tools).
    public static class TapeWav
    {
        public struct WavInfo { public int Channels, Rate, Bits; public long DataBytes; public double Seconds; }

        // ---- the WAV container
        public static bool Parse(byte[] f, out WavInfo info, out int dataOffset, out int dataLength) { return Parse(f, f == null ? 0 : f.Length, out info, out dataOffset, out dataLength); }

        // fileLength = the real length of the file when only its first bytes are in f (the tip reads only the header)
        public static bool Parse(byte[] f, long fileLength, out WavInfo info, out int dataOffset, out int dataLength)
        {
            info = new WavInfo(); dataOffset = 0; dataLength = 0;
            if (f == null || f.Length < 44 || f[0] != 'R' || f[1] != 'I' || f[2] != 'F' || f[3] != 'F' || f[8] != 'W' || f[9] != 'A' || f[10] != 'V' || f[11] != 'E') return false;
            int p = 12; bool fmt = false;
            while (p + 8 <= f.Length)
            {
                string id = Encoding.ASCII.GetString(f, p, 4);
                int len = BitConverter.ToInt32(f, p + 4);
                if (id == "fmt " && p + 24 <= f.Length)
                {
                    int tag = BitConverter.ToUInt16(f, p + 8);
                    info.Channels = BitConverter.ToUInt16(f, p + 10);
                    info.Rate = BitConverter.ToInt32(f, p + 12);
                    info.Bits = BitConverter.ToUInt16(f, p + 22);
                    fmt = (tag == 1 || tag == 0xFFFE) && info.Channels > 0 && info.Rate > 0 && info.Bits > 0;
                }
                else if (id == "data")
                {
                    dataOffset = p + 8;
                    dataLength = (int)(len < 0 ? fileLength - dataOffset : Math.Min((long)len, fileLength - dataOffset));
                    break;
                }
                if (len < 0) return false;
                p += 8 + len + (len & 1);
            }
            if (!fmt || dataOffset == 0) return false;
            info.DataBytes = dataLength;
            int frame = info.Channels * ((info.Bits + 7) / 8);
            info.Seconds = frame > 0 ? dataLength / (double)frame / info.Rate : 0;
            return true;
        }

        public static string Describe(WavInfo i)
        {
            int m = (int)(i.Seconds / 60);
            return "Duration: " + m + ":" + (i.Seconds - m * 60).ToString("00.0", System.Globalization.CultureInfo.InvariantCulture) + " (" + i.Seconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " s)" +
                   "\r\nSampling: " + i.Rate + " Hz\r\nResolution: " + i.Bits + " bit, " + (i.Channels == 1 ? "mono" : i.Channels == 2 ? "stereo" : i.Channels + " channels");
        }

        // the first channel as 8 bit unsigned samples
        static byte[] Samples(byte[] f, out int rate)
        {
            WavInfo info; int off, len;
            if (!Parse(f, out info, out off, out len)) throw new Exception("not a PCM WAV file");
            rate = info.Rate;
            int step = info.Channels * (info.Bits / 8);
            if (info.Bits != 8 && info.Bits != 16) throw new Exception("only 8 and 16 bit recordings are supported");
            int n = len / step;
            var d = new byte[n];
            for (int i = 0; i < n; i++)
                d[i] = info.Bits == 8 ? f[off + i * step] : (byte)((BitConverter.ToInt16(f, off + i * step) >> 8) + 128);
            return d;
        }

        static byte[] WavFile(byte[] data, int rate)
        {
            var o = new MemoryStream();
            var w = new BinaryWriter(o);
            w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + data.Length); w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate); w.Write((short)1); w.Write((short)8);
            w.Write(Encoding.ASCII.GetBytes("data")); w.Write(data.Length); w.Write(data);
            w.Flush();
            return o.ToArray();
        }

        static long Round(double v) { return (long)Math.Round(v, MidpointRounding.ToEven); }

        // ================================================================ MODE 1 (CE-150 format)
        const double Fs = 44100.0;

        static byte[] Header1(string name, byte[] prog)
        {
            byte[] nm = Encoding.GetEncoding(28591).GetBytes(name.ToUpperInvariant());
            var h = new List<byte> { 0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x01 };
            for (int i = 0; i < 26; i++) h.Add(i < nm.Length && i < 7 ? nm[i] : (byte)0);
            int n = prog.Length - 1;
            h.AddRange(new byte[] { 0xC5, (byte)(n >> 8), (byte)n, 0, 0 });
            int s = 0; foreach (byte b in h) s += b;
            h.Add((byte)(s >> 8)); h.Add((byte)s);
            return h.ToArray();
        }

        static byte[] Chunks1(byte[] prog)
        {
            var o = new List<byte>();
            for (int p = 0; p < prog.Length; p += 80)
            {
                int n = Math.Min(80, prog.Length - p), s = 0;
                for (int i = 0; i < n; i++) { o.Add(prog[p + i]); s += prog[p + i]; }
                o.Add((byte)(s >> 8)); o.Add((byte)s);
            }
            return o.ToArray();
        }

        static List<int> Nibbles(byte[] bs) { var r = new List<int>(); foreach (byte b in bs) { r.Add(b & 15); r.Add(b >> 4); } return r; }

        // the program = the tokenized image ending with FFh
        public static byte[] Encode1(string name, byte[] prog)
        {
            var bits = new List<int>();
            Action<IEnumerable<int>> frames = nibs => { foreach (int x in nibs) { bits.Add(0); for (int k = 0; k < 4; k++) bits.Add((x >> k) & 1); for (int k = 0; k < 6; k++) bits.Add(1); } };
            var first = new List<int> { 0xA }; first.AddRange(Nibbles(Header1(name, prog)));
            frames(first);
            for (int i = 0; i < 76; i++) bits.Add(1);
            frames(Nibbles(Chunks1(prog)));
            for (int i = 0; i < 73; i++) bits.Add(1);
            frames(new[] { 5, 5 });
            for (int i = 0; i < 71; i++) bits.Add(1);
            var edges = new List<double>();
            double t = 0;
            Action<double, int> tone = (f, cycles) => { double half = Fs / f / 2; for (int c = 0; c < cycles; c++) { t += half; edges.Add(t); t += half; edges.Add(t); } };
            tone(2500, (int)Round(8.0 * 2500));
            foreach (int b in bits) { if (b != 0) tone(2500, 8); else tone(1250, 4); }
            int total = (int)t + 1;
            var buf = new byte[total];
            for (int i = 0; i < total; i++) buf[i] = 128;
            const byte lo = 30, hi = 226;
            double pos = 0; bool high = true;
            foreach (double e in edges)
            {
                long a = Round(pos), z = Round(e);
                byte v = high ? hi : lo;
                for (long i = a; i < z && i < total; i++) buf[i] = v;
                pos = e; high = !high;
            }
            return WavFile(buf, (int)Fs);
        }

        // returns the tape name and the program bytes (the tokenized image, FFh at the end)
        public static byte[] Decode1(byte[] wav, out string name)
        {
            int rate;
            byte[] d = Samples(wav, out rate);
            double scale = Fs / rate;
            var cr = new List<int>();
            bool prev = d.Length > 0 && d[0] >= 128;
            for (int i = 0; i < d.Length; i++) { bool s = d[i] >= 128; if (s && !prev) cr.Add(i); prev = s; }
            var runs = new List<KeyValuePair<char, double>>();
            for (int i = 1; i < cr.Count; i++)
            {
                double p = (cr[i] - cr[i - 1]) * scale;
                char c = p >= 30 ? 'S' : (p < 22 ? 'F' : 'T');
                if (c == 'T') continue;
                if (runs.Count > 0 && runs[runs.Count - 1].Key == c) runs[runs.Count - 1] = new KeyValuePair<char, double>(c, runs[runs.Count - 1].Value + p);
                else runs.Add(new KeyValuePair<char, double>(c, p));
            }
            var r = new List<int>();
            foreach (var run in runs)
            {
                long n = Round((run.Value + (run.Key == 'S' ? 35 : 17.6)) / 140.0);
                for (long k = 0; k < n; k++) r.Add(run.Key == 'S' ? 0 : 1);
            }
            int i2 = 0;
            while (i2 < r.Count && r[i2] == 1) i2++;
            var segs = new List<List<int>>(); var cur = new List<int>();
            while (i2 + 11 <= r.Count)
            {
                bool ok = r[i2] == 0;
                for (int k = 5; ok && k < 11; k++) if (r[i2 + k] != 1) ok = false;
                if (ok)
                {
                    int v = 0; for (int k = 0; k < 4; k++) v |= r[i2 + 1 + k] << k;
                    cur.Add(v); i2 += 11;
                }
                else { if (cur.Count > 0) { segs.Add(cur); cur = new List<int>(); } i2++; }
            }
            if (cur.Count > 0) segs.Add(cur);
            if (segs.Count < 2 || segs[0].Count < 3) throw new Exception("no PC-1500 / CE-150 tape recording found");
            var hdr = new List<byte>();
            for (int k = 1; k < segs[0].Count - 1; k += 2) hdr.Add((byte)((segs[0][k + 1] << 4) | segs[0][k]));
            var raw = new List<byte>();
            for (int k = 0; k + 1 < segs[1].Count; k += 2) raw.Add((byte)((segs[1][k + 1] << 4) | segs[1][k]));
            var prog = new List<byte>();
            int q = 0;
            while (q < raw.Count)
            {
                int end = Math.Min(q + 80, raw.Count - 2);
                if (end <= q) break;
                int s = 0; for (int k = q; k < end; k++) s += raw[k];
                if ((s & 0xFFFF) != raw[end] * 256 + raw[end + 1]) throw new Exception("checksum error in the data at byte " + q);
                for (int k = q; k < end; k++) prog.Add(raw[k]);
                q = end + 2;
            }
            name = hdr.Count >= 35 ? Encoding.GetEncoding(28591).GetString(hdr.ToArray(), 9, 26).TrimEnd('\0', ' ') : "";
            if (prog.Count == 0) throw new Exception("no program in the recording");
            return prog.ToArray();
        }

        // ================================================================ MODE 0 (CE-1600P, ASCII BASIC file)
        const double SHORT_HI = 163.0, SHORT_LO = 164.0, LONG_HI = 409.0, LONG_LO = 409.0;
        const int HDR_S1 = 10000, HDR_L = 40, HDR_S2 = 40, DAT_S1 = 11000, DAT_L = 20, DAT_S2 = 20, BLOCK = 256;
        const byte EOFC = 0x1A;

        sealed class Synth0
        {
            readonly int rate;
            public readonly List<byte> Buf = new List<byte>();
            double pos;
            const byte Hi = 226, Lo = 30;
            readonly double sh = SHORT_HI, sl = SHORT_LO, lh = LONG_HI, ll = LONG_LO;
            public Synth0(int r) { rate = r; }
            public Synth0(int r, double sHi, double sLo, double lHi, double lLo) { rate = r; sh = sHi; sl = sLo; lh = lHi; ll = lLo; }
            void Seg(double us, byte level)
            {
                double end = pos + us * rate / 1e6;
                long a = Round(pos), b = Round(end);
                for (long i = a; i < b; i++) Buf.Add(level);
                pos = end;
            }
            public void Silence(double s)
            {
                double end = pos + s * rate;
                long a = Round(pos), b = Round(end);
                for (long i = a; i < b; i++) Buf.Add(128);
                pos = end;
            }
            public void Bit(int v) { if (v != 0) { Seg(lh, Hi); Seg(ll, Lo); } else { Seg(sh, Hi); Seg(sl, Lo); } }
            public void Bits(int v, int n) { for (int i = 0; i < n; i++) Bit(v); }
            public void Byte(int b) { Bit(1); for (int i = 7; i >= 0; i--) Bit((b >> i) & 1); }
            public void Block(byte[] data, int s1, int l1, int s2)
            {
                Bits(0, s1); Bits(1, l1); Bits(0, s2); Bit(1);
                int total = 0;
                foreach (byte b in data) { Byte(b); for (int k = 0; k < 8; k++) total += (b >> k) & 1; }
                total &= 0xFFFF;
                Byte(total >> 8); Byte(total & 0xFF); Bit(1);
            }
        }

        // text = the BASIC listing (every line starts with a line number, at most 80 characters)
        public static byte[] Encode0(string text, string name) { return EncodeAscii(text, name, false); }

        // e500 = the PC-E500(S) (SAVE "CAS:" ASCII): the same file layout as the PC-1600 MODE 0, the pulses of a recording of the PC-E500
        // (333 / 833 us), 6405 and 7536 leading pulses; the layout was checked with a CSAVE recording of the PC-E500, an ASCII recording was not available
        public static byte[] EncodeAscii(string text, string name, bool e500)
        {
            var lines = new List<string>();
            foreach (string l in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')) lines.Add(l.TrimEnd());
            while (lines.Count > 0 && lines[lines.Count - 1].Length == 0) lines.RemoveAt(lines.Count - 1);
            foreach (string l in lines)
            {
                if (l.Length == 0 || !char.IsDigit(l[0])) throw new Exception("every line must start with a line number: " + (l.Length > 30 ? l.Substring(0, 30) : l));
                if (l.Length > 80) throw new Exception("a line is longer than 80 characters: " + l.Substring(0, 30));
            }
            var data = new List<byte>(Encoding.GetEncoding(28591).GetBytes(string.Join("\r\n", lines.ToArray()) + "\r\n"));
            data.Add(EOFC);
            while (data.Count % BLOCK != 0) data.Add(0);
            var hdr = new byte[48];
            hdr[0] = 0x04;
            byte[] nm = Encoding.ASCII.GetBytes(name.ToUpperInvariant());
            for (int i = 0; i < 13; i++) hdr[1 + i] = i < nm.Length && i < 8 ? nm[i] : (byte)' ';
            hdr[14] = (byte)'B'; hdr[15] = (byte)'A'; hdr[16] = (byte)'S';
            hdr[17] = 0x0D; hdr[24] = 0x04; hdr[25] = 1; hdr[26] = 1;
            int hs1 = e500 ? 6405 : HDR_S1, ds1 = e500 ? 7536 : DAT_S1;
            var s = e500 ? new Synth0(44100, 166.5, 166.5, 416.5, 416.5) : new Synth0(44100);
            s.Silence(8.0);
            s.Bits(0, hs1); s.Bits(1, HDR_L); s.Bits(0, HDR_S2); s.Bit(1);
            int total = 0;
            foreach (byte b in hdr) { s.Byte(b); for (int k = 0; k < 8; k++) total += (b >> k) & 1; }
            total &= 0xFFFF;
            s.Byte(total >> 8); s.Byte(total & 0xFF); s.Bit(1);
            s.Silence(2.0);
            for (int p = 0; p < data.Count; p += BLOCK)
            {
                s.Block(data.GetRange(p, BLOCK).ToArray(), ds1, DAT_L, DAT_S2);
                s.Silence(4.0);
            }
            return WavFile(s.Buf.ToArray(), 44100);
        }

        static List<List<int>> Bursts(byte[] d, int rate)
        {
            var rising = new List<int>();
            bool prev = d.Length > 0 && d[0] >= 128;
            for (int i = 0; i < d.Length; i++) { bool s = d[i] >= 128; if (s && !prev) rising.Add(i); prev = s; }
            var res = new List<List<int>>(); var cur = new List<int>();
            for (int i = 1; i < rising.Count; i++)
            {
                double us = (rising[i] - rising[i - 1]) * 1e6 / rate;
                if (us > 5000) { if (cur.Count > 0) res.Add(cur); cur = new List<int>(); }
                else cur.Add(us > 520 ? 1 : 0);
            }
            if (cur.Count > 0) res.Add(cur);
            return res;
        }

        static byte[] ParseBurst(List<int> bits, int s1min, int l1min, int s2min, out bool sumOk)
        {
            sumOk = false;
            int i = 0, n = bits.Count, s1 = 0, l1 = 0, s2 = 0;
            while (i < n && bits[i] == 0) { i++; s1++; }
            while (i < n && bits[i] == 1) { i++; l1++; }
            while (i < n && bits[i] == 0) { i++; s2++; }
            if (s1 < s1min * 0.9 || l1 < l1min * 0.9 || s2 < s2min * 0.9) return null;
            i++;
            int count = (n - i - 1) / 9;
            var o = new List<int>();
            for (int k = 0; k < count; k++)
            {
                if (bits[i] != 1) return null;
                int v = 0; for (int b = 1; b <= 8; b++) v = (v << 1) | bits[i + b];
                o.Add(v); i += 9;
            }
            if (o.Count < 2) return null;
            int calc = 0;
            for (int k = 0; k < o.Count - 2; k++) for (int b = 0; b < 8; b++) calc += (o[k] >> b) & 1;
            sumOk = (calc & 0xFFFF) == o[o.Count - 2] * 256 + o[o.Count - 1];
            var res = new byte[o.Count - 2];
            for (int k = 0; k < res.Length; k++) res[k] = (byte)o[k];
            return res;
        }

        // the PC-1600 MODE 0 and PC-E500 tape: the header (48 bytes) and the data of the blocks (the sums are the numbers of 1 bits)
        public static void DecodeTape0(byte[] wav, bool e500, out byte[] hdr, out byte[] data)
        {
            int rate;
            byte[] d = Samples(wav, out rate);
            var bs = Bursts(d, rate);
            if (bs.Count < 2) throw new Exception("no " + (e500 ? "PC-E500" : "PC-1600 (CE-1600P)") + " tape recording found");
            bool ok;
            hdr = ParseBurst(bs[0], e500 ? 6000 : HDR_S1, HDR_L, HDR_S2, out ok);
            if (hdr == null || hdr.Length != 48) throw new Exception("no " + (e500 ? "PC-E500" : "PC-1600 (CE-1600P)") + " tape header found");
            var all = new List<byte>();
            for (int k = 1; k < bs.Count; k++)
            {
                bool sok;
                byte[] blk = ParseBurst(bs[k], e500 ? 7000 : DAT_S1, DAT_L, DAT_S2, out sok);
                if (blk == null) throw new Exception("block " + k + " cannot be read");
                if (!sok) throw new Exception("checksum error in block " + k);
                all.AddRange(blk);
            }
            data = all.ToArray();
        }

        // returns the text of the ASCII file (up to the end-of-file code 1AH) and the tape name
        public static string Decode0(byte[] wav, out string name)
        {
            byte[] hdr, data;
            DecodeTape0(wav, false, out hdr, out data);
            name = Encoding.GetEncoding(28591).GetString(hdr, 1, 13).Trim();
            int e = Array.IndexOf(data, EOFC);
            if (e < 0) e = data.Length;
            return Encoding.GetEncoding(28591).GetString(data, 0, e);
        }

        // ================================================================ PC-1350 / PC-1360 tape (2 ms per bit: 0 = four 2 kHz pulses, 1 = eight 4 kHz pulses)
        // the check sum: an 8 bit register; for a byte its first nibble is added, then the second one with the carry of the first addition
        static int NibSum(List<int> nibs)
        {
            int c = 0, carry = 0;
            for (int j = 0; j < nibs.Count; j++)
            {
                if (j % 2 == 0) { c += nibs[j]; carry = c >> 8; c &= 255; }
                else { c += nibs[j] + carry; c &= 255; }
            }
            return c;
        }

        static int Cs8(byte[] b, int off, int len)
        {
            var n = new List<int>();
            for (int k = 0; k < len; k++) { n.Add(b[off + k] >> 4); n.Add(b[off + k] & 15); }
            return NibSum(n);
        }

        // prog = the program image (lines of the tokenizer), idByte = 70H (PC-1350) or 72H (PC-1360)
        public static byte[] Encode1350(string name, byte[] prog, int idByte)
        {
            if (prog.Length > 0 && prog[prog.Length - 1] == 0xFF) Array.Resize(ref prog, prog.Length - 1);
            byte[] nm = Encoding.GetEncoding(28591).GetBytes(name.ToUpperInvariant());
            int len = Math.Min(7, nm.Length);
            var nb = new List<byte>();                                   // the name: zeros, the characters from the last one, F5H
            for (int k = 0; k < 7 - len; k++) nb.Add(0);
            for (int k = len - 1; k >= 0; k--) nb.Add(nm[k]);
            nb.Add(0xF5);
            var nibs = new List<int> { idByte >> 4, idByte & 15 };
            var nn = new List<int>();
            foreach (byte b in nb) { nn.Add(b & 15); nn.Add(b >> 4); }      // the name: low nibble first
            nibs.AddRange(nn);
            int csn = NibSum(nn);
            nibs.Add(csn >> 4); nibs.Add(csn & 15);
            int hdrBytes = nibs.Count / 2;
            int p = 0;
            while (prog.Length - p >= 120)
            {
                int c = Cs8(prog, p, 120);
                for (int k = 0; k < 120; k++) { nibs.Add(prog[p + k] >> 4); nibs.Add(prog[p + k] & 15); }
                nibs.Add(c >> 4); nibs.Add(c & 15);
                p += 120;
            }
            int tl = prog.Length - p;
            var tail = new byte[tl + 1];
            Array.Copy(prog, p, tail, 0, tl);
            tail[tl] = 0xFF;
            int cf = Cs8(tail, 0, tail.Length);                           // the last sum: the rest of the program and one FFH
            for (int k = 0; k < tl; k++) { nibs.Add(prog[p + k] >> 4); nibs.Add(prog[p + k] & 15); }
            nibs.AddRange(new[] { 15, 15, 15, 15, cf >> 4, cf & 15 });
            int lastByte = nibs.Count / 2 - 1;
            var bits = new List<int>();
            for (int k = 0; k < 4000; k++) bits.Add(1);                   // 8 s of ones
            for (int j = 0; j < nibs.Count; j++)
            {
                bits.Add(0);
                for (int k = 0; k < 4; k++) bits.Add((nibs[j] >> k) & 1);
                if (j % 2 == 0) bits.Add(1);
                else { int stop = (j / 2 < hdrBytes || j / 2 == lastByte) ? 5 : 3; for (int k = 0; k < stop; k++) bits.Add(1); }
            }
            for (int k = 0; k < 100; k++) bits.Add(1);
            var edges = new List<double>();
            double t = 0;
            Action<double, int> tone = (f, cycles) => { double half = Fs / f / 2; for (int c = 0; c < cycles; c++) { t += half; edges.Add(t); t += half; edges.Add(t); } };
            foreach (int b in bits) { if (b != 0) tone(4000, 8); else tone(2000, 4); }
            int total = (int)t + 1;
            var buf = new byte[total];
            for (int i = 0; i < total; i++) buf[i] = 128;
            double pos = 0; bool high = true;
            foreach (double e in edges)
            {
                long a = Round(pos), z = Round(e);
                byte v = high ? (byte)226 : (byte)30;
                for (long i = a; i < z && i < total; i++) buf[i] = v;
                pos = e; high = !high;
            }
            return WavFile(buf, (int)Fs);
        }

        // returns the program image (without the end marker); warn = a message when a check sum does not agree
        public static byte[] Decode1350(byte[] wav, out string name, out int idByte, out string warn)
        {
            warn = null;
            int rate;
            byte[] d = Samples(wav, out rate);
            var rise = new List<int>();
            bool prev = d.Length > 0 && d[0] >= 128;
            for (int i = 0; i < d.Length; i++) { bool s = d[i] >= 128; if (s && !prev) rise.Add(i); prev = s; }
            var bits = new List<int>();
            char cur = ' '; double acc = 0;
            Action flush = () => { if (cur == 'S' || cur == 'F') { long n = Round(acc / 2000.0); for (long k = 0; k < n; k++) bits.Add(cur == 'F' ? 1 : 0); } };
            for (int i = 1; i < rise.Count; i++)
            {
                double us = (rise[i] - rise[i - 1]) * 1e6 / rate;
                char c = us > 1500 ? 'G' : (us > 375 ? 'S' : 'F');
                if (c == cur) acc += us; else { flush(); cur = c; acc = us; }
            }
            flush();
            int ix = 0;
            while (ix < bits.Count && bits[ix] == 1) ix++;
            var nibs = new List<int>();
            while (ix + 5 <= bits.Count)
            {
                if (bits[ix] != 0) { ix++; continue; }
                int v = 0; for (int k = 0; k < 4; k++) v |= bits[ix + 1 + k] << k;
                nibs.Add(v); ix += 5;
                while (ix < bits.Count && bits[ix] == 1) ix++;
            }
            var by = new List<int>();
            for (int k = 0; k + 1 < nibs.Count; k += 2) by.Add((nibs[k] << 4) | nibs[k + 1]);
            idByte = by.Count > 0 ? by[0] : 0;
            if (by.Count < 14 || (idByte != 0x70 && idByte != 0x72)) throw new Exception("no PC-1350 / PC-1360 tape recording found");
            var chars = new List<char>();
            for (int k = 1; k <= 7; k++) { int ch = ((by[k] & 15) << 4) | (by[k] >> 4); if (ch != 0) chars.Insert(0, (char)ch); }
            name = new string(chars.ToArray());
            int r = by.Count - 10;
            if (r < 3) throw new Exception("the recording is too short");
            int blocks = (r - 3) / 121, tl = (r - 3) % 121;
            var prog = new List<byte>();
            int q = 10;
            for (int bk = 0; bk < blocks; bk++)
            {
                var blk = new byte[120];
                for (int k = 0; k < 120; k++) blk[k] = (byte)by[q + k];
                if (Cs8(blk, 0, 120) != by[q + 120]) { warn = "check sum error in block " + (bk + 1); }
                prog.AddRange(blk);
                q += 121;
            }
            var tail = new byte[tl + 1];
            for (int k = 0; k < tl; k++) { tail[k] = (byte)by[q + k]; prog.Add(tail[k]); }
            tail[tl] = 0xFF;
            if (Cs8(tail, 0, tail.Length) != by[q + tl + 2] && warn == null) warn = "check sum error at the end of the recording";
            return prog.ToArray();
        }
    }

    // the picture of the Buy me a coffee button (img/buymeacoffe58x58.jpg, 58 x 58 px) as base64; it is shown at its own size
    static class BmcData
    {
        public const string Jpg =
            "/9j/4QhPRXhpZgAATU0AKgAAAAgADAEAAAMAAAABAZAAAAEBAAMAAAABAZAAAAECAAMAAAADAAAAngEGAAMAAAABAAIAAAESAAMA" +
            "AAABAAEAAAEVAAMAAAABAAMAAAEaAAUAAAABAAAApAEbAAUAAAABAAAArAEoAAMAAAABAAIAAAExAAIAAAAeAAAAtAEyAAIAAAAU" +
            "AAAA0odpAAQAAAABAAAA6AAAASAACAAIAAgACvyAAAAnEAAK/IAAACcQQWRvYmUgUGhvdG9zaG9wIENTNiAoV2luZG93cykAMjAy" +
            "NjoxMToyNCAxMjozNjo0NAAAAAAEkAAABwAAAAQwMjIxoAEAAwAAAAH//wAAoAIABAAAAAEAAAA6oAMABAAAAAEAAAA6AAAAAAAA" +
            "AAYBAwADAAAAAQAGAAABGgAFAAAAAQAAAW4BGwAFAAAAAQAAAXYBKAADAAAAAQACAAACAQAEAAAAAQAAAX4CAgAEAAAAAQAABskA" +
            "AAAAAAAASAAAAAEAAABIAAAAAf/Y/+0ADEFkb2JlX0NNAAL/7gAOQWRvYmUAZIAAAAAB/9sAhAAMCAgICQgMCQkMEQsKCxEVDwwM" +
            "DxUYExMVExMYEQwMDAwMDBEMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMAQ0LCw0ODRAODhAUDg4OFBQODg4OFBEMDAwMDBER" +
            "DAwMDAwMEQwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAz/wAARCAA6ADoDASIAAhEBAxEB/90ABAAE/8QBPwAAAQUBAQEBAQEA" +
            "AAAAAAAAAwABAgQFBgcICQoLAQABBQEBAQEBAQAAAAAAAAABAAIDBAUGBwgJCgsQAAEEAQMCBAIFBwYIBQMMMwEAAhEDBCESMQVB" +
            "UWETInGBMgYUkaGxQiMkFVLBYjM0coLRQwclklPw4fFjczUWorKDJkSTVGRFwqN0NhfSVeJl8rOEw9N14/NGJ5SkhbSVxNTk9KW1" +
            "xdXl9VZmdoaWprbG1ub2N0dXZ3eHl6e3x9fn9xEAAgIBAgQEAwQFBgcHBgU1AQACEQMhMRIEQVFhcSITBTKBkRShsUIjwVLR8DMk" +
            "YuFygpJDUxVjczTxJQYWorKDByY1wtJEk1SjF2RFVTZ0ZeLys4TD03Xj80aUpIW0lcTU5PSltcXV5fVWZnaGlqa2xtbm9ic3R1dn" +
            "d4eXp7fH/9oADAMBAAIRAxEAPwDtEkkl5w6zifWjI+sNdVFXQH0faHEm9j3V+vs02OxqslzanfnrGn62fnu656ng2vE2T8nbVLq3" +
            "SLGdZy7szpGP1tvUrd2IXZApva1lY3Y1VVm3d6LW7v0Sq/sHDH/rJZzf5Lcw7f8Az6ug5aODHgxxrFO48fucHLSnPj+aMjl5vFk/" +
            "Vy/V/wA005mRkTqPC5f966/SOpdcxeqYnT+uXNe3qFNr6G3NrryK7KnQ2m12M70bfWq/SMXSrzrL6VXY+vo2F9WW9Ozs5rnY+ZkZ" +
            "O9zBVD7L9zfUe11a9EaHBjQ9294aA5/G4gQ5/wDacqPxTFjBxZIcMZZBLihAYoxqMvTljHl8ueEYy/mvn/yLLgkTYN6df/Rl0kkl" +
            "ms7/AP/Q7RJJJecOs4vX9rOqfVy9w46k2mfK5jq4/wCiu0rwsasyGAnxdr+VcR9cninpOPmEwMPPxL93gG2bd3/SXfLrfgeLDl5O" +
            "Ep44zninOEZyiJSjr7lRP/VGhzBIyEAkAgPGdXO//GDjVge3G6VZYY7G270h/wBQtJZQeLvr51t/fFxMSj/P3ZK1Vj/H5Xz8h+5C" +
            "Ef8Am8f/AHbY5Yfq77kqSSSWUzv/0e0VXquW7B6ZlZrAHPx6nWNa6dpcPo79vu27v3VaQc3EozcS3EyJ9G0Q8tcWEAEWbm2D6G1z" +
            "Ny87xGIyQMxcBKPGP6l+p1JXRrenEZ1zAzsTLw+sMZawe2xlddrW2VE47GWehkbcirfk5LasZ/8AhvS9WlQwumdOyHU42B1nq9Lb" +
            "KnW047ct7Q2lj/s24BzXenW6z+Y/0itX/Vvol7ftV11ztN1uWckzYGltlNl+RO132V7GPxvoV1LNx+m4fS+s5fUcmp1XT+m0t+xM" +
            "9Q2uc0Ob6WRXX6jrbq6/Ud6TLf0OM/8Amv8ASLYw5sYjk+65c+CdcXtx4eDJk9OMQhD9+U5f+FtaQNjjEZeLY6J1j6t4uzE6fXkz" +
            "mWhzr7mOe61zn/ZfteRkvc53pfav1Tf/AKb/AAf562OmdVxeqU2XYof6dVrqSXjbuLY99ert1fuVKvpfQrMZ2JU59Aw7K8IXiz07" +
            "fUpd9qqqqud9N/q5H7u+xWuldL6f09hGE91rWj0Wuc8P2MYT+rM2BrWbLHOdb/hn2/z6qc2eWkMkx73vGQo5fXxD96cv60WTHxih" +
            "6eHwb6SSSz2d/9LtFU6thWZ/Tr8Op7a32hsF4JYQ17LHVWhha/0bms9G3Z/g3q2kvPMHH7uP2/5zjjwX+/xeh1JVwm9q1eer+rme" +
            "Ok2dOtux3MF4urpawtosHqvyb25NbRurbker6Xo1b66K6aVl9b+reViU29Vdfjh1BEuaxzQ+t9lDG4uS5270MDArr9Oqyl3q+h/O" +
            "LtUzvoO+jwfp/R4/wn8j95bPL/6R940MZ/WH3QDARv0e/r/0/wD1Z7TXl7VfpbafseH6didR62+wk0k0ZFmVZTYH1/pcndssvj1H" +
            "Nfi+iyyjGq9P18XZZ9prXSfV3pWV0rEdj5D2GTuisl255H6fKte5tfvyHbdtDG7KK2f4W3fYtX8530ee3PA/nf5f/otJM+IHmThn" +
            "pjjy1xoQPHrf6EuGH+U4/wBFOLg4hvxKSSSWK2X/2f/tEBhQaG90b3Nob3AgMy4wADhCSU0EBAAAAAAADxwBWgADGyVHHAIAAALq" +
            "nAA4QklNBCUAAAAAABDFXGJ3iQxaE7JA3eJ6NFNSOEJJTQQ6AAAAAAEVAAAAEAAAAAEAAAAAAAtwcmludE91dHB1dAAAAAUAAAAA" +
            "UHN0U2Jvb2wBAAAAAEludGVlbnVtAAAAAEludGUAAAAASW1nIAAAAA9wcmludFNpeHRlZW5CaXRib29sAAAAAAtwcmludGVyTmFt" +
            "ZVRFWFQAAAAUAEUAUABTAE8ATgAgAFgAUAAtADYANAAwACAAUwBlAHIAaQBlAHMAAAAAAA9wcmludFByb29mU2V0dXBPYmpjAAAA" +
            "EQBVAHMAdABhAHcAaQBlAG4AaQBlACAAcAByAPMAYgB5AAAAAAAKcHJvb2ZTZXR1cAAAAAEAAAAAQmx0bmVudW0AAAAMYnVpbHRp" +
            "blByb29mAAAACXByb29mQ01ZSwA4QklNBDsAAAAAAi0AAAAQAAAAAQAAAAAAEnByaW50T3V0cHV0T3B0aW9ucwAAABcAAAAAQ3B0" +
            "bmJvb2wAAAAAAENsYnJib29sAAAAAABSZ3NNYm9vbAAAAAAAQ3JuQ2Jvb2wAAAAAAENudENib29sAAAAAABMYmxzYm9vbAAAAAAA" +
            "Tmd0dmJvb2wAAAAAAEVtbERib29sAAAAAABJbnRyYm9vbAAAAAAAQmNrZ09iamMAAAABAAAAAAAAUkdCQwAAAAMAAAAAUmQgIGRv" +
            "dWJAb+AAAAAAAAAAAABHcm4gZG91YkBv4AAAAAAAAAAAAEJsICBkb3ViQG/gAAAAAAAAAAAAQnJkVFVudEYjUmx0AAAAAAAAAAAA" +
            "AAAAQmxkIFVudEYjUmx0AAAAAAAAAAAAAAAAUnNsdFVudEYjUHhsQFIAAAAAAAAAAAAKdmVjdG9yRGF0YWJvb2wBAAAAAFBnUHNl" +
            "bnVtAAAAAFBnUHMAAAAAUGdQQwAAAABMZWZ0VW50RiNSbHQAAAAAAAAAAAAAAABUb3AgVW50RiNSbHQAAAAAAAAAAAAAAABTY2wg" +
            "VW50RiNQcmNAWQAAAAAAAAAAABBjcm9wV2hlblByaW50aW5nYm9vbAAAAAAOY3JvcFJlY3RCb3R0b21sb25nAAAAAAAAAAxjcm9w" +
            "UmVjdExlZnRsb25nAAAAAAAAAA1jcm9wUmVjdFJpZ2h0bG9uZwAAAAAAAAALY3JvcFJlY3RUb3Bsb25nAAAAAAA4QklNA+0AAAAA" +
            "ABAASAAAAAEAAgBIAAAAAQACOEJJTQQmAAAAAAAOAAAAAAAAAAAAAD+AAAA4QklNBA0AAAAAAAQAAAAeOEJJTQQZAAAAAAAEAAAA" +
            "HjhCSU0D8wAAAAAACQAAAAAAAAAAAQA4QklNJxAAAAAAAAoAAQAAAAAAAAACOEJJTQP1AAAAAABIAC9mZgABAGxmZgAGAAAAAAAB" +
            "AC9mZgABAKGZmgAGAAAAAAABADIAAAABAFoAAAAGAAAAAAABADUAAAABAC0AAAAGAAAAAAABOEJJTQP4AAAAAABwAAD/////////" +
            "////////////////////A+gAAAAA/////////////////////////////wPoAAAAAP////////////////////////////8D6AAA" +
            "AAD/////////////////////////////A+gAADhCSU0ECAAAAAAAEAAAAAEAAAJAAAACQAAAAAA4QklNBB4AAAAAAAQAAAAAOEJJ" +
            "TQQaAAAAAANLAAAABgAAAAAAAAAAAAAAOgAAADoAAAALAGIAdQB5AG0AZQBhAGMAbwBmAGYAZQAAAAEAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAQAAAAAAAAAAAAAAOgAAADoAAAAAAAAAAAAAAAAAAAAAAQAAAAAAAAAAAAAAAAAAAAAAAAAQAAAAAQAAAAAAAG51bGwAAAAC" +
            "AAAABmJvdW5kc09iamMAAAABAAAAAAAAUmN0MQAAAAQAAAAAVG9wIGxvbmcAAAAAAAAAAExlZnRsb25nAAAAAAAAAABCdG9tbG9u" +
            "ZwAAADoAAAAAUmdodGxvbmcAAAA6AAAABnNsaWNlc1ZsTHMAAAABT2JqYwAAAAEAAAAAAAVzbGljZQAAABIAAAAHc2xpY2VJRGxv" +
            "bmcAAAAAAAAAB2dyb3VwSURsb25nAAAAAAAAAAZvcmlnaW5lbnVtAAAADEVTbGljZU9yaWdpbgAAAA1hdXRvR2VuZXJhdGVkAAAA" +
            "AFR5cGVlbnVtAAAACkVTbGljZVR5cGUAAAAASW1nIAAAAAZib3VuZHNPYmpjAAAAAQAAAAAAAFJjdDEAAAAEAAAAAFRvcCBsb25n" +
            "AAAAAAAAAABMZWZ0bG9uZwAAAAAAAAAAQnRvbWxvbmcAAAA6AAAAAFJnaHRsb25nAAAAOgAAAAN1cmxURVhUAAAAAQAAAAAAAG51" +
            "bGxURVhUAAAAAQAAAAAAAE1zZ2VURVhUAAAAAQAAAAAABmFsdFRhZ1RFWFQAAAABAAAAAAAOY2VsbFRleHRJc0hUTUxib29sAQAA" +
            "AAhjZWxsVGV4dFRFWFQAAAABAAAAAAAJaG9yekFsaWduZW51bQAAAA9FU2xpY2VIb3J6QWxpZ24AAAAHZGVmYXVsdAAAAAl2ZXJ0" +
            "QWxpZ25lbnVtAAAAD0VTbGljZVZlcnRBbGlnbgAAAAdkZWZhdWx0AAAAC2JnQ29sb3JUeXBlZW51bQAAABFFU2xpY2VCR0NvbG9y" +
            "VHlwZQAAAABOb25lAAAACXRvcE91dHNldGxvbmcAAAAAAAAACmxlZnRPdXRzZXRsb25nAAAAAAAAAAxib3R0b21PdXRzZXRsb25n" +
            "AAAAAAAAAAtyaWdodE91dHNldGxvbmcAAAAAADhCSU0EKAAAAAAADAAAAAI/8AAAAAAAADhCSU0EEQAAAAAAAQEAOEJJTQQUAAAA" +
            "AAAEAAAAAzhCSU0EDAAAAAAG5QAAAAEAAAA6AAAAOgAAALAAACfgAAAGyQAYAAH/2P/tAAxBZG9iZV9DTQAC/+4ADkFkb2JlAGSA" +
            "AAAAAf/bAIQADAgICAkIDAkJDBELCgsRFQ8MDA8VGBMTFRMTGBEMDAwMDAwRDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAEN" +
            "CwsNDg0QDg4QFA4ODhQUDg4ODhQRDAwMDAwREQwMDAwMDBEMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwM/8AAEQgAOgA6AwEi" +
            "AAIRAQMRAf/dAAQABP/EAT8AAAEFAQEBAQEBAAAAAAAAAAMAAQIEBQYHCAkKCwEAAQUBAQEBAQEAAAAAAAAAAQACAwQFBgcICQoL" +
            "EAABBAEDAgQCBQcGCAUDDDMBAAIRAwQhEjEFQVFhEyJxgTIGFJGhsUIjJBVSwWIzNHKC0UMHJZJT8OHxY3M1FqKygyZEk1RkRcKj" +
            "dDYX0lXiZfKzhMPTdePzRieUpIW0lcTU5PSltcXV5fVWZnaGlqa2xtbm9jdHV2d3h5ent8fX5/cRAAICAQIEBAMEBQYHBwYFNQEA" +
            "AhEDITESBEFRYXEiEwUygZEUobFCI8FS0fAzJGLhcoKSQ1MVY3M08SUGFqKygwcmNcLSRJNUoxdkRVU2dGXi8rOEw9N14/NGlKSF" +
            "tJXE1OT0pbXF1eX1VmZ2hpamtsbW5vYnN0dXZ3eHl6e3x//aAAwDAQACEQMRAD8A7RJJJecOs4n1oyPrDXVRV0B9H2hxJvY91fr7" +
            "NNjsarJc2p356xp+tn57uuep4NrxNk/J21S6t0ixnWcu7M6Rj9bb1K3diF2QKb2tZWN2NVVZt3ei1u79Eqv7Bwx/6yWc3+S3MO3/" +
            "AM+roOWjgx4McaxTuPH7nBy0pz4/mjI5ebxZP1cv1f8ANNOZkZE6jwuX/euv0jqXXMXqmJ0/rlzXt6hTa+htza68iuyp0NptdjO9" +
            "G31qv0jF0q86y+lV2Pr6NhfVlvTs7Oa52PmZGTvcwVQ+y/c31HtdWvRGhwY0PdveGgOfxuIEOf8A2nKj8UxYwcWSHDGWQS4oQGKM" +
            "ajL05Yx5fLnhGMv5r5/8iy4JE2DenX/0ZdJJJZrO/wD/0O0SSSXnDrOL1/azqn1cvcOOpNpnyuY6uP8AortK8LGrMhgJ8Xa/lXEf" +
            "XJ4p6Tj5hMDDz8S/d4Btm3d/0l3y634Hiw5eThKeOM54pzhGcoiUo6+5UT/1RocwSMhAJAIDxnVzv/xg41YHtxulWWGOxtu9If8A" +
            "ULSWUHi76+dbf3xcTEo/z92StVY/x+V8/IfuQhH/AJvH/wB22OWH6u+5KkkkllM7/9HtFV6rluwemZWawBz8ep1jWunaXD6O/b7t" +
            "u791WkHNxKM3EtxMifRtEPLXFhABFm5tg+htczcvO8RiMkDMXASjxj+pfqdSV0a3pxGdcwM7Ey8PrDGWsHtsZXXa1tlROOxlnoZG" +
            "3Iq35OS2rGf/AIb0vVpUMLpnTsh1ONgdZ6vS2yp1tOO3Le0NpY/7NuAc13p1us/mP9IrV/1b6Je37Vddc7TdblnJM2BpbZTZfkTt" +
            "d9lexj8b6FdSzcfpuH0vrOX1HJqdV0/ptLfsTPUNrnNDm+lkV1+o626uv1Heky39DjP/AJr/AEi2MObGI5PuuXPgnXF7ceHgyZPT" +
            "jEIQ/flOX/hbWkDY4xGXi2OidY+reLsxOn15M5loc6+5jnutc5/2X7XkZL3Od6X2r9U3/wCm/wAH+etjpnVcXqlNl2KH+nVa6kl4" +
            "27i2PfXq7dX7lSr6X0KzGdiVOfQMOyvCF4s9O31KXfaqqqrnfTf6uR+7vsVrpXS+n9PYRhPda1o9FrnPD9jGE/qzNga1myxznW/4" +
            "Z9v8+qnNnlpDJMe97xkKOX18Q/enL+tFkx8Yoenh8G+kkks9nf/S7RVOrYVmf06/Dqe2t9obBeCWENeyx1VoYWv9G5rPRt2f4N6t" +
            "pLzzBx+7j9v+c448F/v8XodSVcJvatXnq/q5njpNnTrbsdzBeLq6WsLaLB6r8m9uTW0bq25Hq+l6NW+uiumlZfW/q3lYlNvVXX44" +
            "dQRLmsc0PrfZQxuLkudu9DAwK6/Tqspd6vofzi7VM76Dvo8H6f0eP8J/I/eWzy/+kfeNDGf1h90AwEb9Hv6/9P8A9We015e1X6W2" +
            "n7Hh+nYnUetvsJNJNGRZlWU2B9f6XJ3bLL49RzX4vossoxqvT9fF2Wfaa10n1d6VldKxHY+Q9hk7orJdueR+nyrXubX78h23bQxu" +
            "yitn+Ft32LV/Od9HntzwP53+X/6LSTPiB5k4Z6Y48tcaEDx63+hLhh/lOP8ARTi4OIb8Skkklitl/9kAOEJJTQQhAAAAAABVAAAA" +
            "AQEAAAAPAEEAZABvAGIAZQAgAFAAaABvAHQAbwBzAGgAbwBwAAAAEwBBAGQAbwBiAGUAIABQAGgAbwB0AG8AcwBoAG8AcAAgAEMA" +
            "UwA2AAAAAQA4QklNBAYAAAAAAAcAAQABAAEBAP/hDX1odHRwOi8vbnMuYWRvYmUuY29tL3hhcC8xLjAvADw/eHBhY2tldCBiZWdp" +
            "bj0i77u/IiBpZD0iVzVNME1wQ2VoaUh6cmVTek5UY3prYzlkIj8+IDx4OnhtcG1ldGEgeG1sbnM6eD0iYWRvYmU6bnM6bWV0YS8i" +
            "IHg6eG1wdGs9IkFkb2JlIFhNUCBDb3JlIDUuMy1jMDExIDY2LjE0NTY2MSwgMjAxMi8wMi8wNi0xNDo1NjoyNyAgICAgICAgIj4g" +
            "PHJkZjpSREYgeG1sbnM6cmRmPSJodHRwOi8vd3d3LnczLm9yZy8xOTk5LzAyLzIyLXJkZi1zeW50YXgtbnMjIj4gPHJkZjpEZXNj" +
            "cmlwdGlvbiByZGY6YWJvdXQ9IiIgeG1sbnM6eG1wTU09Imh0dHA6Ly9ucy5hZG9iZS5jb20veGFwLzEuMC9tbS8iIHhtbG5zOnN0" +
            "RXZ0PSJodHRwOi8vbnMuYWRvYmUuY29tL3hhcC8xLjAvc1R5cGUvUmVzb3VyY2VFdmVudCMiIHhtbG5zOmRjPSJodHRwOi8vcHVy" +
            "bC5vcmcvZGMvZWxlbWVudHMvMS4xLyIgeG1sbnM6cGhvdG9zaG9wPSJodHRwOi8vbnMuYWRvYmUuY29tL3Bob3Rvc2hvcC8xLjAv" +
            "IiB4bWxuczp4bXA9Imh0dHA6Ly9ucy5hZG9iZS5jb20veGFwLzEuMC8iIHhtcE1NOkRvY3VtZW50SUQ9IkVDNTJBNDZDNjBDNERG" +
            "NEZBQUY1MzBEN0FEOTYxMTE0IiB4bXBNTTpJbnN0YW5jZUlEPSJ4bXAuaWlkOjlCQjk3MzUzRkJFN0YxMTFBMzk2RjExNUI4RTFD" +
            "MTc5IiB4bXBNTTpPcmlnaW5hbERvY3VtZW50SUQ9IkVDNTJBNDZDNjBDNERGNEZBQUY1MzBEN0FEOTYxMTE0IiBkYzpmb3JtYXQ9" +
            "ImltYWdlL2pwZWciIHBob3Rvc2hvcDpDb2xvck1vZGU9IjMiIHhtcDpDcmVhdGVEYXRlPSIyMDI2LTExLTI0VDExOjU0OjU4KzAx" +
            "OjAwIiB4bXA6TW9kaWZ5RGF0ZT0iMjAyNi0xMS0yNFQxMjozNjo0NCswMTowMCIgeG1wOk1ldGFkYXRhRGF0ZT0iMjAyNi0xMS0y" +
            "NFQxMjozNjo0NCswMTowMCI+IDx4bXBNTTpIaXN0b3J5PiA8cmRmOlNlcT4gPHJkZjpsaSBzdEV2dDphY3Rpb249InNhdmVkIiBz" +
            "dEV2dDppbnN0YW5jZUlEPSJ4bXAuaWlkOjlBQjk3MzUzRkJFN0YxMTFBMzk2RjExNUI4RTFDMTc5IiBzdEV2dDp3aGVuPSIyMDI2" +
            "LTExLTI0VDEyOjM2OjQ0KzAxOjAwIiBzdEV2dDpzb2Z0d2FyZUFnZW50PSJBZG9iZSBQaG90b3Nob3AgQ1M2IChXaW5kb3dzKSIg" +
            "c3RFdnQ6Y2hhbmdlZD0iLyIvPiA8cmRmOmxpIHN0RXZ0OmFjdGlvbj0ic2F2ZWQiIHN0RXZ0Omluc3RhbmNlSUQ9InhtcC5paWQ6" +
            "OUJCOTczNTNGQkU3RjExMUEzOTZGMTE1QjhFMUMxNzkiIHN0RXZ0OndoZW49IjIwMjYtMTEtMjRUMTI6MzY6NDQrMDE6MDAiIHN0" +
            "RXZ0OnNvZnR3YXJlQWdlbnQ9IkFkb2JlIFBob3Rvc2hvcCBDUzYgKFdpbmRvd3MpIiBzdEV2dDpjaGFuZ2VkPSIvIi8+IDwvcmRm" +
            "OlNlcT4gPC94bXBNTTpIaXN0b3J5PiA8L3JkZjpEZXNjcmlwdGlvbj4gPC9yZGY6UkRGPiA8L3g6eG1wbWV0YT4gICAgICAgICAg" +
            "ICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAg" +
            "ICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAg" +
            "ICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAg" +
            "ICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAg" +
            "ICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAg" +
            "ICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAg" +
            "ICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAg" +
            "ICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAg" +
            "ICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAg" +
            "ICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAg" +
            "ICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAg" +
            "ICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAg" +
            "ICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAg" +
            "ICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAg" +
            "ICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAg" +
            "ICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAg" +
            "ICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAg" +
            "ICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAg" +
            "ICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAg" +
            "ICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAg" +
            "ICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAg" +
            "ICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAg" +
            "ICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAg" +
            "ICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAg" +
            "ICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAg" +
            "ICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAg" +
            "ICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAg" +
            "ICAgICAgICAgICAgICA8P3hwYWNrZXQgZW5kPSJ3Ij8+/+4ADkFkb2JlAGSAAAAAAf/bAIQADAgICAkIDAkJDBELCgsRFQ8MDA8V" +
            "GBMTFRMTGBEMDAwMDAwRDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAENCwsNDg0QDg4QFA4ODhQUDg4ODhQRDAwMDAwREQwM" +
            "DAwMDBEMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwM/8AAEQgAOgA6AwEiAAIRAQMRAf/dAAQABP/EAJsAAAMBAAMBAAAAAAAA" +
            "AAAAAAAFBgQBAgcDAQADAQADAAAAAAAAAAAAAAADBAUHAQIGEAACAQQBAQQGBwgDAAAAAAACAwQAARIFExExMhQGISJCIzNDQVFS" +
            "YqJTJGGBsYKSsrPDFUYHEQACAQMCAgcDDQAAAAAAAAABAgMAERITBCEiMWFCUiMzBTJiQ0FRwaKywtLyg5PTFBX/2gAMAwEAAhED" +
            "EQA/ALSiiis4qtSTzRI8wrUhWgNHiCve7wMl8+HowKMqSQqL26TdfNntlvOT6hXEw6/uLGu221DA3Mt0zUR92OyblEuUiyXiILtl" +
            "GUpmOXCI5e6rL/wMO3/Upw/dGZfH/LXoNssEcEa2ie656mG2Z3z9pWMu7ik8NvD8qk3LFieI6rt+Gm+o2W8i7SJr944THYJaaBcK" +
            "1yFsUXQUtKMXC3mV7wKpa86l6pbDXpoXlkddOnCRR5kiTmQWV0Nj8h5DEl16INisA2MszsNrEfZle1uhH/MVI+qRRgxSJirSBskQ" +
            "RKtlbllVdvLOiq3le38GiwMTcG/D5fzVzRRRU2j1/9C0ooorOKrUl3+IbTy48rdmyFPX9jgJfT8NWi4UZd+tgte/1l6f41Eecjsn" +
            "Ux5l79LQ58R+X1WFmOX4qvq9b6HFDLs0Z41d4ndFdlDMvHUsp/UpDcEiQgEgECozb3z/APQYy7W9WNqmMv0+i7XcVv7KZUqsdnef" +
            "N2f0xYkRH9eUmmtR/X2vv2HcRF+rn9+mNsPDv85NFFFFSqPX/9G0rLtZZQdZKmhaxHHUTBEuuNyt3c8fWxy+zWqvjNiImxGxJHXh" +
            "bbodxK4Xta17MyFlu5iQZVncRUSIXF0DLmPcvzVUa9jbptSQN5AnRJcPcADQt6rAWtoixV7xwBnBIxkKzkyRVGP53FyprpC1mukE" +
            "mNA3O3SLFE1McZZjYUgfhsrWIS41kz4H5lan+W9I8fFOc4vRk2XeTfqyw3FiWPkdcS8KYAcbuLVS2PrYer3MvYyVErX61I+CDku0" +
            "iGxDxSFr5Ca5a+QuIG+5jH8L8yrEM0YWT+rLPA9stNccJJOWMIid9nb9ulmBuMwrddaNJuPLcXCJr1yesxtiJ7gIyaRH4XxciSZE" +
            "XF4r9Jn+d8v26cazaxdoljotj41NJN7nbHK49PXX6SyX61Yl6vRMjFEURotDYuFZ9mcbeRJeKUpTi758sj7ObK1arV6/Xhe0IyaI" +
            "24RIjseABe/6YMLCIYMIib842/HpTdnbMJHGtrFhYy8+Q7zt7y0SPMWHLj1VvoooqfR6/9K0rJtoTJ+ufDUYrNth6XO17hewmDCU" +
            "2wXE+Fwhwtw+Wda6KzyDPVj0/MzXC/fy5KqNbE36Lcanl+XJ9tSzXNdHILPs5aRC4oZblOS8ZKxtksZHLxcKs1oWlNK935blREt2" +
            "pPj2JF7dSECGxrNiAGLJIsuCBAWvjUxJcvB8SrWuC7hd3sv3+72fM+59qrO3/wBHWNhGfEOqAUC35Nfj9v8Ak0qXbSt2ujh9FQ+u" +
            "ibHdmy97pvdEhkpiWWNfvZOWDH9OQhOLwgxEZXHzxcGeJXVJ5d1UrVRCjyDC/W+XRd7lkd7e/lNMhX68gscUAOCFh81ubKa+0Xd7" +
            "fo7ey3xfv/66K6eoHcmF+Ea7a62CHPjfsNinxM+zXMWGQ6cqKKKKi0zX/9k=";
    }

    public static class AppInfo
    {
        public const string Version = "1.6";      // 1.6: the Sharp (other) radio button has a box with the models PC-1350, PC-1360, PC-E500, PC-E500S, PC-1450..75, other (folder, Info text and WAV converter per model); 1.5: port TEST button, Renumber and Remove REM in the Files menu, no changes of the port / target / mode / options while a file is sent, default line delay for every baud rate; 1.1: Software UART speeds 1200 to 19200 (SL V3.0), settings.ini, .img files; 1.2: names of the programs checked for the Sharp; 1.4: file info tip, Skip REM, Check Basic, Files menu, drag out of the list; 1.3: PC-1500 Software UART (4800 bps), six target computers, Port settings box, Flush and Reset port, wrong baud rate warning
    }

    // Names of programs for the pocket computers: the length and the characters that the Sharp accepts
    //   PC-1500: up to 16 characters, any ASCII (no quotation mark, no control characters)
    //   PC-1600: 8 + 3 (8.3), capital letters A-Z, digits and # $ % & ' ( ) - @ ^ _ { } ~
    //   other models with the 15-pin connector (PC-1350/1360: 7 characters, PC-E500: 8 + 3): the common rules of 7 + 3 as for the PC-1600
    public static class SharpName
    {
        const string Symbols = "#$%&'()-@^_{}~";

        public static int MaxBase(int model) { return model == 0 ? 16 : model == 2 ? 7 : 8; }

        public static string Rules(int model)
        {
            return model == 0 ? "up to 16 characters, no quotation mark"
                 : model == 2 ? "up to 7 characters, capital letters, digits and " + Symbols
                 : "up to 8 characters, capital letters, digits and " + Symbols;
        }

        // letters with accents -> plain letters (a with ogonek -> a, l with stroke -> l ...)
        static string Plain(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s.Normalize(NormalizationForm.FormD))
            {
                if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
                sb.Append(c == '\u0142' ? 'l' : c == '\u0141' ? 'L' : c);
            }
            return sb.ToString();
        }

        public static string Fix(string name, int model, out bool changed)
        {
            string src = name ?? "";
            var sb = new StringBuilder();
            foreach (char c in Plain(src))
            {
                if (model == 0) sb.Append(c < 32 || c > 126 || c == '"' ? '_' : c);
                else
                {
                    char u = char.ToUpperInvariant(c);
                    sb.Append((u >= 'A' && u <= 'Z') || (u >= '0' && u <= '9') || Symbols.IndexOf(u) >= 0 ? u : '_');
                }
            }
            string r = sb.ToString().Trim();
            int max = MaxBase(model);
            if (r.Length > max) r = r.Substring(0, max).TrimEnd();
            if (r.Length == 0) r = "PRG";
            changed = r != src;
            return r;
        }
    }
}
