// Local screen-only monitor. .NET Framework 4.x, no packages, no network.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using System.Xml;
using System.Xml.Serialization;

namespace EnergyBeanMonitor
{
    public sealed class Cooldown
    {
        public int Id;
        public double Started;
        public double Ends;
        public double Remaining(double now) { return Math.Max(0, Ends - now); }
    }

    // All durations use monotonic seconds, not the Windows wall clock.
    public sealed class Detector
    {
        public const double Duration = 15.0;
        public const double ConfirmSeconds = 0.18;
        public int MaxBeans = 4;
        public double Seconds = Duration;
        public readonly List<Cooldown> Timers = new List<Cooldown>();
        public int Stable = -1;
        int pending = -1;
        int samples;
        int sequence;
        double since;
        double invalidSince = -1;
        public int Events;
        public void ResetDetection() { Stable = -1; pending = -1; samples = 0; invalidSince = -1; }
        public void Clear() { Timers.Clear(); Events = 0; sequence = 0; ResetDetection(); }
        public int Observe(int? count, double now)
        {
            Timers.RemoveAll(delegate(Cooldown c) { return now > c.Ends + 2.0; });
            if (!count.HasValue)
            {
                pending = -1; samples = 0;
                if (invalidSince < 0) invalidSince = now;
                if (now - invalidSince >= 1.0) { Stable = -1; }
                return 0;
            }
            invalidSince = -1;
            int n = count.Value;
            if (n < 0 || n > MaxBeans) throw new ArgumentOutOfRangeException("count");
            if (pending != n) { pending = n; since = now; samples = 1; return 0; }
            samples++;
            if (samples < 3 || now - since + 1e-9 < ConfirmSeconds) return 0;
            int lost = Stable < 0 ? 0 : Math.Max(0, Stable - n);
            Stable = n;
            for (int i = 0; i < lost; i++)
                Timers.Add(new Cooldown { Id = ++sequence, Started = now, Ends = now + Seconds });
            Events += lost;
            return lost;
        }
    }

    public sealed class Sample
    {
        public bool Valid;
        public bool Lit;
        public double MeanBlue;
        public double BrightFraction;
    }

    public static class Vision
    {
        // Sample diamond interiors. Red/orange/blue beans share brightness, not hue.
        public static Sample Read(Bitmap image, Point center, int radius, int threshold)
        {
            radius = Math.Max(2, radius);
            if (center.X - radius < 0 || center.Y - radius < 0 ||
                center.X + radius >= image.Width || center.Y + radius >= image.Height)
                return new Sample();
            int total = 0, colored = 0, bright = 0, sum = 0;
            for (int y = -radius; y <= radius; y++)
                for (int x = -radius; x <= radius; x++)
                {
                    if (Math.Abs(x) + Math.Abs(y) > radius + 1) continue;
                    Color c = image.GetPixel(center.X + x, center.Y + y);
                    total++; sum += c.B;
                    int value = Math.Max(c.R, Math.Max(c.G, c.B));
                    int low = Math.Min(c.R, Math.Min(c.G, c.B));
                    bool redGold = c.R >= c.B * 1.15 && c.R >= c.G * 0.85;
                    bool blue = c.B >= c.R * 1.05 && c.B >= c.G * 1.01;
                    bool highlight = low >= 175 && value - low <= 80;
                    bool plausible = value > 35 && ((value - low >= 22 && (redGold || blue)) || highlight);
                    if (plausible) colored++;
                    if (plausible && value >= threshold) bright++;
                }
            double fraction = (double)bright / total;
            // Intermediate values are animation/uncertainty, not a confirmed consumption.
            return new Sample { Valid = (double)colored / total >= 0.65 &&
                (fraction <= 0.30 || fraction >= 0.70), Lit = fraction >= 0.70,
                MeanBlue = (double)sum / total, BrightFraction = fraction };
        }

        public static Bitmap Capture(Rectangle rect)
        {
            Bitmap b = new Bitmap(rect.Width, rect.Height, PixelFormat.Format24bppRgb);
            try
            {
                using (Graphics g = Graphics.FromImage(b))
                    g.CopyFromScreen(rect.Location, Point.Empty, rect.Size, CopyPixelOperation.SourceCopy);
                return b;
            }
            catch { b.Dispose(); throw; }
        }
    }

