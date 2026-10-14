using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace HydraBond {
    // ------------------------------------------------------------------
    // Theme
    // ------------------------------------------------------------------
    static class Theme {
        public static SolidColorBrush Br(byte r, byte g, byte b) {
            SolidColorBrush x = new SolidColorBrush(Color.FromRgb(r, g, b));
            x.Freeze();
            return x;
        }
        public static readonly Brush Bg = Br(9, 9, 11);
        public static readonly Brush Card = Br(18, 18, 21);
        public static readonly Brush Card2 = Br(24, 24, 27);
        public static readonly Brush Line = Br(39, 39, 42);
        public static readonly Brush Line2 = Br(63, 63, 70);
        public static readonly Brush Text = Br(250, 250, 250);
        public static readonly Brush Text2 = Br(212, 212, 216);
        public static readonly Brush Muted = Br(161, 161, 170);
        public static readonly Brush Dim = Br(113, 113, 122);
        public static readonly Brush Green = Br(16, 185, 129);
        public static readonly Brush Amber = Br(234, 179, 8);
        public static readonly Brush Red = Br(239, 68, 68);

        public static readonly Color[] Palette = new Color[] {
            Color.FromRgb(59, 130, 246),  // blue
            Color.FromRgb(168, 85, 247),  // purple
            Color.FromRgb(16, 185, 129),  // emerald
            Color.FromRgb(245, 158, 11),  // amber
            Color.FromRgb(236, 72, 153),  // pink
            Color.FromRgb(6, 182, 212),   // cyan
            Color.FromRgb(249, 115, 22),  // orange
            Color.FromRgb(132, 204, 22)   // lime
        };

        public static SolidColorBrush Tint(Color c, byte alpha) {
            SolidColorBrush b = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
            b.Freeze();
            return b;
        }
    }

    // ------------------------------------------------------------------
    // Local control API (the Node engine)
    // ------------------------------------------------------------------
    static class Api {
        public const string Base = "http://127.0.0.1:4545";

        public static string Call(string method, string path, string body, int timeoutMs) {
            HttpWebRequest rq = (HttpWebRequest)WebRequest.Create(Base + path);
            rq.Method = method;
            rq.Proxy = null;
            rq.Timeout = timeoutMs;
            rq.ReadWriteTimeout = timeoutMs;
            if (method == "POST") {
                rq.ContentType = "application/json";
                byte[] b = Encoding.UTF8.GetBytes(body ?? "");
                rq.ContentLength = b.Length;
                using (System.IO.Stream s = rq.GetRequestStream()) s.Write(b, 0, b.Length);
            }
            using (WebResponse r = rq.GetResponse())
            using (System.IO.StreamReader sr = new System.IO.StreamReader(r.GetResponseStream()))
                return sr.ReadToEnd();
        }
    }

    // ------------------------------------------------------------------
    // Live per-adapter speed straight from Windows' interface counters, so every
    // adapter shows its real speed whether or not traffic goes through Boost.
    // ------------------------------------------------------------------
    class NetMonitor {
        private class Snap { public long Rx, Tx; public DateTime At; }
        private readonly Dictionary<string, Snap> last = new Dictionary<string, Snap>();
        private Dictionary<string, double[]> lastResult = new Dictionary<string, double[]>();

        // ip -> { downMbps, upMbps }
        public Dictionary<string, double[]> Sample() {
            DateTime now = DateTime.UtcNow;
            Dictionary<string, double[]> res = new Dictionary<string, double[]>();
            bool tooSoon = false;
            try {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces()) {
                    try {
                        if (ni.OperationalStatus != OperationalStatus.Up) continue;
                        IPv4InterfaceStatistics st = ni.GetIPv4Statistics();
                        foreach (UnicastIPAddressInformation ua in ni.GetIPProperties().UnicastAddresses) {
                            if (ua.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                            string ip = ua.Address.ToString();
                            Snap prev;
                            if (last.TryGetValue(ip, out prev)) {
                                double secs = (now - prev.At).TotalSeconds;
                                if (secs < 0.6) { tooSoon = true; continue; }
                                double down = Math.Max(0, (st.BytesReceived - prev.Rx) * 8.0 / 1e6 / secs);
                                double up = Math.Max(0, (st.BytesSent - prev.Tx) * 8.0 / 1e6 / secs);
                                res[ip] = new double[] { down, up };
                            }
                            last[ip] = new Snap { Rx = st.BytesReceived, Tx = st.BytesSent, At = now };
                        }
                    } catch { }
                }
            } catch { }
            if (tooSoon) return lastResult;
            lastResult = res;
            return res;
        }
    }

    // ------------------------------------------------------------------
    // Engine host: runs node server.js, restarts it, and guarantees the
    // Windows proxy settings are restored whenever it goes away.
    // ------------------------------------------------------------------
    class Engine {
        private Process proc;
        private bool stopping;
        private int restarts;
        private DateTime windowStart = DateTime.UtcNow;
        private readonly string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        private readonly object gate = new object();
        public event Action<string, bool> Notice;

        private string ServerJs { get { return System.IO.Path.Combine(baseDir, "server.js"); } }

        private void Say(string msg, bool warn) {
            Action<string, bool> h = Notice;
            if (h != null) h(msg, warn);
        }

        private ProcessStartInfo Psi(string args) {
            ProcessStartInfo psi = new ProcessStartInfo {
                FileName = "node",
                Arguments = "\"" + ServerJs + "\" " + args,
                WorkingDirectory = baseDir,
                CreateNoWindow = true,
                UseShellExecute = false
            };
            psi.EnvironmentVariables["HYDRABOND_PARENT_PID"] = Process.GetCurrentProcess().Id.ToString();
            return psi;
        }

        public void Start() {
            lock (gate) {
                if (stopping) return;
                if (!System.IO.File.Exists(ServerJs)) {
                    Say("server.js was not found next to HydraBond.exe.", true);
                    return;
                }
                try {
                    proc = Process.Start(Psi(""));
                    proc.EnableRaisingEvents = true;
                    proc.Exited += OnExited;
                } catch (Exception ex) {
                    Say("Node.js is required (https://nodejs.org). " + ex.Message, true);
                }
            }
        }

        private void OnExited(object sender, EventArgs e) {
            if (stopping) return;
            int code = -1;
            try { code = ((Process)sender).ExitCode; } catch { }
            if (code == 3) {
                Say("Another HydraBond engine is already running; connected to it.", false);
                return;
            }
            RunRestore();
            if ((DateTime.UtcNow - windowStart).TotalSeconds > 60) { windowStart = DateTime.UtcNow; restarts = 0; }
            restarts++;
            if (restarts > 5) {
                Say("HydraBond's engine keeps stopping. Your normal internet settings were restored. Details: %APPDATA%\\HydraBond\\hydrabond.log", true);
                return;
            }
            Say("HydraBond's engine stopped unexpectedly. Restarting it (your normal internet settings were restored).", true);
            Thread.Sleep(1000 * restarts);
            Start();
        }

        // Runs "node server.js --restore": puts the original Windows proxy settings back.
        public void RunRestore() {
            try {
                using (Process p = Process.Start(Psi("--restore"))) {
                    if (p != null) p.WaitForExit(10000);
                }
            } catch { }
        }

        public void Stop() {
            lock (gate) {
                if (stopping) return;
                stopping = true;
            }
            try { Api.Call("POST", "/api/exit", "{}", 1500); } catch { }
            try {
                if (proc != null && !proc.HasExited) {
                    if (!proc.WaitForExit(3000)) proc.Kill();
                }
            } catch { }
            RunRestore(); // safety net - no-op when the engine already restored
        }
    }

    // ------------------------------------------------------------------
    // Small controls
    // ------------------------------------------------------------------
    class Toggle : Border {
        private Ellipse thumb;
        private bool on;
        public Action<bool> Clicked;
        public bool On { get { return on; } }

        public Toggle() {
            Width = 34; Height = 18;
            CornerRadius = new CornerRadius(9);
            Padding = new Thickness(2);
            Cursor = Cursors.Hand;
            thumb = new Ellipse { Width = 14, Height = 14, Fill = Theme.Text, HorizontalAlignment = HorizontalAlignment.Left };
            Child = thumb;
            MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e) {
                e.Handled = true;
                if (Clicked != null) Clicked(!on);
            };
            SetOn(false);
        }

        public void SetOn(bool v) {
            on = v;
            Background = v ? Theme.Green : Theme.Line2;
            thumb.HorizontalAlignment = v ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        }
    }

    class DeviceNode {
        public string Ip;
        public Color Accent;
        public Border Card;
        public Border IconBox;
        public TextBlock Icon, Name, Sub, Speed, Rate, Status;
        public Toggle Sw;
        public System.Windows.Shapes.Path Flow;
        public System.Windows.Shapes.Path Arc;
        public bool Enabled = true;
        public double SpeedMbps, Share, Down, Up;
    }

    // ------------------------------------------------------------------
    // Main window
    // ------------------------------------------------------------------
    public class MainWindow : Window {
        private readonly Engine engine = new Engine();
        private readonly NetMonitor monitor = new NetMonitor();
        private double peakSeen;
        private readonly JavaScriptSerializer json = new JavaScriptSerializer();
        private DispatcherTimer timer;
        private bool polling;
        private int failedPolls;
        private bool cleaned;

        // header
        private Ellipse dotStatus;
        private TextBlock txtStatus;
        private Toggle proxySwitch;
        private TextBlock txtProxy;
        private DateTime proxyPendingUntil = DateTime.MinValue;
        private bool boostOn;
        private Border banner;
        private TextBlock bannerText;

        // stage
        private Grid stage;
        private Canvas flowLayer, hubLayer, cardLayer;
        private System.Windows.Shapes.Ellipse hubTrack, hubDisc;
        private Grid hubCenter;
        private TextBlock txtTotal, txtUnit, txtMBs, txtCaption, txtEmpty;
        private Border netNode, ghostCard;
        private System.Windows.Shapes.Path ghostFlow;
        private bool ghostOn;
        private TextBlock netLabel;
        private System.Windows.Shapes.Path outFlow;
        private readonly List<DeviceNode> nodes = new List<DeviceNode>();
        private readonly Dictionary<string, DeviceNode> nodeByIp = new Dictionary<string, DeviceNode>();
        private readonly Dictionary<string, int> colorByIp = new Dictionary<string, int>();
        private readonly Dictionary<string, Color> colorByName = new Dictionary<string, Color>();
        private double totalTarget, totalShown;
        private bool anyTraffic;

        // stats strip
        private TextBlock stDown, stUp, stSession, stPeak, stStreams, stUptime;

        // activity
        private StackPanel feed;
        private long lastFeedId = -1;

        public MainWindow() {
            Build();
            engine.Notice += delegate(string msg, bool warn) {
                Dispatcher.BeginInvoke(new Action(delegate { SetBanner(msg, warn); }));
            };
            engine.Start();
            timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
            timer.Tick += delegate { Poll(); };
            timer.Start();
            CompositionTarget.Rendering += OnRender;
            SizeChanged += delegate { Layout(); };
            Loaded += delegate { Layout(); Poll(); };
        }

        // ---------------- build ----------------
        private static TextBlock Tb(string text, double size, Brush fg, FontWeight? weight) {
            return new TextBlock {
                Text = text, FontSize = size, Foreground = fg,
                FontWeight = weight.HasValue ? weight.Value : FontWeights.Normal
            };
        }

        private static Border Card(Thickness pad) {
            return new Border {
                CornerRadius = new CornerRadius(10), Background = Theme.Card,
                BorderBrush = Theme.Line, BorderThickness = new Thickness(1), Padding = pad
            };
        }

        private void Build() {
            Title = "HydraBond";
            Width = 1020; Height = 700; MinWidth = 880; MinHeight = 620;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.CanResizeWithGrip;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            FontFamily = new FontFamily("Segoe UI");
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

            Border root = new Border {
                CornerRadius = new CornerRadius(12), Background = Theme.Bg,
                BorderBrush = Theme.Line, BorderThickness = new Thickness(1), ClipToBounds = true
            };
            Content = root;

            Grid layout = new Grid();
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(54) });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(150) });
            root.Child = layout;

            UIElement header = BuildHeader();
            Grid.SetRow(header, 0); layout.Children.Add(header);

            banner = new Border { Visibility = Visibility.Collapsed, Padding = new Thickness(24, 7, 24, 7) };
            bannerText = Tb("", 12, Theme.Text, FontWeights.Medium);
            banner.Child = bannerText;
            Grid.SetRow(banner, 1); layout.Children.Add(banner);

            BuildStage();
            Grid.SetRow(stage, 2); layout.Children.Add(stage);

            UIElement stats = BuildStats();
            Grid.SetRow(stats, 3); layout.Children.Add(stats);

            UIElement act = BuildActivity();
            Grid.SetRow(act, 4); layout.Children.Add(act);
        }

        private UIElement BuildHeader() {
            Border bar = new Border { BorderBrush = Theme.Line, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(22, 0, 12, 0), Background = Theme.Bg };
            bar.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e) {
                if (e.ClickCount == 1) { try { DragMove(); } catch { } }
            };

            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            StackPanel brand = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            brand.Children.Add(BuildLogo());
            TextBlock title = Tb("HydraBond", 16, Theme.Text, FontWeights.Bold);
            title.Margin = new Thickness(9, 0, 0, 0);
            brand.Children.Add(title);
            Border ver = new Border { CornerRadius = new CornerRadius(4), Background = Theme.Line, Padding = new Thickness(6, 1, 6, 2), Margin = new Thickness(10, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            ver.Child = Tb("v1.1", 11, Theme.Muted, FontWeights.Medium);
            brand.Children.Add(ver);
            TextBlock by = Tb("by Karthikeya Reddy", 12, Theme.Dim, null);
            by.Margin = new Thickness(10, 2, 0, 0); by.VerticalAlignment = VerticalAlignment.Center;
            brand.Children.Add(by);
            g.Children.Add(brand);

            StackPanel right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            Border pill = new Border { CornerRadius = new CornerRadius(14), Padding = new Thickness(11, 5, 12, 5), Background = Theme.Card2, BorderBrush = Theme.Line, BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 18, 0) };
            StackPanel ps = new StackPanel { Orientation = Orientation.Horizontal };
            dotStatus = new Ellipse { Width = 7, Height = 7, Fill = Theme.Amber, Margin = new Thickness(0, 1, 7, 0), VerticalAlignment = VerticalAlignment.Center };
            txtStatus = Tb("Starting engine...", 11.5, Theme.Text2, FontWeights.Medium);
            ps.Children.Add(dotStatus); ps.Children.Add(txtStatus);
            pill.Child = ps;
            right.Children.Add(pill);

            StackPanel sw = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 18, 0), VerticalAlignment = VerticalAlignment.Center };
            txtProxy = Tb("Boost all apps", 12, Theme.Muted, FontWeights.Medium);
            txtProxy.Margin = new Thickness(0, 0, 9, 0); txtProxy.VerticalAlignment = VerticalAlignment.Center;
            proxySwitch = new Toggle();
            string tip = "When on, every app on this PC uses your combined connections.\nTurning it off - or closing HydraBond - puts your normal internet settings back.";
            proxySwitch.ToolTip = tip; txtProxy.ToolTip = tip;
            proxySwitch.Clicked = delegate(bool want) { SetProxy(want); };
            sw.Children.Add(txtProxy); sw.Children.Add(proxySwitch);
            right.Children.Add(sw);

            right.Children.Add(IconButton("", delegate { WindowState = WindowState.Minimized; }, false));
            right.Children.Add(IconButton("", delegate { Close(); }, true));
            Grid.SetColumn(right, 1); g.Children.Add(right);

            bar.Child = g;
            return bar;
        }

        // App mark: three adapter streams merging into a hub, one pooled stream out.
        private static UIElement BuildLogo() {
            Canvas c = new Canvas { Width = 40, Height = 40 };
            Border tile = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(9), Background = Theme.Card2, BorderBrush = Theme.Line2, BorderThickness = new Thickness(0.6) };
            c.Children.Add(tile);
            string[] geo = new string[] { "M6,10 C13,10 12,20 16,20", "M6,20 L16,20", "M6,30 C13,30 12,20 16,20", "M27.2,20 L34,20" };
            Brush[] col = new Brush[] { new SolidColorBrush(Theme.Palette[0]), new SolidColorBrush(Theme.Palette[1]), new SolidColorBrush(Theme.Palette[2]), Theme.Text };
            for (int i = 0; i < geo.Length; i++) {
                c.Children.Add(new System.Windows.Shapes.Path { Data = Geometry.Parse(geo[i]), Stroke = col[i], StrokeThickness = 2.6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
            }
            System.Windows.Shapes.Ellipse ring = new System.Windows.Shapes.Ellipse { Width = 10.4, Height = 10.4, Stroke = Theme.Text, StrokeThickness = 2.2 };
            Canvas.SetLeft(ring, 15.8); Canvas.SetTop(ring, 14.8);
            System.Windows.Shapes.Ellipse dot = new System.Windows.Shapes.Ellipse { Width = 4.4, Height = 4.4, Fill = Theme.Text };
            Canvas.SetLeft(dot, 18.8); Canvas.SetTop(dot, 17.8);
            c.Children.Add(ring); c.Children.Add(dot);
            return new Viewbox { Width = 28, Height = 28, Child = c, VerticalAlignment = VerticalAlignment.Center };
        }

        private Border IconButton(string glyph, Action onClick, bool danger) {
            Border b = new Border { Width = 34, Height = 30, CornerRadius = new CornerRadius(6), Background = Brushes.Transparent, Cursor = Cursors.Hand, Margin = new Thickness(2, 0, 0, 0) };
            TextBlock t = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 10, Foreground = Theme.Muted, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            b.Child = t;
            b.MouseEnter += delegate { b.Background = danger ? Theme.Red : Theme.Line; t.Foreground = Theme.Text; };
            b.MouseLeave += delegate { b.Background = Brushes.Transparent; t.Foreground = Theme.Muted; };
            b.MouseLeftButtonUp += delegate { onClick(); };
            return b;
        }

        private void BuildStage() {
            stage = new Grid { ClipToBounds = true, Margin = new Thickness(0, 4, 0, 0) };
            flowLayer = new Canvas(); hubLayer = new Canvas(); cardLayer = new Canvas();
            stage.Children.Add(flowLayer); stage.Children.Add(hubLayer); stage.Children.Add(cardLayer);

            outFlow = new System.Windows.Shapes.Path { Stroke = Theme.Line2, StrokeThickness = 2, StrokeDashArray = new DoubleCollection(new double[] { 3, 4 }), StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
            AnimateDash(outFlow);
            flowLayer.Children.Add(outFlow);

            hubTrack = new System.Windows.Shapes.Ellipse { Stroke = Theme.Line, StrokeThickness = 14, Fill = Brushes.Transparent };
            hubDisc = new System.Windows.Shapes.Ellipse { Fill = Theme.Card, Stroke = Theme.Line, StrokeThickness = 1 };
            hubLayer.Children.Add(hubDisc);
            hubLayer.Children.Add(hubTrack);

            hubCenter = new Grid();
            StackPanel cs = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            txtTotal = Tb("0.0", 40, Theme.Text, FontWeights.Bold); txtTotal.HorizontalAlignment = HorizontalAlignment.Center;
            txtUnit = Tb("Mbps combined", 12, Theme.Muted, FontWeights.SemiBold); txtUnit.HorizontalAlignment = HorizontalAlignment.Center;
            txtMBs = Tb("0.0 MB/s", 12, Theme.Dim, null); txtMBs.HorizontalAlignment = HorizontalAlignment.Center; txtMBs.Margin = new Thickness(0, 2, 0, 0);
            cs.Children.Add(txtTotal); cs.Children.Add(txtUnit); cs.Children.Add(txtMBs);
            hubCenter.Children.Add(cs);
            hubLayer.Children.Add(hubCenter);

            txtCaption = Tb("", 12, Theme.Muted, null);
            txtCaption.TextAlignment = TextAlignment.Center; txtCaption.TextWrapping = TextWrapping.Wrap;
            hubLayer.Children.Add(txtCaption);

            netNode = new Border { Width = 76, Height = 76, CornerRadius = new CornerRadius(38), Background = Theme.Card, BorderBrush = Theme.Line2, BorderThickness = new Thickness(1) };
            netNode.Child = new TextBlock { Text = "", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 28, Foreground = Theme.Muted, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            hubLayer.Children.Add(netNode);
            netLabel = Tb("Internet", 12, Theme.Muted, FontWeights.Medium);
            netLabel.TextAlignment = TextAlignment.Center; netLabel.Width = 76;
            hubLayer.Children.Add(netLabel);

            ghostCard = new Border { CornerRadius = new CornerRadius(10), BorderBrush = Theme.Line2, BorderThickness = new Thickness(1), Background = Brushes.Transparent, Padding = new Thickness(14, 8, 14, 8), Visibility = Visibility.Collapsed };
            ghostCard.BorderBrush = Theme.Line2;
            StackPanel gs = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            gs.Children.Add(Tb("+  Add another network", 13, Theme.Muted, FontWeights.SemiBold));
            gs.Children.Add(Tb("USB-tether a phone, or join Wi-Fi + Ethernet", 10.5, Theme.Dim, null));
            ghostCard.Child = gs;
            cardLayer.Children.Add(ghostCard);
            ghostFlow = new System.Windows.Shapes.Path { Stroke = Theme.Line, StrokeThickness = 1.5, StrokeDashArray = new DoubleCollection(new double[] { 1, 5 }), StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Visibility = Visibility.Collapsed };
            flowLayer.Children.Add(ghostFlow);

            txtEmpty = Tb("No active network adapters found.\nConnect to Wi-Fi, Ethernet or a USB-tethered phone.", 13, Theme.Muted, null);
            txtEmpty.TextAlignment = TextAlignment.Left; txtEmpty.Visibility = Visibility.Collapsed;
            cardLayer.Children.Add(txtEmpty);
        }

        private UIElement BuildStats() {
            Grid g = new Grid { Margin = new Thickness(24, 6, 24, 12) };
            string[] labels = new string[] { "DOWNLOAD", "UPLOAD", "SESSION DATA", "PEAK", "ACTIVE STREAMS", "UPTIME" };
            TextBlock[] vals = new TextBlock[labels.Length];
            for (int i = 0; i < labels.Length; i++) {
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                if (i < labels.Length - 1) g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
                Border c = Card(new Thickness(14, 10, 14, 11));
                StackPanel sp = new StackPanel();
                sp.Children.Add(Tb(labels[i], 10, Theme.Dim, FontWeights.SemiBold));
                vals[i] = Tb("-", 17, Theme.Text, FontWeights.Bold);
                vals[i].Margin = new Thickness(0, 3, 0, 0);
                sp.Children.Add(vals[i]);
                c.Child = sp;
                Grid.SetColumn(c, i * 2);
                g.Children.Add(c);
            }
            stDown = vals[0]; stUp = vals[1]; stSession = vals[2]; stPeak = vals[3]; stStreams = vals[4]; stUptime = vals[5];
            return g;
        }

        private UIElement BuildActivity() {
            Border c = Card(new Thickness(16, 10, 8, 8));
            c.Margin = new Thickness(24, 0, 24, 18);
            Grid g = new Grid();
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            TextBlock h = Tb("RECENT CONNECTIONS", 10, Theme.Dim, FontWeights.SemiBold);
            h.Margin = new Thickness(0, 0, 0, 6);
            g.Children.Add(h);
            ScrollViewer sv = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            feed = new StackPanel();
            sv.Content = feed;
            Grid.SetRow(sv, 1); g.Children.Add(sv);
            c.Child = g;
            return c;
        }

        // ---------------- polling ----------------
        private void Poll() {
            if (polling) return;
            polling = true;
            ThreadPool.QueueUserWorkItem(delegate {
                Dictionary<string, double[]> speeds = monitor.Sample();
                Dictionary<string, object> data = null;
                try {
                    string raw = Api.Call("GET", "/api/stats", null, 2000);
                    data = json.Deserialize<Dictionary<string, object>>(raw);
                } catch { }
                Dispatcher.BeginInvoke(new Action(delegate {
                    polling = false;
                    if (data == null) {
                        failedPolls++;
                        if (failedPolls >= 3) SetOffline();
                        return;
                    }
                    failedPolls = 0;
                    try { UpdateView(data, speeds); } catch { }
                }));
            });
        }

        private void SetProxy(bool want) {
            proxySwitch.SetOn(want); boostOn = want;
            txtProxy.Text = "Boost all apps";
            txtProxy.Foreground = want ? Theme.Text : Theme.Muted;
            proxyPendingUntil = DateTime.UtcNow.AddSeconds(2);
            ThreadPool.QueueUserWorkItem(delegate {
                string err = null;
                try { Api.Call("POST", "/api/proxy", "{\"enabled\":" + (want ? "true" : "false") + "}", 8000); }
                catch (WebException ex) {
                    err = "Couldn't switch Boost " + (want ? "on" : "off") + ".";
                    try {
                        if (ex.Response != null) {
                            using (System.IO.StreamReader sr = new System.IO.StreamReader(ex.Response.GetResponseStream())) {
                                Dictionary<string, object> d = json.Deserialize<Dictionary<string, object>>(sr.ReadToEnd());
                                if (d.ContainsKey("error") && !string.IsNullOrEmpty(Str(d["error"]))) err = Str(d["error"]);
                            }
                        }
                    } catch { }
                } catch { err = "Couldn't switch Boost " + (want ? "on" : "off") + "."; }
                Dispatcher.BeginInvoke(new Action(delegate {
                    if (err != null) { proxyPendingUntil = DateTime.MinValue; SetBanner(err, true); }
                    Poll();
                }));
            });
        }

        private void SetAdapter(string ip, bool enabled) {
            ThreadPool.QueueUserWorkItem(delegate {
                try { Api.Call("POST", "/api/adapter", "{\"ip\":\"" + ip + "\",\"enabled\":" + (enabled ? "true" : "false") + "}", 3000); } catch { }
                Dispatcher.BeginInvoke(new Action(delegate { Poll(); }));
            });
        }

        // ---------------- helpers ----------------
        private static double Num(object o) { try { return Convert.ToDouble(o); } catch { return 0; } }
        private static string Str(object o) { return o == null ? "" : o.ToString(); }
        private static bool Bool(object o) { return o is bool && (bool)o; }

        private static string Bytes(double b) {
            if (b < 1024 * 1024) return (b / 1024).ToString("0") + " KB";
            if (b < 1024.0 * 1024 * 1024) return (b / (1024 * 1024)).ToString("0.0") + " MB";
            return (b / (1024.0 * 1024 * 1024)).ToString("0.00") + " GB";
        }

        private static string Uptime(double sec) {
            TimeSpan t = TimeSpan.FromSeconds(sec);
            if (t.TotalHours >= 1) return string.Format("{0}h {1:00}m", (int)t.TotalHours, t.Minutes);
            return string.Format("{0}m {1:00}s", t.Minutes, t.Seconds);
        }

        private static string MbpsText(double v) { return v >= 100 ? v.ToString("0") : v.ToString("0.0"); }

        private Color ColorFor(string ip) {
            int idx;
            if (!colorByIp.TryGetValue(ip, out idx)) { idx = colorByIp.Count; colorByIp[ip] = idx; }
            return Theme.Palette[idx % Theme.Palette.Length];
        }

        private static void AnimateDash(System.Windows.Shapes.Path p) {
            DoubleAnimation a = new DoubleAnimation(0, -7, new Duration(TimeSpan.FromSeconds(0.9))) { RepeatBehavior = RepeatBehavior.Forever };
            p.BeginAnimation(Shape.StrokeDashOffsetProperty, a);
        }

        private void SetBanner(string msg, bool warn) {
            if (string.IsNullOrEmpty(msg)) { banner.Visibility = Visibility.Collapsed; return; }
            banner.Background = warn ? Theme.Br(69, 26, 3) : Theme.Br(8, 47, 73);
            bannerText.Foreground = warn ? Theme.Br(253, 186, 116) : Theme.Br(125, 211, 252);
            bannerText.Text = msg;
            banner.Visibility = Visibility.Visible;
        }

        private void SetOffline() {
            dotStatus.Fill = Theme.Red;
            txtStatus.Text = "Engine offline";
            totalTarget = 0; anyTraffic = false;
            foreach (DeviceNode n in nodes) { n.SpeedMbps = 0; n.Share = 0; }
            ApplyFlowStyles();
        }

        // ---------------- view update ----------------
        private void UpdateView(Dictionary<string, object> d, Dictionary<string, double[]> sp) {
            IList devices = d["devices"] as IList ?? new ArrayList();
            int pooled = (int)Num(d["pooledCount"]);
            double total = 0;

            // status pill
            if (devices.Count == 0) { dotStatus.Fill = Theme.Red; txtStatus.Text = "No network adapters"; }
            else if (pooled >= 2) { dotStatus.Fill = Theme.Green; txtStatus.Text = pooled + " adapters pooled"; }
            else { dotStatus.Fill = Theme.Amber; txtStatus.Text = "1 adapter - waiting for more"; }

            // system proxy switch
            if (DateTime.UtcNow > proxyPendingUntil) {
                bool on = Bool(d["systemProxyEnabled"]);
                proxySwitch.SetOn(on);
                txtProxy.Text = "Boost all apps"; boostOn = on;
                txtProxy.Foreground = on ? Theme.Text : Theme.Muted;
            }

            // engine problems (port conflicts)
            Dictionary<string, object> eng = d["engine"] as Dictionary<string, object>;
            IList perr = eng != null ? eng["portErrors"] as IList : null;
            if (perr != null && perr.Count > 0) SetBanner(Str(perr[0]), true);
            else if (banner.Visibility == Visibility.Visible && bannerText.Text.IndexOf("is in use") >= 0) SetBanner(null, false);

            // sync device nodes
            HashSet<string> seen = new HashSet<string>();
            List<DeviceNode> ordered = new List<DeviceNode>();
            foreach (object o in devices) {
                Dictionary<string, object> dev = (Dictionary<string, object>)o;
                string ip = Str(dev["ip"]);
                seen.Add(ip);
                DeviceNode n;
                if (!nodeByIp.TryGetValue(ip, out n)) { n = CreateNode(ip, Str(dev["name"])); nodeByIp[ip] = n; }
                ordered.Add(n);

                string status = Str(dev["status"]);
                n.Enabled = Bool(dev["enabled"]);
                double down = Num(dev["downMbps"]), up = Num(dev["upMbps"]);
                double[] meas;
                if (sp != null && sp.TryGetValue(ip, out meas)) { down = meas[0]; up = meas[1]; }
                n.Down = down; n.Up = up; n.SpeedMbps = down + up;
                if (status == "active" || status == "idle") status = n.SpeedMbps > 0.05 ? "active" : "idle";
                colorByName[Str(dev["name"])] = n.Accent;
                n.Name.Text = Str(dev["name"]);
                n.Icon.Text = GlyphFor(Str(dev["icon"]));
                n.Sub.Text = Str(dev["type"]) + "  •  " + ip;
                n.Speed.Text = MbpsText(n.SpeedMbps) + " Mbps";
                n.Rate.Text = "↓ " + MbpsText(down) + "   ↑ " + MbpsText(up);
                n.Sw.SetOn(n.Enabled);
                n.Card.Opacity = n.Enabled ? 1.0 : 0.65;
                switch (status) {
                    case "active": n.Status.Text = "Active"; n.Status.Foreground = Theme.Green; break;
                    case "idle": n.Status.Text = "Idle"; n.Status.Foreground = Theme.Muted; break;
                    case "resting": n.Status.Text = "Resting"; n.Status.Foreground = Theme.Amber; break;
                    default: n.Status.Text = "Off"; n.Status.Foreground = Theme.Dim; break;
                }
                if (Bool(dev["isPrimary"]) && status != "resting") n.Status.Text += " • Primary";
            }
            List<string> gone = new List<string>();
            foreach (KeyValuePair<string, DeviceNode> kv in nodeByIp) if (!seen.Contains(kv.Key)) gone.Add(kv.Key);
            foreach (string ip in gone) RemoveNode(nodeByIp[ip]);
            nodes.Clear(); nodes.AddRange(ordered);

            // The pool total counts only adapters that are switched on; every adapter still shows its own speed.
            double sumDown = 0, sumUp = 0;
            foreach (DeviceNode nn in nodes) if (nn.Enabled) { sumDown += nn.Down; sumUp += nn.Up; }
            total = sumDown + sumUp;
            foreach (DeviceNode nn in nodes) nn.Share = (nn.Enabled && total > 0.05) ? nn.SpeedMbps / total : 0;
            if (total > peakSeen) peakSeen = total;

            // hub
            totalTarget = total;
            anyTraffic = total > 0.05;
            txtMBs.Text = (total / 8).ToString("0.0") + " MB/s";
            if (devices.Count == 0) txtCaption.Text = "";
            else if (pooled >= 2 && boostOn) txtCaption.Text = "Spreading your internet across " + pooled + " connections";
            else if (pooled >= 2) txtCaption.Text = "Switch on \"Boost all apps\" to start combining your connections";
            else if (devices.Count >= 2) txtCaption.Text = "Switch on at least two adapters to combine them";
            else txtCaption.Text = "Plug in a phone (USB tethering) or join another network to start pooling";
            txtEmpty.Visibility = devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ghostOn = devices.Count == 1;
            ghostCard.Visibility = ghostFlow.Visibility = ghostOn ? Visibility.Visible : Visibility.Collapsed;

            // stats
            stDown.Text = MbpsText(sumDown) + " Mbps";
            stUp.Text = MbpsText(sumUp) + " Mbps";
            stSession.Text = Bytes(Num(d["totalBytesTransferred"]));
            stPeak.Text = MbpsText(peakSeen) + " Mbps";
            stStreams.Text = ((int)Num(d["activeSockets"])).ToString();
            stUptime.Text = Uptime(Num(d["uptimeSec"]));

            UpdateFeed(d["recentConnections"] as IList);
            Layout();
            ApplyFlowStyles();
        }

        private static string GlyphFor(string icon) {
            switch (icon) {
                case "wifi": return "";
                case "ethernet": return "";
                case "phone": return "";
                default: return "";
            }
        }

        private DeviceNode CreateNode(string ip, string name) {
            DeviceNode n = new DeviceNode { Ip = ip, Accent = ColorFor(ip) };

            Border card = new Border { CornerRadius = new CornerRadius(10), Background = Theme.Card, BorderBrush = Theme.Line, BorderThickness = new Thickness(1), Padding = new Thickness(12, 8, 12, 8), ClipToBounds = true };
            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            n.IconBox = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(8), Background = Theme.Tint(n.Accent, 38), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            n.Icon = new TextBlock { FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 15, Foreground = new SolidColorBrush(n.Accent), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            n.IconBox.Child = n.Icon;
            Grid.SetRowSpan(n.IconBox, 2);
            g.Children.Add(n.IconBox);

            StackPanel top = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            n.Name = Tb(name, 13, Theme.Text, FontWeights.SemiBold); n.Name.TextTrimming = TextTrimming.CharacterEllipsis;
            n.Sub = Tb("", 10.5, Theme.Dim, null); n.Sub.TextTrimming = TextTrimming.CharacterEllipsis;
            top.Children.Add(n.Name); top.Children.Add(n.Sub);
            Grid.SetColumn(top, 1); g.Children.Add(top);

            n.Sw = new Toggle { VerticalAlignment = VerticalAlignment.Center, ToolTip = "Include this adapter in the pool" };
            string captured = ip;
            n.Sw.Clicked = delegate(bool want) { SetAdapter(captured, want); };
            Grid.SetColumn(n.Sw, 2); g.Children.Add(n.Sw);

            Grid bottom = new Grid { VerticalAlignment = VerticalAlignment.Center };
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            n.Speed = Tb("0.0 Mbps", 13, new SolidColorBrush(n.Accent), FontWeights.Bold);
            n.Rate = Tb("", 10.5, Theme.Muted, null); n.Rate.Margin = new Thickness(10, 2, 0, 0);
            n.Status = Tb("", 10.5, Theme.Muted, FontWeights.Medium); n.Status.HorizontalAlignment = HorizontalAlignment.Right; n.Status.Margin = new Thickness(0, 2, 0, 0);
            StackPanel left = new StackPanel { Orientation = Orientation.Horizontal };
            left.Children.Add(n.Speed); left.Children.Add(n.Rate);
            bottom.Children.Add(left);
            Grid.SetColumn(n.Status, 1); bottom.Children.Add(n.Status);
            Grid.SetRow(bottom, 1); Grid.SetColumnSpan(bottom, 2); Grid.SetColumn(bottom, 1);
            g.Children.Add(bottom);

            card.Child = g;
            n.Card = card;
            cardLayer.Children.Add(card);

            n.Flow = new System.Windows.Shapes.Path { Stroke = Theme.Line2, StrokeThickness = 1.5, StrokeDashArray = new DoubleCollection(new double[] { 3, 4 }), StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
            AnimateDash(n.Flow);
            flowLayer.Children.Add(n.Flow);

            n.Arc = new System.Windows.Shapes.Path { Stroke = new SolidColorBrush(n.Accent), StrokeThickness = 14, StrokeStartLineCap = PenLineCap.Flat, StrokeEndLineCap = PenLineCap.Flat };
            hubLayer.Children.Insert(2, n.Arc); // above track/disc, below centre text
            return n;
        }

        private void RemoveNode(DeviceNode n) {
            cardLayer.Children.Remove(n.Card);
            flowLayer.Children.Remove(n.Flow);
            hubLayer.Children.Remove(n.Arc);
            nodeByIp.Remove(n.Ip);
        }

        private void UpdateFeed(IList list) {
            if (list == null || list.Count == 0) {
                if (feed.Children.Count == 0 || lastFeedId != -1) {
                    feed.Children.Clear();
                    feed.Children.Add(Tb("Nothing to show yet. Switch on \"Boost all apps\" (top right) and use the internet as usual.", 11.5, Theme.Dim, null));
                    lastFeedId = -1;
                }
                return;
            }
            long topId = (long)Num(((Dictionary<string, object>)list[0])["id"]);
            if (topId == lastFeedId) return;
            lastFeedId = topId;
            feed.Children.Clear();
            int shown = 0;
            foreach (object o in list) {
                if (shown++ >= 30) break;
                Dictionary<string, object> c = (Dictionary<string, object>)o;
                Grid row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                FontFamily mono = new FontFamily("Consolas");
                TextBlock t1 = Tb(Str(c["time"]), 11, Theme.Dim, null); t1.FontFamily = mono;
                TextBlock t2 = Tb(Str(c["proto"]), 11, Theme.Muted, FontWeights.SemiBold); t2.FontFamily = mono;
                TextBlock t3 = Tb(Str(c["host"]) + ":" + Str(c["port"]), 11, Theme.Text2, null); t3.FontFamily = mono; t3.TextTrimming = TextTrimming.CharacterEllipsis;
                string via = Str(c["adapter"]);
                Color col;
                TextBlock t4 = Tb("via " + via, 11, colorByName.TryGetValue(via, out col) ? (Brush)new SolidColorBrush(col) : Theme.Muted, FontWeights.SemiBold);
                Grid.SetColumn(t2, 1); Grid.SetColumn(t3, 2); Grid.SetColumn(t4, 3);
                row.Children.Add(t1); row.Children.Add(t2); row.Children.Add(t3); row.Children.Add(t4);
                feed.Children.Add(row);
            }
        }

        // ---------------- layout of the hub-and-spoke stage ----------------
        private static Point PtOnCircle(double cx, double cy, double r, double deg) {
            double a = deg * Math.PI / 180.0;
            return new Point(cx + r * Math.Cos(a), cy + r * Math.Sin(a));
        }

        private static Geometry ArcGeo(double cx, double cy, double r, double a0, double a1) {
            double span = a1 - a0;
            if (span <= 0.05) return Geometry.Empty;
            if (span >= 359.5) return new EllipseGeometry(new Point(cx, cy), r, r);
            PathFigure fig = new PathFigure { StartPoint = PtOnCircle(cx, cy, r, a0), IsClosed = false };
            fig.Segments.Add(new ArcSegment(PtOnCircle(cx, cy, r, a1), new Size(r, r), 0, span > 180, SweepDirection.Clockwise, true));
            PathGeometry pg = new PathGeometry();
            pg.Figures.Add(fig);
            return pg;
        }

        private static void Place(UIElement e, double x, double y) { Canvas.SetLeft(e, x); Canvas.SetTop(e, y); }

        private void Layout() {
            double W = stage.ActualWidth, H = stage.ActualHeight;
            if (W < 300 || H < 180) return;
            int n = nodes.Count;
            int slots = n + (ghostOn ? 1 : 0);
            double margin = 24, netD = 76, gap = 10;
            double cardW = Math.Max(230, Math.Min(310, W * 0.32));
            double cardH = slots == 0 ? 80 : Math.Max(52, Math.Min(86, (H - 2 * 14 - (slots - 1) * gap) / slots));
            double total = slots * cardH + Math.Max(0, slots - 1) * gap;
            double top = Math.Max(8, (H - total) / 2);

            double leftEdge = margin + cardW, netLeft = W - margin - netD;
            double half = (netLeft - leftEdge) / 2;
            double R = Math.Max(60, Math.Min(Math.Min(H * 0.36, 150), half * 0.66));
            double cx = (leftEdge + netLeft) / 2, cy = H / 2 - 12;
            const double thick = 14;
            double r = R - thick / 2;

            hubTrack.Width = hubTrack.Height = 2 * r; hubTrack.StrokeThickness = thick; Place(hubTrack, cx - r, cy - r);
            double inner = (R - thick - 4);
            hubDisc.Width = hubDisc.Height = 2 * (R - thick / 2); Place(hubDisc, cx - r, cy - r);
            hubCenter.Width = hubCenter.Height = 2 * inner; Place(hubCenter, cx - inner, cy - inner);
            txtTotal.FontSize = Math.Max(24, Math.Min(46, R * 0.34));
            txtCaption.Width = Math.Max(160, Math.Min(2 * R + 80, half * 2 - 10));
            Place(txtCaption, cx - txtCaption.Width / 2, cy + R + 14);

            Place(netNode, netLeft, cy - netD / 2);
            Place(netLabel, netLeft, cy + netD / 2 + 6);
            PathFigure of = new PathFigure { StartPoint = new Point(cx + R, cy), IsClosed = false };
            of.Segments.Add(new LineSegment(new Point(netLeft, cy), true));
            PathGeometry opg = new PathGeometry(); opg.Figures.Add(of);
            outFlow.Data = opg;

            Place(txtEmpty, margin, H / 2 - 20);

            double angle = -90;
            for (int i = 0; i < n; i++) {
                DeviceNode d = nodes[i];
                double y = top + i * (cardH + gap);
                d.Card.Width = cardW; d.Card.Height = cardH;
                Place(d.Card, margin, y);

                double sx = margin + cardW, sy = y + cardH / 2;
                double phi = slots == 1 ? 0 : -52 + 104.0 * i / (slots - 1);
                Point end = PtOnCircle(cx, cy, R, 180 + phi); // left side of the hub
                end = new Point(cx - R * Math.Cos(phi * Math.PI / 180.0), cy + R * Math.Sin(phi * Math.PI / 180.0));
                double dx = (end.X - sx) * 0.55;
                PathFigure f = new PathFigure { StartPoint = new Point(sx, sy), IsClosed = false };
                f.Segments.Add(new BezierSegment(new Point(sx + dx, sy), new Point(end.X - dx, end.Y), end, true));
                PathGeometry pg = new PathGeometry(); pg.Figures.Add(f);
                d.Flow.Data = pg;

                double span = d.Share * 360.0;
                int segs = 0;
                foreach (DeviceNode o in nodes) if (o.Share > 0.001) segs++;
                double gapDeg = segs > 1 ? 2.0 : 0.0;
                if (d.Share > 0.001) {
                    d.Arc.Data = ArcGeo(cx, cy, r, angle + gapDeg / 2, angle + span - gapDeg / 2);
                    d.Arc.StrokeThickness = thick;
                    angle += span;
                } else {
                    d.Arc.Data = Geometry.Empty;
                }
            }
            if (ghostOn) LayoutGhost(margin, cardW, cardH, top, gap, cx, cy, R, slots, n);
        }

        private void LayoutGhost(double margin, double cardW, double cardH, double top, double gap, double cx, double cy, double R, int slots, int idx) {
            double y = top + idx * (cardH + gap);
            ghostCard.Width = cardW; ghostCard.Height = cardH;
            Place(ghostCard, margin, y);
            double sx = margin + cardW, sy = y + cardH / 2;
            double phi = slots == 1 ? 0 : -52 + 104.0 * idx / (slots - 1);
            Point end = new Point(cx - R * Math.Cos(phi * Math.PI / 180.0), cy + R * Math.Sin(phi * Math.PI / 180.0));
            double dx = (end.X - sx) * 0.55;
            PathFigure f = new PathFigure { StartPoint = new Point(sx, sy), IsClosed = false };
            f.Segments.Add(new BezierSegment(new Point(sx + dx, sy), new Point(end.X - dx, end.Y), end, true));
            PathGeometry pg = new PathGeometry(); pg.Figures.Add(f);
            ghostFlow.Data = pg;
        }

        private void ApplyFlowStyles() {
            foreach (DeviceNode d in nodes) {
                bool active = d.Enabled && d.SpeedMbps > 0.05;
                d.Flow.Stroke = active ? new SolidColorBrush(d.Accent) : Theme.Line2;
                d.Flow.StrokeThickness = active ? 2 + 2.5 * d.Share : 1.5;
                d.Flow.Opacity = active ? 1.0 : (d.Enabled ? 0.7 : 0.25);
            }
            outFlow.Stroke = anyTraffic ? Theme.Text2 : Theme.Line2;
            outFlow.StrokeThickness = anyTraffic ? 3 : 2;
            outFlow.Opacity = anyTraffic ? 0.9 : 0.7;
            hubTrack.Stroke = Theme.Line;
            netNode.BorderBrush = anyTraffic ? Theme.Text2 : Theme.Line2;
        }

        // smooth count-up/down of the big number
        private void OnRender(object sender, EventArgs e) {
            if (totalShown == totalTarget) return;
            totalShown += (totalTarget - totalShown) * 0.2;
            if (Math.Abs(totalTarget - totalShown) < 0.02) totalShown = totalTarget;
            txtTotal.Text = MbpsText(totalShown);
        }

        // ---------------- shutdown ----------------
        public void Cleanup() {
            if (cleaned) return;
            cleaned = true;
            try { timer.Stop(); } catch { }
            engine.Stop();
        }

        protected override void OnClosing(CancelEventArgs e) {
            // Hide immediately so closing feels instant; the proxy is restored in the background of the close.
            try { Hide(); } catch { }
            Cleanup();
            base.OnClosing(e);
        }
    }

    class Program {
        static MainWindow win;

        static void Log(string text) {
            try {
                string dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HydraBond");
                System.IO.Directory.CreateDirectory(dir);
                System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "app-crash.log"), DateTime.Now.ToString("s") + " " + text + Environment.NewLine);
            } catch { }
        }

        [STAThread]
        static void Main() {
            bool created;
            using (Mutex single = new Mutex(true, "Local\\HydraBond.App.SingleInstance", out created)) {
                if (!created) return;

                // Last-chance paths: make sure Boost never outlives the app.
                AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs ex) {
                    Log(Convert.ToString(ex.ExceptionObject));
                    try { if (win != null) win.Cleanup(); } catch { }
                };
                try {
                    Application app = new Application();
                    app.SessionEnding += delegate { try { if (win != null) win.Cleanup(); } catch { } };
                    app.Exit += delegate { try { if (win != null) win.Cleanup(); } catch { } };
                    app.DispatcherUnhandledException += delegate(object s, DispatcherUnhandledExceptionEventArgs ex) {
                        Log(ex.Exception.ToString());
                        ex.Handled = true;
                    };
                    win = new MainWindow();
                    app.Run(win);
                } catch (Exception ex) {
                    Log(ex.ToString());
                    try { if (win != null) win.Cleanup(); } catch { }
                }
            }
        }
    }
}