    public static class Native
    {
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("shcore.dll")] public static extern int SetProcessDpiAwareness(int value);
        [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(Point p);
        [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint flags);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT rect);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string className, string title);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
        public static IntPtr Root(Point p) { return GetAncestor(WindowFromPoint(p), 2); }
        public static Rectangle Bounds(IntPtr h)
        {
            RECT r;
            if (!GetWindowRect(h, out r)) return Rectangle.Empty;
            return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        }
        public static string Title(IntPtr h)
        {
            StringBuilder s = new StringBuilder(512); GetWindowText(h, s, s.Capacity); return s.ToString();
        }
    }

    public sealed class Selector : Form
    {
        readonly Bitmap snapshot;
        readonly Rectangle screen;
        readonly int slots;
        readonly string side;
        public readonly List<Point> Points = new List<Point>();
        Point hover;
        public Selector(Bitmap image, Rectangle bounds) : this(image, bounds, 4, "右方") { }
        public Selector(Bitmap image, Rectangle bounds, int count, string name)
        {
            snapshot = image; screen = bounds; slots = count; side = name;
            FormBorderStyle = FormBorderStyle.None; StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None; Bounds = bounds; TopMost = true;
            DoubleBuffered = true; Cursor = Cursors.Cross; KeyPreview = true;
            Text = "选择四颗能量豆";
            KeyDown += delegate(object s, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); } };
            MouseMove += delegate(object s, MouseEventArgs e) { hover = e.Location; Invalidate(); };
            MouseUp += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Right) { if (Points.Count > 0) Points.RemoveAt(Points.Count - 1); Invalidate(); return; }
                if (e.Button != MouseButtons.Left) return;
                Point p = new Point(e.X + screen.X, e.Y + screen.Y);
                foreach (Point previous in Points)
                    if (Math.Abs(p.X - previous.X) < 10 && Math.Abs(p.Y - previous.Y) < 10) return;
                Points.Add(p);
                if (Points.Count == slots) { Points.Sort(delegate(Point a, Point b) { return a.X.CompareTo(b.X); }); DialogResult = DialogResult.OK; Close(); }
                else Invalidate();
            };
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.DrawImageUnscaled(snapshot, 0, 0);
            using (Brush b = new SolidBrush(Color.FromArgb(235, 15, 23, 42))) e.Graphics.FillRectangle(b, 20, 20, 860, 90);
            using (Font f = new Font("Microsoft YaHei UI", 16, FontStyle.Bold))
                e.Graphics.DrawString("依次点击" + side + " " + slots + " 颗豆中心（红/蓝均可，暗豆也选）", f, Brushes.White, 34, 30);
            using (Font f = new Font("Microsoft YaHei UI", 11))
                e.Graphics.DrawString("已选 " + Points.Count + "/" + slots + " · 右键撤销 · Esc 取消 · 点击不会传给游戏", f, Brushes.Cyan, 34, 72);
            foreach (Point p in Points)
            {
                int x = p.X - screen.X, y = p.Y - screen.Y;
                e.Graphics.DrawEllipse(Pens.Lime, x - 10, y - 10, 20, 20);
                e.Graphics.DrawLine(Pens.Lime, x - 14, y, x + 14, y);
            }
            // A nearest-neighbour magnifier makes tiny bean centres selectable.
            Rectangle source = new Rectangle(hover.X - 16, hover.Y - 16, 32, 32);
            if (source.X >= 0 && source.Y >= 0 && source.Right <= snapshot.Width && source.Bottom <= snapshot.Height)
            {
                int x = Math.Min(Math.Max(20, hover.X + 36), Width - 150);
                int y = Math.Min(Math.Max(120, hover.Y + 36), Height - 150);
                e.Graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
                e.Graphics.DrawImage(snapshot, new Rectangle(x, y, 128, 128), source, GraphicsUnit.Pixel);
                e.Graphics.DrawRectangle(Pens.Cyan, x, y, 128, 128);
                e.Graphics.DrawLine(Pens.Red, x + 58, y + 64, x + 70, y + 64);
                e.Graphics.DrawLine(Pens.Red, x + 64, y + 58, x + 64, y + 70);
            }
        }
    }

    public sealed class MiniForm : Form
    {
        readonly MonitorForm owner;
        Point drag;
        Rectangle game;
        double leftRatio = 0.5 - 340.0 / 3840, topRatio = 0.11;
        double widthRatio = 340.0 / 1920, heightRatio = 70.0 / 1920;
        public bool Editing { get; private set; }
        readonly string settings = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "overlay-layout.txt");
        public static string FormatText(double remaining)
        { return "替身：" + Math.Max(0, remaining).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " s"; }
        public static Rectangle DefaultBounds(Rectangle target)
        {
            int w = (int)Math.Round(target.Width * 340.0 / 1920);
            int h = (int)Math.Round(target.Width * 70.0 / 1920);
            return new Rectangle(target.X + (target.Width - w) / 2, target.Y + (int)Math.Round(target.Width * 0.11), w, h);
        }
        protected override bool ShowWithoutActivation { get { return !Editing; } }
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x80; // WS_EX_TOOLWINDOW
                if (!Editing) cp.ExStyle |= 0x20 | 0x80000 | 0x08000000; // transparent, layered, no activate
                return cp;
            }
        }
        public MiniForm(MonitorForm parent)
        {
            owner = parent; Text = "拖动位置 / 拖边缩放 · 回面板锁定"; FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual; Bounds = new Rectangle(60, 300, 340, 70);
            AutoScaleMode = AutoScaleMode.None; TopMost = true; DoubleBuffered = true; ShowInTaskbar = false;
            BackColor = Color.Fuchsia; TransparencyKey = Color.Fuchsia; ForeColor = Color.Lime;
            MinimumSize = new Size(160, 42);
            MouseDown += delegate(object s, MouseEventArgs e) { if (Editing) drag = e.Location; };
            MouseMove += delegate(object s, MouseEventArgs e)
            { if (Editing && e.Button == MouseButtons.Left) Location = new Point(Left + e.X - drag.X, Top + e.Y - drag.Y); };
            Resize += delegate { Invalidate(); };
            FormClosing += delegate(object s, FormClosingEventArgs e)
            { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; owner.FinishOverlayEdit(); } };
            try
            {
                string[] p = File.ReadAllText(settings).Trim().Split(',');
                if (p.Length == 4)
                {
                    double a = Double.Parse(p[0], System.Globalization.CultureInfo.InvariantCulture), b = Double.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture);
                    double c = Double.Parse(p[2], System.Globalization.CultureInfo.InvariantCulture), d = Double.Parse(p[3], System.Globalization.CultureInfo.InvariantCulture);
                    if (a >= -1 && a <= 2 && b >= -1 && b <= 2 && c >= 0.04 && c <= 1 && d >= 0.015 && d <= 0.5)
                    { leftRatio = a; topRatio = b; widthRatio = c; heightRatio = d; }
                }
            }
            catch { /* An absent/read-only settings file does not prevent monitoring. */ }
        }
        public void SetEditing(bool value)
        {
            if (Editing == value) return;
            Rectangle content = RectangleToScreen(ClientRectangle);
            if (!value && game.Width > 0)
            {
                leftRatio = (double)(content.X - game.X) / game.Width;
                topRatio = (double)(content.Y - game.Y) / game.Width;
                widthRatio = (double)content.Width / game.Width; heightRatio = (double)content.Height / game.Width;
                try
                {
                    string[] p = new string[] { leftRatio.ToString("R", System.Globalization.CultureInfo.InvariantCulture), topRatio.ToString("R", System.Globalization.CultureInfo.InvariantCulture), widthRatio.ToString("R", System.Globalization.CultureInfo.InvariantCulture), heightRatio.ToString("R", System.Globalization.CultureInfo.InvariantCulture) };
                    File.WriteAllText(settings, String.Join(",", p));
                }
                catch { }
            }
            Editing = value;
            FormBorderStyle = value ? FormBorderStyle.SizableToolWindow : FormBorderStyle.None;
            TransparencyKey = value ? Color.Empty : Color.Fuchsia;
            BackColor = value ? Color.FromArgb(17, 24, 39) : Color.Fuchsia;
            if (value) { ClientSize = content.Size; Location = content.Location; }
            else Bounds = content;
            RecreateHandle(); Invalidate();
        }
        public void Sync(IntPtr target, bool wanted)
        {
            if (!wanted || !Native.IsWindow(target) || Native.IsIconic(target) || !Native.IsWindowVisible(target)) { Hide(); return; }
            game = Native.Bounds(target);
            IntPtr foreground = Native.GetForegroundWindow();
            if (foreground != target && foreground != owner.Handle && foreground != Handle) { Hide(); return; }
            if (!Editing)
            {
                Rectangle next = new Rectangle(game.X + (int)Math.Round(leftRatio * game.Width), game.Y + (int)Math.Round(topRatio * game.Width),
                    Math.Max(160, (int)Math.Round(widthRatio * game.Width)), Math.Max(42, (int)Math.Round(heightRatio * game.Width)));
                if (Bounds != next) Bounds = next;
            }
            if (!Visible) Show();
            Invalidate();
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            string text = owner == null ? FormatText(12.3) : owner.OverlayText;
            bool dual = text.IndexOf('\n') >= 0;
            float pixels = Math.Max(12, Math.Min(ClientSize.Width / (dual ? 9.0f : 7.8f), ClientSize.Height * (dual ? 0.30f : 0.65f)));
            using (Font font = new Font("Microsoft YaHei UI", pixels, FontStyle.Bold, GraphicsUnit.Pixel))
            using (GraphicsPath path = new GraphicsPath())
            using (StringFormat format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap })
            using (Pen outline = new Pen(Color.Black, Math.Max(2, pixels / 10)))
            {
                path.AddString(text, font.FontFamily, (int)FontStyle.Bold, pixels, new RectangleF(4, 2, ClientSize.Width - 8, ClientSize.Height - 4), format);
                outline.LineJoin = LineJoin.Round; e.Graphics.DrawPath(outline, path); e.Graphics.FillPath(Brushes.Lime, path);
            }
        }
    }

    // Only typed local XML is accepted. No Python/config execution and no plugins.
    public sealed class SavedSettings
    {
        public string TargetTitle = "";
        public int Mode;
        public int RightSlots = 4, LeftSlots = 4, Threshold = 160;
        public double Seconds = 15, RightWidth, LeftWidth;
        public PointF[] RightPoints, LeftPoints;
    }

    public static class SettingsStore
    {
        static bool PointsOK(PointF[] points, int slots)
        {
            if (points == null) return true;
            if (points.Length != slots) return false;
            foreach (PointF p in points)
                if (Single.IsNaN(p.X) || Single.IsNaN(p.Y) || p.X < 0 || p.X > 1 || p.Y < 0 || p.Y > 1) return false;
            return true;
        }
        public static void Validate(SavedSettings s)
        {
            if (s == null || s.TargetTitle == null || s.TargetTitle.Length > 512 || s.Mode < 0 || s.Mode > 2 ||
                (s.RightSlots != 4 && s.RightSlots != 6) || (s.LeftSlots != 4 && s.LeftSlots != 6) ||
                s.Threshold < 110 || s.Threshold > 220 || Double.IsNaN(s.Seconds) || s.Seconds < 5 || s.Seconds > 60 ||
                !PointsOK(s.RightPoints,s.RightSlots) || !PointsOK(s.LeftPoints,s.LeftSlots) ||
                Double.IsNaN(s.RightWidth) || Double.IsNaN(s.LeftWidth) || s.RightWidth < 0 || s.LeftWidth < 0 ||
                s.RightWidth > 50000 || s.LeftWidth > 50000 ||
                (s.RightPoints != null && s.RightWidth < 100) || (s.LeftPoints != null && s.LeftWidth < 100))
                throw new InvalidDataException("配置数值或校准点无效");
        }
        public static SavedSettings Load(string path)
        {
            if (new FileInfo(path).Length > 65536) throw new InvalidDataException("配置过大");
            XmlReaderSettings options = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 65536 };
            using (XmlReader reader = XmlReader.Create(path, options))
            {
                SavedSettings s = (SavedSettings)new XmlSerializer(typeof(SavedSettings)).Deserialize(reader);
                Validate(s); return s;
            }
        }
        public static void Save(string path, SavedSettings s)
        {
            Validate(s);
            string temp = path + ".tmp";
            using (XmlWriter writer = XmlWriter.Create(temp, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true }))
                new XmlSerializer(typeof(SavedSettings)).Serialize(writer, s);
            if (File.Exists(path)) File.Replace(temp, path, path + ".bak"); else File.Move(temp, path);
        }
    }

    public sealed class MonitorForm : Form
    {
        readonly Detector detector = new Detector(); // Right-side identity retained for existing checks.
        readonly Detector leftDetector = new Detector();
        readonly Stopwatch clock = Stopwatch.StartNew();
        readonly System.Windows.Forms.Timer tick = new System.Windows.Forms.Timer();
        readonly Label status = new Label(), count = new Label(), headline = new Label(), latest = new Label();
        readonly PictureBox preview = new PictureBox();
        readonly ListBox timers = new ListBox();
        readonly NumericUpDown threshold = new NumericUpDown(), duration = new NumericUpDown();
        readonly ComboBox mode = new ComboBox(), calibrationSide = new ComboBox(), slots = new ComboBox();
        readonly Button start, adjust, calibrate;
        readonly MiniForm mini;
        readonly string config = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"monitor-settings.xml");
        IntPtr anchor;
        PointF[] normalized, leftNormalized;
        double calibratedWidth, leftWidth;
        bool running, loading = true;
        string targetTitle = "", lastEvent = "等待消耗能量豆";
        bool RightEnabled { get { return mode.SelectedIndex != 1; } }
        bool LeftEnabled { get { return mode.SelectedIndex != 0; } }
        double Remaining(Detector d)
        {
            double now = clock.Elapsed.TotalSeconds, end = Double.MaxValue;
            foreach (Cooldown c in d.Timers) if (c.Ends > now) end = Math.Min(end,c.Ends);
            return end < Double.MaxValue ? Math.Max(0,end-now) : 0;
        }
        public string OverlayText
        {
            get
            {
                if (mode.SelectedIndex == 2)
                    return "左" + MiniForm.FormatText(Remaining(leftDetector)) + "\n右" + MiniForm.FormatText(Remaining(detector));
                return MiniForm.FormatText(Remaining(mode.SelectedIndex == 1 ? leftDetector : detector));
            }
        }
        public void FinishOverlayEdit() { mini.SetEditing(false); adjust.Text = "调整悬浮窗"; }
        public string CompactStatus { get { return running ? "离线监测中" : "已暂停"; } }
        public string TimerText
        {
            get
            {
                double r = Remaining(detector), l = Remaining(leftDetector);
                double remaining = r > 0 && l > 0 ? Math.Min(r,l) : Math.Max(r,l);
                return remaining > 0 ? remaining.ToString("0.0") + " s" : "— —";
            }
        }
        public string ActiveSummary { get { return "左右独立计时 · " + duration.Value + " 秒 · 纯离线"; } }
        public MonitorForm()
        {
            Text = "能量豆监测 · 离线增强版"; AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(620,690); FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen; TopMost = true;
            BackColor = Color.FromArgb(17,24,39); ForeColor = Color.White;
            Font = new Font("Microsoft YaHei UI",10); DoubleBuffered = true;
            AddLabel("替身计时 · 离线增强",20,17,400,35,19,Color.White);
            adjust = AddButton("调整悬浮窗",460,20,140,delegate
            {
                if (!Native.IsWindow(anchor)) { status.Text="先校准并绑定游戏窗口"; return; }
                mini.Sync(anchor,true); mini.SetEditing(!mini.Editing);
                adjust.Text=mini.Editing?"锁定悬浮窗":"调整悬浮窗";
                if (mini.Editing) mini.Show();
            });
            AddLabel("无联网 / 无上报 / 不自动点击 / 配置仅保存到本地",20,57,580,24,9,Color.LightGreen);
            AddLabel("监测目标",20,95,88,25,10,Color.Silver);
            mode.Items.AddRange(new object[]{"仅右方","仅左方","左右双方"}); mode.DropDownStyle=ComboBoxStyle.DropDownList;
            Place(mode,113,91,180,30); mode.SelectedIndex=0;
            AddLabel("每次计时",325,95,90,25,10,Color.Silver);
            duration.Minimum=5; duration.Maximum=60; duration.DecimalPlaces=1; duration.Increment=0.5M; duration.Value=15;
            Place(duration,424,91,100,30); AddLabel("秒",540,95,50,25,10,Color.Silver);
            calibrationSide.Items.AddRange(new object[]{"校准右方","校准左方"}); calibrationSide.DropDownStyle=ComboBoxStyle.DropDownList;
            Place(calibrationSide,20,136,150,30); calibrationSide.SelectedIndex=0;
            slots.Items.AddRange(new object[]{"4 颗豆","6 颗豆"}); slots.DropDownStyle=ComboBoxStyle.DropDownList;
            Place(slots,181,136,112,30); slots.SelectedIndex=0;
            calibrate=AddButton("① 选择豆中心",305,132,140,delegate{Calibrate();});
            start=AddButton("② 开始监测",460,132,140,delegate{Toggle();});
            AddButton("清空两边计时",20,181,150,delegate{detector.Clear();leftDetector.Clear();lastEvent="两边计时已清空";});
            AddLabel("亮度阈值",198,187,95,25,10,Color.Silver);
            threshold.Minimum=110;threshold.Maximum=220;threshold.Value=160;Place(threshold,305,182,100,30);
            AddButton("保存本地配置",460,181,140,delegate{SaveConfig();});
            Place(status,20,235,580,46);status.Text="先校准右方或左方；暗豆也要选择，支持红/蓝/赤金亮豆";
            Place(count,20,286,580,30);count.Font=new Font(Font.FontFamily,12,FontStyle.Bold);
            Place(preview,20,326,580,65);preview.BackColor=Color.FromArgb(30,41,59);preview.SizeMode=PictureBoxSizeMode.Zoom;
            AddLabel("采样预览（双边模式显示最近一次采样）",20,397,580,24,9,Color.Silver);
            Place(headline,20,424,580,64);headline.Font=new Font("Segoe UI",34,FontStyle.Bold);headline.ForeColor=Color.Cyan;
            Place(latest,20,496,580,27);latest.ForeColor=Color.LightGreen;
            Place(timers,20,534,580,88);timers.BackColor=Color.FromArgb(30,41,59);timers.ForeColor=Color.White;timers.BorderStyle=BorderStyle.None;
            AddLabel("先校准每一方，再开始。调整窗口/分辨率后请核对预览。\n悬浮窗可拖动缩放；锁定后鼠标穿透。面板可最小化。",20,636,580,49,9,Color.Silver);
            mini=new MiniForm(this);
            calibrationSide.SelectedIndexChanged+=delegate{slots.SelectedIndex=(calibrationSide.SelectedIndex==0?detector.MaxBeans:leftDetector.MaxBeans)==6?1:0;};
            mode.SelectedIndexChanged+=delegate{ResetDetection();SaveConfig();};
            threshold.ValueChanged+=delegate{ResetDetection();SaveConfig();};
            duration.ValueChanged+=delegate{detector.Seconds=leftDetector.Seconds=(double)duration.Value;SaveConfig();};
            LoadConfig();loading=false;
            tick.Interval=50;tick.Tick+=delegate{UpdateFrame();};tick.Start();
            FormClosed+=delegate{SaveConfig();tick.Stop();mini.Dispose();if(preview.Image!=null)preview.Image.Dispose();};
            // Scale design coordinates once; point fonts already follow the desktop DPI.
            using(Graphics g=CreateGraphics()) if(g.DpiX>97) Scale(new SizeF(g.DpiX/96f,g.DpiY/96f));
        }
        void ResetDetection(){detector.ResetDetection();leftDetector.ResetDetection();}
        void Place(Control c,int x,int y,int w,int h){c.SetBounds(x,y,w,h);Controls.Add(c);}
        void AddLabel(string text,int x,int y,int w,int h,float size,Color color)
        {Place(new Label{Text=text,ForeColor=color,Font=new Font("Microsoft YaHei UI",size)},x,y,w,h);}
        Button AddButton(string text,int x,int y,int width,EventHandler action)
        {
            Button b=new Button{Text=text,BackColor=Color.FromArgb(51,65,85),ForeColor=Color.White,FlatStyle=FlatStyle.Flat};
            b.FlatAppearance.BorderSize=0;Place(b,x,y,width,36);b.Click+=action;return b;
        }
        void SaveConfig()
        {
            if(loading)return;
            try
            {
                SettingsStore.Save(config,new SavedSettings{TargetTitle=targetTitle,Mode=mode.SelectedIndex,RightSlots=detector.MaxBeans,LeftSlots=leftDetector.MaxBeans,
                    Threshold=(int)threshold.Value,Seconds=(double)duration.Value,RightWidth=calibratedWidth,LeftWidth=leftWidth,RightPoints=normalized,LeftPoints=leftNormalized});
            }
            catch(Exception ex){status.Text="本地配置未保存："+ex.Message;}
        }
        void LoadConfig()
        {
            if(!File.Exists(config))return;
            try
            {
                SavedSettings s=SettingsStore.Load(config);targetTitle=s.TargetTitle;
                detector.MaxBeans=s.RightSlots;leftDetector.MaxBeans=s.LeftSlots;detector.Seconds=leftDetector.Seconds=s.Seconds;
                normalized=s.RightPoints;leftNormalized=s.LeftPoints;calibratedWidth=s.RightWidth;leftWidth=s.LeftWidth;
                threshold.Value=s.Threshold;duration.Value=(decimal)s.Seconds;mode.SelectedIndex=s.Mode;slots.SelectedIndex=detector.MaxBeans==6?1:0;
                anchor=String.IsNullOrEmpty(targetTitle)?IntPtr.Zero:Native.FindWindow(null,targetTitle);
                status.Text="已载入本地校准；核对预览后开始。若窗口变化请重选。";
            }
            catch(Exception ex){status.Text="已忽略无效本地配置："+ex.Message;}
        }
        void Calibrate()
        {
            bool left=calibrationSide.SelectedIndex==1;int total=slots.SelectedIndex==1?6:4;
            running=false;start.Text="② 开始监测";ResetDetection();Hide();mini.Hide();
            try
            {
                Application.DoEvents();System.Threading.Thread.Sleep(220);Rectangle screen=SystemInformation.VirtualScreen;
                using(Bitmap shot=Vision.Capture(screen))using(Selector selector=new Selector(shot,screen,total,left?"左方":"右方"))
                {
                    if(selector.ShowDialog()!=DialogResult.OK){status.Text="已取消校准";return;}
                    selector.Hide();Application.DoEvents();IntPtr h=Native.Root(selector.Points[0]);Rectangle rect=Native.Bounds(h);
                    if(h==IntPtr.Zero||rect.Width<100||rect.Height<100)throw new Exception("未选到游戏窗口");
                    foreach(Point p in selector.Points)if(Native.Root(p)!=h||!rect.Contains(p))throw new Exception("所有豆必须位于同一窗口");
                    PointF[] positions=new PointF[total];
                    for(int i=0;i<total;i++)positions[i]=new PointF((float)(selector.Points[i].X-rect.X)/rect.Width,(float)(selector.Points[i].Y-rect.Y)/rect.Height);
                    if(anchor!=IntPtr.Zero&&anchor!=h){normalized=null;leftNormalized=null;detector.Clear();leftDetector.Clear();}
                    anchor=h;targetTitle=Native.Title(h);
                    if(left){leftNormalized=positions;leftWidth=rect.Width;leftDetector.MaxBeans=total;leftDetector.ResetDetection();}
                    else{normalized=positions;calibratedWidth=rect.Width;detector.MaxBeans=total;detector.ResetDetection();}
                    SaveConfig();status.Text=(left?"左方":"右方")+total+" 豆校准完成，点击开始";
                }
            }
            catch(Exception ex){status.Text="校准失败："+ex.Message;}
            finally{Show();Activate();}
        }
        void Toggle()
        {
            if(running){running=false;ResetDetection();start.Text="② 开始监测";status.Text="识别已暂停；已有倒计时继续";return;}
            if(!Native.IsWindow(anchor)&&!String.IsNullOrEmpty(targetTitle))anchor=Native.FindWindow(null,targetTitle);
            if((RightEnabled&&normalized==null)||(LeftEnabled&&leftNormalized==null)||!Native.IsWindow(anchor))
            {
                calibrationSide.SelectedIndex=RightEnabled&&normalized==null?0:LeftEnabled&&leftNormalized==null?1:calibrationSide.SelectedIndex;
                Calibrate();return;
            }
            running=true;ResetDetection();start.Text="暂停监测";SaveConfig();
        }
        void UpdateFrame()
        {
            double now=clock.Elapsed.TotalSeconds;
            detector.Timers.RemoveAll(delegate(Cooldown c){return now>c.Ends+2;});leftDetector.Timers.RemoveAll(delegate(Cooldown c){return now>c.Ends+2;});
            if(running)try{ReadScreen(now);}catch(Exception ex){running=false;ResetDetection();start.Text="② 开始监测";status.Text="截屏失败，已暂停："+ex.Message;}
            count.Text="左方："+(leftDetector.Stable<0?"—":leftDetector.Stable.ToString())+" / "+leftDetector.MaxBeans+"       右方："+(detector.Stable<0?"—":detector.Stable.ToString())+" / "+detector.MaxBeans;
            headline.Text=TimerText;latest.Text=lastEvent;timers.BeginUpdate();timers.Items.Clear();
            AddTimers(leftDetector,"左",now);AddTimers(detector,"右",now);timers.EndUpdate();
            mini.Sync(anchor,running||mini.Editing||detector.Timers.Count+leftDetector.Timers.Count>0);
        }
        void AddTimers(Detector d,string side,double now)
        {foreach(Cooldown c in d.Timers)timers.Items.Add(side+"方 消耗 #"+c.Id+"                    "+(c.Remaining(now)>0?c.Remaining(now).ToString("0.0")+" 秒":"计时已到"));}
        void ReadScreen(double now)
        {
            if(!Native.IsWindow(anchor)||Native.IsIconic(anchor)||!Native.IsWindowVisible(anchor))
            {ResetDetection();status.Text="游戏不可见，等待恢复；已有计时继续";return;}
            Rectangle rect=Native.Bounds(anchor);if(rect.Width<100||rect.Height<100){ResetDetection();return;}
            string s="";
            if(RightEnabled)s+=ReadSide(detector,normalized,calibratedWidth,"右",rect,now);
            if(LeftEnabled)s+="  "+ReadSide(leftDetector,leftNormalized,leftWidth,"左",rect,now);
            status.Text=s;
        }
        string ReadSide(Detector d,PointF[] positions,double width,string side,Rectangle rect,double now)
        {
            if(positions==null||positions.Length!=d.MaxBeans){d.ResetDetection();return side+"方未校准";}
            Point[] points=new Point[positions.Length];
            for(int i=0;i<points.Length;i++)
            {
                points[i]=new Point(rect.X+(int)Math.Round(positions[i].X*rect.Width),rect.Y+(int)Math.Round(positions[i].Y*rect.Height));
                if(Native.Root(points[i])!=anchor){d.ResetDetection();return side+"方被遮挡";}
            }
            int radius=Math.Max(2,Math.Min(12,(int)Math.Round(4*rect.Width/Math.Max(100,width))));
            int minX=points[0].X,maxX=points[0].X,minY=points[0].Y,maxY=points[0].Y;
            foreach(Point p in points){minX=Math.Min(minX,p.X);maxX=Math.Max(maxX,p.X);minY=Math.Min(minY,p.Y);maxY=Math.Max(maxY,p.Y);}
            Rectangle roi=Rectangle.FromLTRB(minX-radius-5,minY-radius-5,maxX+radius+6,maxY+radius+6);
            if(!SystemInformation.VirtualScreen.Contains(roi)){d.ResetDetection();return side+"方区域移出屏幕";}
            Bitmap shot=Vision.Capture(roi);int lit=0;bool valid=true;string pattern="";
            for(int i=0;i<points.Length;i++)
            {
                Sample sample=Vision.Read(shot,new Point(points[i].X-roi.X,points[i].Y-roi.Y),radius,(int)threshold.Value);
                valid&=sample.Valid;if(sample.Lit)lit++;pattern+=!sample.Valid?"?":sample.Lit?"●":"○";
            }
            Image old=preview.Image;preview.Image=shot;if(old!=null)old.Dispose();
            int lost=d.Observe(valid?(int?)lit:null,now);
            if(lost>0)lastEvent=side+"方减少 "+lost+" 颗 → 新增 "+lost+" 个 "+d.Seconds.ToString("0.0")+" 秒计时";
            return side+"方 "+pattern+(valid?" 监测中":" 等待稳定");
        }
    }


    public static class Tests
    {
        static readonly StringBuilder log = new StringBuilder();
        static void Check(string name, bool pass)
        { log.AppendLine((pass ? "PASS " : "FAIL ") + name); if (!pass) throw new Exception(name); }
        static int Stable(Detector d, int count, double t)
        { d.Observe(count, t); d.Observe(count, t + 0.05); d.Observe(count, t + 0.10); return d.Observe(count, t + 0.20); }
        static Bitmap Fixture(int count)
        {
            Bitmap b = new Bitmap(170, 40);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.Clear(Color.FromArgb(20, 30, 40));
                for (int i = 0; i < 4; i++)
                    using (Brush brush = new SolidBrush(i >= 4 - count ? Color.FromArgb(170, 235, 250) : Color.FromArgb(23, 47, 96)))
                    { int x = 25 + 40 * i; g.FillPolygon(brush, new Point[] { new Point(x, 7), new Point(x + 10, 20), new Point(x, 33), new Point(x - 10, 20) }); }
            }
            return b;
        }
        static int Count(Bitmap b)
        {
            int count = 0;
            for (int i = 0; i < 4; i++) { Sample s = Vision.Read(b, new Point(25 + 40 * i, 20), 4, 160); Check("fixture_slot_" + i + "_valid", s.Valid); if (s.Lit) count++; }
            return count;
        }
        public static int Run(string input, string report)
        {
            int exit = 0;
            try
            {
                log.AppendLine("EnergyBeanMonitor deterministic tests; all times below are injected monotonic seconds.");
                Detector d = new Detector();
                Check("baseline_4_no_timer", Stable(d, 4, 0) == 0 && d.Stable == 4 && d.Timers.Count == 0);
                d.Observe(3, 0.5); d.Observe(3, 0.55); Stable(d, 4, 0.6);
                Check("brief_flash_ignored", d.Timers.Count == 0);
                Check("4_to_3_one_timer", Stable(d, 3, 1) == 1 && d.Timers.Count == 1);
                Check("countdown_starts_at_15", Math.Abs(d.Timers[0].Remaining(1.2) - 15) < 1e-8);
                Check("countdown_after_1s_is_14", Math.Abs(d.Timers[0].Remaining(2.2) - 14) < 1e-8);
                Check("countdown_after_15s_is_0", d.Timers[0].Remaining(16.2) < 1e-8);
                Stable(d, 3, 2); Check("no_duplicate_on_unchanged_count", d.Events == 1);
                Stable(d, 4, 3); Check("recovery_does_not_trigger", d.Events == 1);
                Check("4_to_2_two_independent_timers", Stable(d, 2, 4) == 2 && d.Timers.Count == 3);
                Check("independent_deadlines", Math.Abs(d.Timers[0].Ends - 16.2) < 1e-8 && Math.Abs(d.Timers[1].Ends - 19.2) < 1e-8);
                d.ResetDetection(); Stable(d, 1, 5); Check("resume_rebaselines_without_false_trigger", d.Events == 3 && d.Timers.Count == 3);
                d.Observe(null, 5.5); Stable(d, 0, 5.6); Check("short_uncertainty_preserves_real_drop", d.Events == 4);
                d.Observe(null, 6); d.Observe(null, 7.1); Stable(d, 4, 7.2); Check("long_uncertainty_rebaselines", d.Events == 4);
                d.Clear(); Stable(d, 4, 0); Check("4_to_0_four_timers", Stable(d, 0, 1) == 4 && d.Timers.Count == 4);
                d.Clear(); Check("clear_resets_all", d.Stable == -1 && d.Timers.Count == 0 && d.Events == 0);
                using (Bitmap four = Fixture(4)) using (Bitmap three = Fixture(3))
                {
                    int a = Count(four), b = Count(three);
                    Check("synthetic_frame_counts_4_and_3", a == 4 && b == 3);
                    Stable(d, a, 0); Stable(d, b, 1);
                    Check("pixel_to_timer_pipeline", d.Events == 1 && Math.Abs(d.Timers[0].Remaining(1.2) - 15) < 1e-8);
                }
                if (!String.IsNullOrEmpty(input))
                {
                    using (Bitmap image = new Bitmap(input))
                    {
                        int n = 0; string pattern = "";
                        int[] xs = new int[] { 1610, 1641, 1672, 1703 };
                        for (int i = 0; i < 4; i++)
                        {
                            Sample s = Vision.Read(image, new Point(xs[i], 190), 4, 160);
                            Check("attachment_slot_" + i + "_valid", s.Valid);
                            if (s.Lit) n++; pattern += s.Lit ? "1" : "0";
                            log.AppendLine("attachment slot=" + (i + 1) + " meanB=" + s.MeanBlue.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " lit=" + s.Lit);
                        }
                        log.AppendLine("attachment pattern=" + pattern + " lit=" + n + "/4");
                        Check("attachment_two_bright_two_dark", pattern == "0011" && n == 2);
                    }
                }
                log.AppendLine("RESULT PASS");
            }
            catch (Exception ex) { log.AppendLine("RESULT FAIL " + ex); exit = 1; }
            log.AppendLine("EXIT " + exit);
            string result = log.ToString();
            if (!String.IsNullOrEmpty(report)) File.WriteAllText(report, result, new UTF8Encoding(true));
            Console.Write(result); return exit;
        }
    }

    public static class Program
    {
        [STAThread] public static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--self-test") return Tests.Run(args.Length > 1 ? args[1] : null, args.Length > 2 ? args[2] : null);
            try { Native.SetProcessDpiAwareness(2); } catch { Native.SetProcessDPIAware(); }
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MonitorForm()); return 0;
        }
    }
}
