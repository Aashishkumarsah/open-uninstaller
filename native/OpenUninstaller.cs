// Open Uninstaller - native Windows desktop application (WinForms, C# 5 / .NET 4.x)
// Compiles with the csc.exe built into Windows. All data/actions come from the
// local JSON bridge (src/bridge.js) backed by Node + PowerShell - no browser.
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace OpenUninstaller
{
    internal static class Bridge
    {
        private static readonly JavaScriptSerializer Ser = CreateSerializer();
        public static string Root { get { return Path.GetDirectoryName(Application.ExecutablePath); } }
        public static string BridgePath { get { return Path.Combine(Root, "src", "bridge.js"); } }
        public static string LogPath { get { return Path.Combine(Root, "data", "app.log"); } }
        public static string BackupsPath { get { return Path.Combine(Root, "data", "backups"); } }
        public static string ScreenshotsPath { get { return Path.Combine(Root, "data", "screenshots"); } }

        private static JavaScriptSerializer CreateSerializer()
        {
            var s = new JavaScriptSerializer();
            s.MaxJsonLength = int.MaxValue;
            return s;
        }

        public static void Log(string text)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath));
                File.AppendAllText(LogPath, DateTime.Now.ToString("s") + " " + text + Environment.NewLine);
            }
            catch (Exception) { }
        }

        public static object Call(string kind, string name, object body)
        {
            var sw = Stopwatch.StartNew();
            var psi = new ProcessStartInfo();
            psi.FileName = "node";
            psi.Arguments = "\"" + BridgePath + "\" " + kind + " " + name + " -";
            psi.RedirectStandardInput = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;

            using (var p = Process.Start(psi))
            {
                string payload = body == null ? "{}" : Ser.Serialize(body);
                using (var swIn = new StreamWriter(p.StandardInput.BaseStream, new UTF8Encoding(false)))
                {
                    swIn.Write(payload);
                    swIn.Flush();
                }

                var outTask = p.StandardOutput.ReadToEndAsync();
                var errTask = p.StandardError.ReadToEndAsync();
                p.WaitForExit();
                string outp = outTask.Result;
                string err = errTask.Result;
                sw.Stop();

                if (string.IsNullOrWhiteSpace(outp))
                {
                    Log("BRIDGE FAIL " + kind + ":" + name + " (" + sw.ElapsedMilliseconds + "ms) empty output: " + err);
                    throw new Exception("Bridge returned no output. " + err);
                }
                var res = Ser.DeserializeObject(outp) as Dictionary<string, object>;
                if (res == null)
                {
                    Log("BRIDGE FAIL " + kind + ":" + name + " invalid json");
                    throw new Exception("Bridge returned invalid JSON: " + outp.Substring(0, Math.Min(200, outp.Length)));
                }
                if (res.ContainsKey("ok") && !(bool)res["ok"])
                {
                    string msg = res.ContainsKey("error") ? Convert.ToString(res["error"]) : "unknown bridge error";
                    Log("BRIDGE ERR " + kind + ":" + name + " (" + sw.ElapsedMilliseconds + "ms): " + msg);
                    throw new Exception(msg);
                }
                object data = res.ContainsKey("data") ? res["data"] : null;
                var arr = data as object[];
                Log("BRIDGE OK " + kind + ":" + name + " (" + sw.ElapsedMilliseconds + "ms)" + (arr != null ? " rows=" + arr.Length : ""));
                return data;
            }
        }

        public static string Dump(object o) { return Ser.Serialize(o); }

        public static string PrettyDump(object o)
        {
            string raw = Ser.Serialize(o);
            var sb = new StringBuilder();
            int indent = 0;
            bool inStr = false;
            for (int i = 0; i < raw.Length; i++)
            {
                char c = raw[i];
                if (c == '"' && (i == 0 || raw[i - 1] != '\\')) inStr = !inStr;
                if (!inStr)
                {
                    if (c == '{' || c == '[')
                    {
                        sb.Append(c).Append(Environment.NewLine).Append(new string(' ', (++indent) * 2));
                        continue;
                    }
                    if (c == '}' || c == ']')
                    {
                        sb.Append(Environment.NewLine).Append(new string(' ', (--indent) * 2)).Append(c);
                        continue;
                    }
                    if (c == ',')
                    {
                        sb.Append(c).Append(Environment.NewLine).Append(new string(' ', indent * 2));
                        continue;
                    }
                    if (c == ':')
                    {
                        sb.Append(": ");
                        continue;
                    }
                }
                sb.Append(c);
            }
            return sb.ToString();
        }
    }

    internal static class Win32
    {
        [DllImport("user32.dll")]
        public static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        public static extern IntPtr WindowFromPoint(POINT point);

        [DllImport("user32.dll")]
        public static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X; public int Y; }

        public static bool SplitCommand(string raw, out string exe, out string args)
        {
            exe = "";
            args = "";
            if (string.IsNullOrWhiteSpace(raw)) return false;
            string s = Environment.ExpandEnvironmentVariables(raw.Trim());

            if (s.StartsWith("\""))
            {
                int end = s.IndexOf('"', 1);
                if (end > 1)
                {
                    exe = s.Substring(1, end - 1).Trim();
                    args = s.Substring(end + 1).Trim();
                    return !string.IsNullOrEmpty(exe);
                }
            }

            string lower = s.ToLowerInvariant();
            int idx = lower.IndexOf(".exe");
            while (idx >= 0)
            {
                int candEnd = idx + 4;
                if (candEnd == s.Length || char.IsWhiteSpace(s[candEnd]))
                {
                    string candidate = s.Substring(0, candEnd).Trim();
                    if (File.Exists(candidate) || candEnd == s.Length || candidate.IndexOf('\\') < 0)
                    {
                        exe = candidate;
                        args = candEnd < s.Length ? s.Substring(candEnd).Trim() : "";
                        return true;
                    }
                }
                idx = lower.IndexOf(".exe", idx + 4);
            }

            int sp = s.IndexOf(' ');
            if (sp > 0)
            {
                exe = s.Substring(0, sp).Trim();
                args = s.Substring(sp + 1).Trim();
            }
            else
            {
                exe = s;
                args = "";
            }
            return !string.IsNullOrEmpty(exe);
        }

        public static void OpenInRegEdit(string hivePath, string subKey)
        {
            string h = (hivePath ?? "").Trim();
            if (h.StartsWith("HKLM:\\", StringComparison.OrdinalIgnoreCase)) h = "HKEY_LOCAL_MACHINE\\" + h.Substring(6);
            else if (h.StartsWith("HKCU:\\", StringComparison.OrdinalIgnoreCase)) h = "HKEY_CURRENT_USER\\" + h.Substring(6);
            else if (h.StartsWith("HKLM\\", StringComparison.OrdinalIgnoreCase)) h = "HKEY_LOCAL_MACHINE\\" + h.Substring(5);
            else if (h.StartsWith("HKCU\\", StringComparison.OrdinalIgnoreCase)) h = "HKEY_CURRENT_USER\\" + h.Substring(5);
            string full = string.IsNullOrEmpty(subKey) ? h : (h + "\\" + subKey);
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Applets\Regedit", "LastKey", full);
            }
            catch { }
            Process.Start("regedit.exe");
        }
    }

    internal class ThemePalette
    {
        public string Name;
        public Color BgApp;
        public Color BgSidebar;
        public Color BgHeader;
        public Color BgToolbar;
        public Color BgCard;
        public Color BgGrid;
        public Color BgGridAlt;
        public Color BgGridHeader;
        public Color BgInput;
        public Color Border;
        public Color TextPrimary;
        public Color TextMuted;
        public Color AccentGreen;
        public Color AccentBlue;
        public Color AccentAmber;
        public Color AccentRed;
        public Color SelectionBg;
        public Color SelectionFg;
        public Color BtnFore;

        public static ThemePalette Get(string name)
        {
            string n = (name ?? "dark").ToLowerInvariant();
            if (n == "light")
            {
                return new ThemePalette
                {
                    Name = "light",
                    BgApp = Color.FromArgb(241, 245, 249),
                    BgSidebar = Color.FromArgb(226, 232, 240),
                    BgHeader = Color.FromArgb(255, 255, 255),
                    BgToolbar = Color.FromArgb(248, 250, 252),
                    BgCard = Color.FromArgb(255, 255, 255),
                    BgGrid = Color.FromArgb(255, 255, 255),
                    BgGridAlt = Color.FromArgb(248, 250, 252),
                    BgGridHeader = Color.FromArgb(226, 232, 240),
                    BgInput = Color.FromArgb(255, 255, 255),
                    Border = Color.FromArgb(203, 213, 225),
                    TextPrimary = Color.FromArgb(15, 23, 42),
                    TextMuted = Color.FromArgb(71, 85, 105),
                    AccentGreen = Color.FromArgb(34, 197, 94),
                    AccentBlue = Color.FromArgb(14, 165, 233),
                    AccentAmber = Color.FromArgb(245, 158, 11),
                    AccentRed = Color.FromArgb(239, 68, 68),
                    SelectionBg = Color.FromArgb(219, 234, 254),
                    SelectionFg = Color.FromArgb(15, 23, 42),
                    BtnFore = Color.FromArgb(8, 16, 10),
                };
            }
            if (n == "midnight")
            {
                return new ThemePalette
                {
                    Name = "midnight",
                    BgApp = Color.FromArgb(10, 12, 18),
                    BgSidebar = Color.FromArgb(14, 16, 24),
                    BgHeader = Color.FromArgb(16, 19, 28),
                    BgToolbar = Color.FromArgb(18, 21, 31),
                    BgCard = Color.FromArgb(20, 24, 36),
                    BgGrid = Color.FromArgb(16, 19, 28),
                    BgGridAlt = Color.FromArgb(22, 26, 38),
                    BgGridHeader = Color.FromArgb(13, 16, 25),
                    BgInput = Color.FromArgb(24, 29, 42),
                    Border = Color.FromArgb(38, 45, 62),
                    TextPrimary = Color.FromArgb(234, 238, 247),
                    TextMuted = Color.FromArgb(136, 148, 170),
                    AccentGreen = Color.FromArgb(52, 211, 153),
                    AccentBlue = Color.FromArgb(96, 165, 250),
                    AccentAmber = Color.FromArgb(251, 191, 36),
                    AccentRed = Color.FromArgb(248, 113, 113),
                    SelectionBg = Color.FromArgb(35, 58, 94),
                    SelectionFg = Color.White,
                    BtnFore = Color.FromArgb(8, 16, 10),
                };
            }
            return new ThemePalette
            {
                Name = "dark",
                BgApp = Color.FromArgb(15, 18, 24),
                BgSidebar = Color.FromArgb(20, 24, 33),
                BgHeader = Color.FromArgb(22, 26, 36),
                BgToolbar = Color.FromArgb(24, 28, 38),
                BgCard = Color.FromArgb(26, 31, 42),
                BgGrid = Color.FromArgb(24, 28, 37),
                BgGridAlt = Color.FromArgb(30, 35, 46),
                BgGridHeader = Color.FromArgb(19, 23, 31),
                BgInput = Color.FromArgb(32, 37, 48),
                Border = Color.FromArgb(46, 53, 68),
                TextPrimary = Color.FromArgb(232, 236, 242),
                TextMuted = Color.FromArgb(148, 160, 180),
                AccentGreen = Color.FromArgb(74, 222, 128),
                AccentBlue = Color.FromArgb(56, 189, 248),
                AccentAmber = Color.FromArgb(251, 191, 36),
                AccentRed = Color.FromArgb(248, 113, 113),
                SelectionBg = Color.FromArgb(37, 68, 102),
                SelectionFg = Color.White,
                BtnFore = Color.FromArgb(8, 16, 10),
            };
        }
    }

    internal static class IconCache
    {
        private static readonly Dictionary<string, Bitmap> Cache = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);

        private static string ResolveExeFromPathOrCommand(string hint, string fallbackDir)
        {
            string s = (hint ?? "").Trim();
            if (!string.IsNullOrEmpty(s))
            {
                if (s.StartsWith("\""))
                {
                    int q = s.IndexOf('"', 1);
                    if (q > 1) s = s.Substring(1, q - 1);
                }
                int comma = s.LastIndexOf(',');
                if (comma > 2)
                {
                    string tail = s.Substring(comma + 1).Trim();
                    int idxNum;
                    if (int.TryParse(tail, out idxNum)) s = s.Substring(0, comma).Trim();
                }
                s = Environment.ExpandEnvironmentVariables(s.Trim().Trim('"'));
                if (File.Exists(s)) return s;

                string exe;
                string args;
                if (Win32.SplitCommand(s, out exe, out args) && File.Exists(exe)) return exe;
            }

            if (!string.IsNullOrEmpty(fallbackDir) && Directory.Exists(fallbackDir))
            {
                try
                {
                    string[] exes = Directory.GetFiles(fallbackDir, "*.exe", SearchOption.TopDirectoryOnly);
                    foreach (var f in exes)
                    {
                        string bn = Path.GetFileName(f).ToLowerInvariant();
                        if (!bn.StartsWith("unins") && !bn.Contains("uninstall")) return f;
                    }
                    if (exes.Length > 0) return exes[0];
                }
                catch { }
            }
            return null;
        }

        private static Bitmap MakeLetterBadge(string label)
        {
            var bmp = new Bitmap(20, 20);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                string ch = "?";
                if (!string.IsNullOrWhiteSpace(label))
                {
                    char first = label.Trim()[0];
                    ch = char.ToUpperInvariant(first).ToString();
                }
                int hash = Math.Abs((label ?? "A").GetHashCode());
                Color c = Color.FromArgb(40 + (hash % 70), 85 + ((hash / 7) % 85), 115 + ((hash / 13) % 100));
                using (var br = new SolidBrush(c)) g.FillEllipse(br, 1, 1, 18, 18);
                using (var f = new Font("Segoe UI", 8.2f, FontStyle.Bold))
                using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                {
                    g.DrawString(ch, f, Brushes.White, new RectangleF(0, 0, 20, 20), sf);
                }
            }
            return bmp;
        }

        public static Bitmap GetIcon(string pathHint, string dirHint, string labelFallback)
        {
            string key = (pathHint ?? "") + "|" + (dirHint ?? "") + "|" + (labelFallback ?? "");
            Bitmap existing;
            if (Cache.TryGetValue(key, out existing)) return existing;

            Bitmap result = null;
            string resolved = ResolveExeFromPathOrCommand(pathHint, dirHint);
            if (!string.IsNullOrEmpty(resolved))
            {
                try
                {
                    using (var ico = Icon.ExtractAssociatedIcon(resolved))
                    {
                        if (ico != null)
                        {
                            result = new Bitmap(20, 20);
                            using (var g = Graphics.FromImage(result))
                            {
                                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                                g.DrawIcon(ico, new Rectangle(1, 1, 18, 18));
                            }
                        }
                    }
                }
                catch { result = null; }
            }
            if (result == null) result = MakeLetterBadge(labelFallback);
            Cache[key] = result;
            return result;
        }
    }

    internal class FlatTabControl : TabControl
    {
        public FlatTabControl()
        {
            Appearance = TabAppearance.FlatButtons;
            SizeMode = TabSizeMode.Fixed;
            Multiline = true;
            ItemSize = new Size(1, 1);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x1328 && !DesignMode)
            {
                m.Result = (IntPtr)1;
                return;
            }
            base.WndProc(ref m);
        }
    }

    internal class NavItemControl : Control
    {
        public int TabIndexTarget { get; set; }
        public string BadgeText { get; set; }
        public ThemePalette Theme { get; set; }
        public Color AccentColor { get; set; }
        private bool isActive;
        private bool isHover;

        public bool IsActive
        {
            get { return isActive; }
            set { isActive = value; Invalidate(); }
        }

        public NavItemControl(string text, int tabIndex, ThemePalette theme, Color accent)
        {
            Text = text;
            TabIndexTarget = tabIndex;
            Theme = theme;
            AccentColor = accent;
            BadgeText = "";
            Height = 32;
            Margin = new Padding(4, 1, 4, 1);
            Cursor = Cursors.Hand;
            DoubleBuffered = true;
            Font = new Font("Segoe UI", 9f);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            isHover = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            isHover = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var t = Theme ?? ThemePalette.Get("dark");
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(t.BgSidebar);

            Rectangle card = new Rectangle(4, 1, Width - 8, Height - 2);
            if (isActive)
            {
                using (var br = new SolidBrush(t.Name == "light" ? Color.FromArgb(207, 224, 245) : Color.FromArgb(35, 44, 60)))
                {
                    g.FillRectangle(br, card);
                }
                using (var bar = new SolidBrush(AccentColor))
                {
                    g.FillRectangle(bar, 4, 4, 3, Height - 8);
                }
            }
            else if (isHover)
            {
                using (var br = new SolidBrush(t.Name == "light" ? Color.FromArgb(215, 222, 232) : Color.FromArgb(28, 33, 44)))
                {
                    g.FillRectangle(br, card);
                }
            }

            Color fg = isActive ? t.TextPrimary : t.TextMuted;
            Font fontUse = isActive ? new Font("Segoe UI", 9f, FontStyle.Bold) : Font;
            int rightPad = string.IsNullOrEmpty(BadgeText) ? 10 : 48;
            TextRenderer.DrawText(g, Text, fontUse, new Rectangle(14, 0, Width - 14 - rightPad, Height), fg,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            if (isActive && fontUse != Font) fontUse.Dispose();

            if (!string.IsNullOrEmpty(BadgeText))
            {
                Rectangle badgeRect = new Rectangle(Width - 46, 7, 36, 18);
                using (var br = new SolidBrush(isActive ? AccentColor : (t.Name == "light" ? Color.FromArgb(203, 213, 225) : Color.FromArgb(38, 45, 58))))
                {
                    g.FillRectangle(br, badgeRect);
                }
                Color bFg = isActive ? t.BtnFore : t.TextMuted;
                using (var bf = new Font("Segoe UI", 7.6f, FontStyle.Bold))
                {
                    TextRenderer.DrawText(g, BadgeText, bf, badgeRect, bFg, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }
            }
        }
    }

    internal class KpiCardControl : Control
    {
        public string TitleText { get; set; }
        public string ValueText { get; set; }
        public string SubText { get; set; }
        public Color AccentColor { get; set; }
        public ThemePalette Theme { get; set; }
        private bool hover;

        public KpiCardControl(string title, string val, string sub, Color accent, ThemePalette theme)
        {
            TitleText = title;
            ValueText = val;
            SubText = sub;
            AccentColor = accent;
            Theme = theme;
            Cursor = Cursors.Hand;
            DoubleBuffered = true;
            Margin = new Padding(6);
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var t = Theme ?? ThemePalette.Get("dark");
            Graphics g = e.Graphics;
            g.Clear(t.BgApp);

            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var bg = new SolidBrush(t.BgCard)) g.FillRectangle(bg, r);
            using (var top = new SolidBrush(AccentColor)) g.FillRectangle(top, 0, 0, Width, 3);
            using (var pen = new Pen(hover ? AccentColor : t.Border, 1f)) g.DrawRectangle(pen, r);

            using (var tf = new Font("Segoe UI", 8.5f, FontStyle.Bold))
                TextRenderer.DrawText(g, TitleText, tf, new Rectangle(12, 10, Width - 24, 20), t.TextMuted, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            using (var vf = new Font("Segoe UI", 20f, FontStyle.Bold))
                TextRenderer.DrawText(g, ValueText ?? "0", vf, new Rectangle(10, 30, Width - 20, 36), t.TextPrimary, TextFormatFlags.NoPrefix);
            using (var sf = new Font("Segoe UI", 8.2f))
                TextRenderer.DrawText(g, SubText ?? "", sf, new Rectangle(12, Height - 24, Width - 24, 18), AccentColor, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }

    internal class DrivesBarPanel : Control
    {
        public object[] Drives { get; set; }
        public ThemePalette Theme { get; set; }

        public DrivesBarPanel()
        {
            DoubleBuffered = true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var t = Theme ?? ThemePalette.Get("dark");
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(t.BgCard);
            using (var pen = new Pen(t.Border, 1f)) g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);

            using (var hf = new Font("Segoe UI", 9.5f, FontStyle.Bold))
                TextRenderer.DrawText(g, "Local Storage Drives", hf, new Rectangle(14, 10, Width - 28, 22), t.TextPrimary);

            if (Drives == null || Drives.Length == 0)
            {
                TextRenderer.DrawText(g, "No drive information loaded yet.", Font, new Rectangle(14, 42, Width - 28, 22), t.TextMuted);
                return;
            }

            int y = 40;
            foreach (var o in Drives)
            {
                var d = o as Dictionary<string, object>;
                if (d == null || y + 36 > Height) continue;
                string drv = d.ContainsKey("Drive") ? Convert.ToString(d["Drive"]) : "";
                string label = d.ContainsKey("Label") ? Convert.ToString(d["Label"]) : "";
                string fs = d.ContainsKey("FileSystem") ? Convert.ToString(d["FileSystem"]) : "";
                double totalGb = d.ContainsKey("TotalGB") ? Convert.ToDouble(d["TotalGB"]) : 0;
                double freeGb = d.ContainsKey("FreeGB") ? Convert.ToDouble(d["FreeGB"]) : 0;
                double freePct = d.ContainsKey("FreePercent") ? Convert.ToDouble(d["FreePercent"]) : 0;
                double usedPct = Math.Max(0, Math.Min(100, 100.0 - freePct));

                string line = string.Format("{0} ({1}, {2})  —  {3:F1} GB free of {4:F1} GB ({5:F1}% free)", drv, label, fs, freeGb, totalGb, freePct);
                using (var lf = new Font("Segoe UI", 8.5f))
                    TextRenderer.DrawText(g, line, lf, new Rectangle(14, y, Width - 28, 18), t.TextPrimary, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

                Rectangle track = new Rectangle(14, y + 20, Math.Max(40, Width - 28), 8);
                using (var tb = new SolidBrush(t.BgGridAlt)) g.FillRectangle(tb, track);
                int fillW = (int)Math.Round(track.Width * (usedPct / 100.0));
                Color barColor = usedPct > 85 ? t.AccentRed : (usedPct > 65 ? t.AccentAmber : t.AccentBlue);
                using (var fb = new SolidBrush(barColor)) g.FillRectangle(fb, track.X, track.Y, Math.Max(2, fillW), track.Height);
                y += 38;
            }
        }
    }

    internal static class GridHelper
    {
        public static void Style(DataGridView g, ThemePalette t)
        {
            if (g == null) return;
            try
            {
                typeof(DataGridView).InvokeMember("DoubleBuffered",
                    BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.SetProperty,
                    null, g, new object[] { true });
            }
            catch { }

            g.BackgroundColor = t.BgGrid;
            g.GridColor = t.Border;
            g.BorderStyle = BorderStyle.None;
            g.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            g.DefaultCellStyle.BackColor = t.BgGrid;
            g.DefaultCellStyle.ForeColor = t.TextPrimary;
            g.DefaultCellStyle.SelectionBackColor = t.SelectionBg;
            g.DefaultCellStyle.SelectionForeColor = t.SelectionFg;
            g.AlternatingRowsDefaultCellStyle.BackColor = t.BgGridAlt;
            g.AlternatingRowsDefaultCellStyle.ForeColor = t.TextPrimary;
            g.ColumnHeadersDefaultCellStyle.BackColor = t.BgGridHeader;
            g.ColumnHeadersDefaultCellStyle.ForeColor = t.AccentGreen;
            g.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI", 8.8f, FontStyle.Bold);
            g.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
            g.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            g.ColumnHeadersHeight = 32;
            g.RowTemplate.Height = 28;
            g.EnableHeadersVisualStyles = false;
            g.ReadOnly = true;
            g.AllowUserToAddRows = false;
            g.AllowUserToDeleteRows = false;
            g.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            g.RowHeadersVisible = false;
            g.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            g.MultiSelect = false;
        }

        private static bool IsNumericObject(object v)
        {
            return (v is int || v is long || v is double || v is float || v is decimal);
        }

        public static void Fill(DataGridView g, object data, bool withCheckColumn, bool defaultChecked, string[] columnsOrder, Func<Dictionary<string, object>, Bitmap> iconSelector)
        {
            if (g == null) return;
            g.SuspendLayout();
            try
            {
                g.DataSource = null;
                g.Rows.Clear();
                g.Columns.Clear();

                var arr = data as object[];
                var first = (arr != null && arr.Length > 0) ? arr[0] as Dictionary<string, object> : null;
                if (first == null && (columnsOrder == null || columnsOrder.Length == 0)) return;

                if (withCheckColumn)
                {
                    g.Columns.Add(new DataGridViewCheckBoxColumn
                    {
                        Name = "Select",
                        HeaderText = "",
                        Width = 36,
                        AutoSizeMode = DataGridViewAutoSizeColumnMode.None,
                        ReadOnly = false,
                        SortMode = DataGridViewColumnSortMode.NotSortable,
                    });
                    g.ReadOnly = false;
                }
                else
                {
                    g.ReadOnly = true;
                }

                if (iconSelector != null)
                {
                    g.Columns.Add(new DataGridViewImageColumn
                    {
                        Name = "Icon",
                        HeaderText = "",
                        Width = 32,
                        AutoSizeMode = DataGridViewAutoSizeColumnMode.None,
                        ImageLayout = DataGridViewImageCellLayout.Normal,
                        SortMode = DataGridViewColumnSortMode.NotSortable,
                        ReadOnly = true,
                    });
                }

                string[] keys;
                if (columnsOrder != null && columnsOrder.Length > 0)
                {
                    if (first != null)
                    {
                        var present = new List<string>();
                        foreach (var c in columnsOrder)
                        {
                            if (first.ContainsKey(c)) present.Add(c);
                        }
                        keys = present.ToArray();
                    }
                    else
                    {
                        keys = columnsOrder;
                    }
                }
                else
                {
                    keys = first.Keys.ToArray();
                }

                foreach (var k in keys)
                {
                    var col = new DataGridViewTextBoxColumn
                    {
                        Name = k,
                        HeaderText = k,
                        ReadOnly = true,
                        SortMode = DataGridViewColumnSortMode.Automatic,
                    };
                    if (k == "SizeMB" || k == "SizeKB" || k == "ProcessId" || k == "id" || k == "TotalGB" || k == "FreeGB" || k == "FreePercent" || k == "ItemsCount")
                    {
                        col.FillWeight = 50;
                    }
                    else if (k == "Architecture" || k == "InstallDate" || k == "Status" || k == "Enabled" || k == "Kind" || k == "Drive")
                    {
                        col.FillWeight = 70;
                    }
                    else if (k == "Category" || k == "DisplayVersion")
                    {
                        col.FillWeight = 82;
                    }
                    g.Columns.Add(col);
                }

                if (arr == null) return;

                foreach (var item in arr)
                {
                    var d = item as Dictionary<string, object>;
                    var values = new List<object>();
                    if (withCheckColumn) values.Add(defaultChecked);
                    if (iconSelector != null) values.Add(iconSelector(d));
                    foreach (var k in keys)
                    {
                        object v = (d != null && d.ContainsKey(k)) ? d[k] : null;
                        if (v == null) values.Add("");
                        else if (IsNumericObject(v)) values.Add(v is decimal ? Convert.ToDouble(v) : v);
                        else if (v is object[] || v is Dictionary<string, object>) values.Add(Bridge.Dump(v));
                        else values.Add(v.ToString());
                    }
                    int idx = g.Rows.Add(values.ToArray());
                    g.Rows[idx].Tag = d;
                }
            }
            finally
            {
                g.ResumeLayout();
            }
        }

        public static string GetCell(DataGridView g, int row, string col)
        {
            if (g == null || row < 0 || row >= g.Rows.Count || !g.Columns.Contains(col)) return "";
            object val = g.Rows[row].Cells[col].Value;
            return val == null ? "" : val.ToString();
        }

        public static Dictionary<string, object> GetRowDict(DataGridView g, int row)
        {
            if (g == null || row < 0 || row >= g.Rows.Count) return null;
            return g.Rows[row].Tag as Dictionary<string, object>;
        }

        public static List<int> GetCheckedRows(DataGridView g)
        {
            var indices = new List<int>();
            if (g == null || !g.Columns.Contains("Select")) return indices;
            foreach (DataGridViewRow row in g.Rows)
            {
                object v = row.Cells["Select"].Value;
                if (v is bool && (bool)v) indices.Add(row.Index);
            }
            return indices;
        }

        public static void ToggleAllChecks(DataGridView g, bool value)
        {
            if (g == null || !g.Columns.Contains("Select")) return;
            foreach (DataGridViewRow r in g.Rows) r.Cells["Select"].Value = value;
        }

        public static void CollectCheckedRegCleanup(DataGridView gReg, out string[] keys, out Dictionary<string, object>[] regValues)
        {
            var keyList = new List<string>();
            var valList = new List<Dictionary<string, object>>();
            foreach (int i in GetCheckedRows(gReg))
            {
                string kind = GetCell(gReg, i, "Kind");
                string k = GetCell(gReg, i, "Key");
                if (string.IsNullOrEmpty(k)) continue;
                if (kind == "Value")
                {
                    string vn = GetCell(gReg, i, "ValueName");
                    if (!string.IsNullOrEmpty(vn))
                    {
                        valList.Add(new Dictionary<string, object> { { "Key", k }, { "ValueName", vn } });
                    }
                }
                else
                {
                    keyList.Add(k);
                }
            }
            keys = keyList.ToArray();
            regValues = valList.ToArray();
        }
    }

    internal class UninstallWizardForm : Form
    {
        private readonly Dictionary<string, object> program;
        private readonly bool quietMode;
        private readonly Action onCompleted;
        private readonly ThemePalette theme;
        private ComboBox cMode;
        private CheckBox chkBackup;
        private Button bStart, bSkip, bScan, bDelete, bSelectAll, bUnselectAll, bClose;
        private DataGridView gFs, gReg;
        private TextBox tLog;
        private Label lStepInfo, lModeDesc;
        private string displayName, publisher, version, registryKey, hivePath, installLoc, displayIcon;
        private string uninstallStr, quietUninstallStr;
        private bool windowsInstaller;

        public UninstallWizardForm(Dictionary<string, object> prog, bool quiet, string defaultMode, bool defaultBackup, ThemePalette pal, Action onDone)
        {
            program = prog ?? new Dictionary<string, object>();
            quietMode = quiet;
            theme = pal ?? ThemePalette.Get("dark");
            onCompleted = onDone;

            displayName = GetStr("DisplayName");
            publisher = GetStr("Publisher");
            version = GetStr("DisplayVersion");
            registryKey = GetStr("RegistryKey");
            hivePath = GetStr("HivePath");
            installLoc = GetStr("InstallLocation");
            displayIcon = GetStr("DisplayIcon");
            uninstallStr = GetStr("UninstallString");
            quietUninstallStr = GetStr("QuietUninstallString");
            windowsInstaller = program.ContainsKey("WindowsInstaller") && (program["WindowsInstaller"] is bool) && (bool)program["WindowsInstaller"];

            Text = "Guided Uninstall Wizard - " + displayName;
            Size = new Size(940, 700);
            MinimumSize = new Size(760, 560);
            StartPosition = FormStartPosition.CenterParent;
            BackColor = theme.BgApp;
            ForeColor = theme.TextPrimary;
            Font = new Font("Segoe UI", 9f);

            BuildUi(defaultMode, defaultBackup);
        }

        private string GetStr(string key)
        {
            return (program.ContainsKey(key) && program[key] != null) ? Convert.ToString(program[key]) : "";
        }

        private Button Btn(string text, Color c)
        {
            return new Button
            {
                Text = text,
                BackColor = c,
                ForeColor = theme.BtnFore,
                FlatStyle = FlatStyle.Flat,
                AutoSize = true,
                Height = 30,
                Padding = new Padding(8, 0, 8, 0),
                Margin = new Padding(3),
                Font = new Font("Segoe UI", 8.8f, FontStyle.Bold),
            };
        }

        private void BuildUi(string defaultMode, bool defaultBackup)
        {
            var header = new Panel { Dock = DockStyle.Top, Height = 86, BackColor = theme.BgHeader, Padding = new Padding(12) };
            string chosenCmd = (quietMode && !string.IsNullOrEmpty(quietUninstallStr)) ? quietUninstallStr : uninstallStr;
            lStepInfo = new Label
            {
                Dock = DockStyle.Fill,
                ForeColor = theme.TextPrimary,
                Text = string.Format(
                    "Program: {0}   (Version: {1}, Publisher: {2})\r\n" +
                    "Install Location: {3}\r\n" +
                    "Command ({4}): {5}",
                    displayName,
                    string.IsNullOrEmpty(version) ? "-" : version,
                    string.IsNullOrEmpty(publisher) ? "-" : publisher,
                    string.IsNullOrEmpty(installLoc) ? "-" : installLoc,
                    quietMode && !string.IsNullOrEmpty(quietUninstallStr) ? "Quiet" : "Standard",
                    string.IsNullOrEmpty(chosenCmd) ? "(none - use Skip to scan leftovers)" : chosenCmd
                ),
            };
            header.Controls.Add(lStepInfo);

            var controlsBar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, BackColor = theme.BgToolbar, Padding = new Padding(8), WrapContents = true };
            chkBackup = new CheckBox { Text = "Backup Registry key first", Checked = defaultBackup, AutoSize = true, ForeColor = theme.TextPrimary, Margin = new Padding(4, 7, 10, 3) };
            cMode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 105, BackColor = theme.BgInput, ForeColor = theme.TextPrimary, Margin = new Padding(3, 5, 8, 3) };
            cMode.Items.AddRange(new object[] { "built-in", "safe", "moderate", "advanced" });
            int mIdx = cMode.Items.IndexOf(string.IsNullOrEmpty(defaultMode) ? "moderate" : defaultMode);
            cMode.SelectedIndex = mIdx >= 0 ? mIdx : 2;

            bStart = Btn("1. Run Uninstaller", theme.AccentRed);
            bSkip = Btn("Skip -> Step 2", Color.FromArgb(148, 163, 184));
            bScan = Btn("2. Scan Leftovers", theme.AccentBlue);
            bSelectAll = Btn("Check All", Color.FromArgb(148, 163, 184));
            bUnselectAll = Btn("Uncheck All", Color.FromArgb(148, 163, 184));
            bDelete = Btn("3. Delete Checked Leftovers", theme.AccentGreen);
            bClose = Btn("Close", Color.FromArgb(203, 213, 225));
            bDelete.Enabled = false;

            controlsBar.Controls.AddRange(new Control[]
            {
                chkBackup,
                new Label { Text = "Scan Mode:", AutoSize = true, Padding = new Padding(2, 8, 2, 0), ForeColor = theme.TextMuted },
                cMode, bStart, bSkip, bScan, bSelectAll, bUnselectAll, bDelete, bClose
            });

            lModeDesc = new Label
            {
                Dock = DockStyle.Top,
                Height = 26,
                BackColor = theme.BgCard,
                ForeColor = theme.AccentBlue,
                Padding = new Padding(10, 5, 10, 0),
                Text = "",
            };
            Action updateModeDesc = () =>
            {
                string m = Convert.ToString(cMode.SelectedItem);
                if (m == "built-in") lModeDesc.Text = "Built-in Only: runs the vendor uninstaller without scanning for additional leftover files or registry entries.";
                else if (m == "safe") lModeDesc.Text = "Safe (Normal) Mode: runs uninstaller + scans direct install folders, shortcuts, Uninstall/Software keys & traced logs.";
                else if (m == "advanced") lModeDesc.Text = "Advanced Mode (Deep): Moderate scan + 3-level AppData, Temp, Prefetch, MuiCache, AppCompatFlags & Service paths.";
                else lModeDesc.Text = "Moderate Mode (Recommended): Safe scan + 2-level AppData, ProgramData, Software subkeys, App Paths & Startup Run values.";
            };
            cMode.SelectedIndexChanged += (s, e) => updateModeDesc();
            updateModeDesc();

            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 235 };
            gFs = new DataGridView { Dock = DockStyle.Fill };
            gReg = new DataGridView { Dock = DockStyle.Fill };
            GridHelper.Style(gFs, theme);
            GridHelper.Style(gReg, theme);
            split.Panel1.Controls.Add(gFs);
            split.Panel1.Controls.Add(new Label { Text = "Filesystem Leftovers Found:", Dock = DockStyle.Top, Height = 22, BackColor = theme.BgGridHeader, ForeColor = theme.AccentGreen, Padding = new Padding(6, 3, 0, 0) });
            split.Panel2.Controls.Add(gReg);
            split.Panel2.Controls.Add(new Label { Text = "Registry Leftovers Found (Keys & Values):", Dock = DockStyle.Top, Height = 22, BackColor = theme.BgGridHeader, ForeColor = theme.AccentBlue, Padding = new Padding(6, 3, 0, 0) });

            tLog = new TextBox
            {
                Dock = DockStyle.Bottom,
                Height = 96,
                Multiline = true,
                ReadOnly = true,
                BackColor = theme.BgGridHeader,
                ForeColor = theme.TextPrimary,
                ScrollBars = ScrollBars.Both,
                Font = new Font("Consolas", 8.8f),
            };

            Controls.Add(split);
            Controls.Add(tLog);
            Controls.Add(lModeDesc);
            Controls.Add(controlsBar);
            Controls.Add(header);

            bStart.Click += (s, e) => RunStep1Async();
            bSkip.Click += (s, e) => ScanLeftoversAsync();
            bScan.Click += (s, e) => ScanLeftoversAsync();
            bSelectAll.Click += (s, e) => { GridHelper.ToggleAllChecks(gFs, true); GridHelper.ToggleAllChecks(gReg, true); };
            bUnselectAll.Click += (s, e) => { GridHelper.ToggleAllChecks(gFs, false); GridHelper.ToggleAllChecks(gReg, false); };
            bDelete.Click += (s, e) => CleanLeftoversAsync();
            bClose.Click += (s, e) => Close();

            AppendLog("Ready. Click '1. Run Uninstaller' to run the built-in uninstaller with foreground UI, or 'Scan Leftovers' directly.");
        }

        private void AppendLog(string line)
        {
            tLog.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + line + Environment.NewLine);
        }

        private async void RunStep1Async()
        {
            bStart.Enabled = false;
            try
            {
                if (chkBackup.Checked && !string.IsNullOrEmpty(registryKey))
                {
                    AppendLog("Backing up Uninstall registry key...");
                    var bk = await Task.Run(() => Bridge.Call("action", "backup-key", new { name = displayName, hivePath = hivePath, registryKey = registryKey })) as Dictionary<string, object>;
                    AppendLog("Backup saved: " + (bk != null && bk.ContainsKey("file") ? Convert.ToString(bk["file"]) : "ok"));
                }

                string cmd = (quietMode && !string.IsNullOrEmpty(quietUninstallStr)) ? quietUninstallStr : uninstallStr;
                if (string.IsNullOrWhiteSpace(cmd))
                {
                    AppendLog("No UninstallString registered; proceeding to leftover scan.");
                    await ScanLeftoversInternalAsync();
                    return;
                }

                string exe, args;
                if (!Win32.SplitCommand(cmd, out exe, out args))
                {
                    AppendLog("Could not parse uninstall command: " + cmd);
                    return;
                }

                if (windowsInstaller || exe.ToLowerInvariant().Contains("msiexec"))
                {
                    if (args.IndexOf("/I", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        args = Regex.Replace(args, "/I", "/X", RegexOptions.IgnoreCase);
                    }
                    if (quietMode && args.IndexOf("/qn", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        args = args + " /qn /norestart";
                    }
                }

                AppendLog("Launching foreground uninstaller: " + exe + " " + args);
                int exitCode = -1;
                string launchErr = null;
                await Task.Run(() =>
                {
                    try
                    {
                        var psi = new ProcessStartInfo { FileName = exe, Arguments = args, UseShellExecute = true };
                        using (var p = Process.Start(psi))
                        {
                            if (p != null) { p.WaitForExit(); exitCode = p.ExitCode; }
                        }
                    }
                    catch (Exception ex) { launchErr = ex.Message; }
                });

                if (launchErr != null)
                {
                    AppendLog("Uninstaller launch failed: " + launchErr);
                    await Task.Run(() => Bridge.Call("action", "uninstall-record", new { name = displayName, detail = launchErr, status = "error" }));
                    return;
                }

                AppendLog("Uninstaller process exited (code " + exitCode + ").");
                await Task.Run(() => Bridge.Call("action", "uninstall-record", new { name = displayName, exitCode = exitCode, status = exitCode == 0 ? "ok" : "partial" }));
                if (onCompleted != null) onCompleted();
                await ScanLeftoversInternalAsync();
            }
            catch (Exception ex) { AppendLog("Error: " + ex.Message); }
            finally { bStart.Enabled = true; }
        }

        private async void ScanLeftoversAsync()
        {
            await ScanLeftoversInternalAsync();
        }

        private async Task ScanLeftoversInternalAsync()
        {
            string mode = Convert.ToString(cMode.SelectedItem);
            if (string.IsNullOrEmpty(mode)) mode = "moderate";
            if (mode == "built-in")
            {
                AppendLog("Built-in mode selected: no extra leftover scan run. Switch Scan Mode to safe, moderate, or advanced to scan.");
                GridHelper.Fill(gFs, null, true, true, null, null);
                GridHelper.Fill(gReg, null, true, true, null, null);
                bDelete.Enabled = false;
                return;
            }
            bScan.Enabled = false;
            try
            {
                AppendLog("Scanning leftovers (" + mode + " mode) for: " + displayName + "...");
                var payload = new
                {
                    pattern = displayName,
                    mode = mode,
                    displayName = displayName,
                    publisher = publisher,
                    installLocation = installLoc,
                    registryKey = registryKey,
                    hivePath = hivePath,
                    uninstallString = uninstallStr,
                    displayIcon = displayIcon
                };
                var d = await Task.Run(() => Bridge.Call("action", "leftovers", payload)) as Dictionary<string, object>;
                if (d != null)
                {
                    GridHelper.Fill(gFs, d.ContainsKey("filesystem") ? d["filesystem"] : null, true, true, null, null);
                    GridHelper.Fill(gReg, d.ContainsKey("registry") ? d["registry"] : null, true, true, null, null);
                }
                int total = gFs.Rows.Count + gReg.Rows.Count;
                AppendLog("Scan complete (" + mode + "): " + gFs.Rows.Count + " filesystem item(s), " + gReg.Rows.Count + " registry entry/entries.");
                bDelete.Enabled = total > 0;
            }
            catch (Exception ex) { AppendLog("Scan failed: " + ex.Message); }
            finally { bScan.Enabled = true; }
        }

        private async void CleanLeftoversAsync()
        {
            var files = GridHelper.GetCheckedRows(gFs).Select(i => GridHelper.GetCell(gFs, i, "Path")).Where(s => !string.IsNullOrEmpty(s)).ToArray();
            string[] regs;
            Dictionary<string, object>[] regValues;
            GridHelper.CollectCheckedRegCleanup(gReg, out regs, out regValues);
            if (files.Length == 0 && regs.Length == 0 && regValues.Length == 0)
            {
                MessageBox.Show(this, "Check at least one filesystem item or registry entry.", "Nothing Selected");
                return;
            }
            if (MessageBox.Show(this, string.Format("Back up and permanently delete {0} filesystem item(s), {1} registry key(s), and {2} registry value(s)?", files.Length, regs.Length, regValues.Length), "Confirm Cleanup", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;

            bDelete.Enabled = false;
            try
            {
                var res = await Task.Run(() => Bridge.Call("action", "leftovers-clean", new { files = files, registry = regs, regValues = regValues, backupFirst = true })) as Dictionary<string, object>;
                AppendLog("Cleanup finished: " + Bridge.Dump(res));
                if (onCompleted != null) onCompleted();
                await ScanLeftoversInternalAsync();
            }
            catch (Exception ex) { AppendLog("Cleanup error: " + ex.Message); }
            finally { bDelete.Enabled = true; }
        }
    }

    internal class ForcedUninstallForm : Form
    {
        private readonly ThemePalette theme;
        private readonly Action onCompleted;
        private TextBox tPath, tName;
        private ComboBox cMode, cFoundSetup;
        private Button bProbe, bRunSetup, bDelete, bSelAll, bUnselAll;
        private Label lInfo;
        private DataGridView gFs, gReg;

        public ForcedUninstallForm(string initialPath, string initialName, string defaultMode, ThemePalette pal, Action onDone)
        {
            theme = pal ?? ThemePalette.Get("dark");
            onCompleted = onDone;

            Text = "Forced Uninstall Wizard (Remnants & Broken Installations)";
            Size = new Size(940, 680);
            MinimumSize = new Size(760, 540);
            StartPosition = FormStartPosition.CenterParent;
            BackColor = theme.BgApp;
            ForeColor = theme.TextPrimary;
            Font = new Font("Segoe UI", 9f);

            var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(10), WrapContents = true, BackColor = theme.BgToolbar };
            tPath = new TextBox { Width = 330, Text = initialPath ?? "", BackColor = theme.BgInput, ForeColor = theme.TextPrimary, Margin = new Padding(3, 5, 3, 3) };
            var bFile = MkBtn("File...", theme.AccentBlue);
            var bDir = MkBtn("Folder...", theme.AccentBlue);
            tName = new TextBox { Width = 200, Text = initialName ?? "", BackColor = theme.BgInput, ForeColor = theme.TextPrimary, Margin = new Padding(3, 5, 3, 3) };
            cMode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 100, BackColor = theme.BgInput, ForeColor = theme.TextPrimary, Margin = new Padding(3, 5, 6, 3) };
            cMode.Items.AddRange(new object[] { "safe", "moderate", "advanced" });
            int idx = cMode.Items.IndexOf(string.IsNullOrEmpty(defaultMode) ? "moderate" : defaultMode);
            cMode.SelectedIndex = idx >= 0 ? idx : 1;
            bProbe = MkBtn("Scan & Probe", theme.AccentGreen);

            top.Controls.AddRange(new Control[]
            {
                new Label { Text = "Folder/Shortcut/Exe:", AutoSize = true, Padding = new Padding(2, 8, 2, 0), ForeColor = theme.TextMuted },
                tPath, bFile, bDir,
                new Label { Text = "Name:", AutoSize = true, Padding = new Padding(6, 8, 2, 0), ForeColor = theme.TextMuted },
                tName,
                new Label { Text = "Mode:", AutoSize = true, Padding = new Padding(4, 8, 2, 0), ForeColor = theme.TextMuted },
                cMode, bProbe
            });

            var mid = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8), BackColor = theme.BgHeader };
            cFoundSetup = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 360, BackColor = theme.BgInput, ForeColor = theme.TextPrimary, Margin = new Padding(3, 5, 6, 3) };
            bRunSetup = MkBtn("Run Built-in Uninstaller", theme.AccentAmber);
            bRunSetup.Enabled = false;
            bSelAll = MkBtn("Check All", Color.FromArgb(148, 163, 184));
            bUnselAll = MkBtn("Uncheck All", Color.FromArgb(148, 163, 184));
            bDelete = MkBtn("Backup & Delete Checked", theme.AccentRed);
            bDelete.Enabled = false;
            mid.Controls.AddRange(new Control[]
            {
                new Label { Text = "Found Setup/Uninstaller:", AutoSize = true, Padding = new Padding(2, 8, 2, 0), ForeColor = theme.TextMuted },
                cFoundSetup, bRunSetup, bSelAll, bUnselAll, bDelete
            });

            lInfo = new Label
            {
                Dock = DockStyle.Top,
                Height = 44,
                Padding = new Padding(10, 6, 10, 4),
                BackColor = theme.BgCard,
                ForeColor = theme.TextMuted,
                Text = "Enter a program folder, .exe, .lnk shortcut, or exact name and click 'Scan & Probe'.",
            };

            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 230 };
            gFs = new DataGridView { Dock = DockStyle.Fill };
            gReg = new DataGridView { Dock = DockStyle.Fill };
            GridHelper.Style(gFs, theme);
            GridHelper.Style(gReg, theme);
            split.Panel1.Controls.Add(gFs);
            split.Panel1.Controls.Add(new Label { Text = "Matched Filesystem Targets:", Dock = DockStyle.Top, Height = 22, BackColor = theme.BgGridHeader, ForeColor = theme.AccentGreen, Padding = new Padding(6, 3, 0, 0) });
            split.Panel2.Controls.Add(gReg);
            split.Panel2.Controls.Add(new Label { Text = "Matched Registry Keys:", Dock = DockStyle.Top, Height = 22, BackColor = theme.BgGridHeader, ForeColor = theme.AccentBlue, Padding = new Padding(6, 3, 0, 0) });

            Controls.Add(split);
            Controls.Add(lInfo);
            Controls.Add(mid);
            Controls.Add(top);

            bFile.Click += (s, e) => { using (var d = new OpenFileDialog()) { if (d.ShowDialog(this) == DialogResult.OK) tPath.Text = d.FileName; } };
            bDir.Click += (s, e) => { using (var d = new FolderBrowserDialog()) { if (d.ShowDialog(this) == DialogResult.OK) tPath.Text = d.SelectedPath; } };
            bProbe.Click += (s, e) => ProbeAsync();
            bSelAll.Click += (s, e) => { GridHelper.ToggleAllChecks(gFs, true); GridHelper.ToggleAllChecks(gReg, true); };
            bUnselAll.Click += (s, e) => { GridHelper.ToggleAllChecks(gFs, false); GridHelper.ToggleAllChecks(gReg, false); };
            bRunSetup.Click += (s, e) => RunSetupAsync();
            bDelete.Click += (s, e) => DeleteCheckedAsync();

            if (!string.IsNullOrWhiteSpace(initialPath) || !string.IsNullOrWhiteSpace(initialName))
            {
                Shown += (s, e) => ProbeAsync();
            }
        }

        private Button MkBtn(string text, Color c)
        {
            return new Button
            {
                Text = text,
                BackColor = c,
                ForeColor = theme.BtnFore,
                FlatStyle = FlatStyle.Flat,
                AutoSize = true,
                Height = 28,
                Padding = new Padding(8, 0, 8, 0),
                Margin = new Padding(3),
                Font = new Font("Segoe UI", 8.6f, FontStyle.Bold),
            };
        }

        private async void ProbeAsync()
        {
            string tp = (tPath.Text ?? "").Trim();
            string nm = (tName.Text ?? "").Trim();
            string mode = Convert.ToString(cMode.SelectedItem) ?? "moderate";
            if (string.IsNullOrEmpty(tp) && string.IsNullOrEmpty(nm))
            {
                MessageBox.Show(this, "Enter a target path/folder or program name.");
                return;
            }
            bProbe.Enabled = false;
            try
            {
                var res = await Task.Run(() => Bridge.Call("action", "forced-probe", new { targetPath = tp, programName = nm, mode = mode })) as Dictionary<string, object>;
                if (res != null)
                {
                    GridHelper.Fill(gFs, res.ContainsKey("filesystem") ? res["filesystem"] : null, true, true, null, null);
                    GridHelper.Fill(gReg, res.ContainsKey("registry") ? res["registry"] : null, true, true, null, null);
                    cFoundSetup.Items.Clear();
                    var setups = res.ContainsKey("uninstallers") ? res["uninstallers"] as object[] : null;
                    if (setups != null)
                    {
                        foreach (var o in setups)
                        {
                            var d = o as Dictionary<string, object>;
                            if (d != null && d.ContainsKey("path")) cFoundSetup.Items.Add(Convert.ToString(d["path"]));
                        }
                    }
                    if (cFoundSetup.Items.Count > 0) { cFoundSetup.SelectedIndex = 0; bRunSetup.Enabled = true; }
                    else bRunSetup.Enabled = false;

                    var traced = res.ContainsKey("tracedMatches") ? res["tracedMatches"] as object[] : null;
                    var refs = res.ContainsKey("refLogs") ? res["refLogs"] as object[] : null;
                    lInfo.Text = string.Format(
                        "Resolved Search Name: '{0}'   |   Target Dir: {1}\r\n" +
                        "Found: {2} filesystem path(s), {3} registry key(s), {4} setup exe(s), {5} traced log(s), {6} reference log(s).",
                        res.ContainsKey("searchName") ? Convert.ToString(res["searchName"]) : "",
                        res.ContainsKey("targetDir") ? Convert.ToString(res["targetDir"]) : "-",
                        gFs.Rows.Count, gReg.Rows.Count,
                        cFoundSetup.Items.Count,
                        traced != null ? traced.Length : 0,
                        refs != null ? refs.Length : 0);
                    bDelete.Enabled = (gFs.Rows.Count + gReg.Rows.Count) > 0;
                }
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Probe Error"); }
            finally { bProbe.Enabled = true; }
        }

        private async void RunSetupAsync()
        {
            string exe = Convert.ToString(cFoundSetup.SelectedItem);
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return;
            bRunSetup.Enabled = false;
            try
            {
                await Task.Run(() =>
                {
                    using (var p = Process.Start(new ProcessStartInfo { FileName = exe, UseShellExecute = true }))
                    {
                        if (p != null) p.WaitForExit();
                    }
                });
                ProbeAsync();
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Launch Error"); }
            finally { bRunSetup.Enabled = cFoundSetup.Items.Count > 0; }
        }

        private async void DeleteCheckedAsync()
        {
            var files = GridHelper.GetCheckedRows(gFs).Select(i => GridHelper.GetCell(gFs, i, "Path")).Where(s => !string.IsNullOrEmpty(s)).ToArray();
            string[] regs;
            Dictionary<string, object>[] regValues;
            GridHelper.CollectCheckedRegCleanup(gReg, out regs, out regValues);
            if (files.Length == 0 && regs.Length == 0 && regValues.Length == 0) { MessageBox.Show(this, "Check at least one item."); return; }
            if (MessageBox.Show(this, string.Format("Back up and delete {0} filesystem path(s), {1} registry key(s), and {2} value(s)?", files.Length, regs.Length, regValues.Length), "Confirm Forced Cleanup", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;
            bDelete.Enabled = false;
            try
            {
                await Task.Run(() => Bridge.Call("action", "leftovers-clean", new { files = files, registry = regs, regValues = regValues, backupFirst = true }));
                if (onCompleted != null) onCompleted();
                ProbeAsync();
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Cleanup Error"); }
            finally { bDelete.Enabled = true; }
        }
    }

    internal class HunterOverlayForm : Form
    {
        private readonly MainForm ownerForm;
        private bool dragging = false;
        private bool moved = false;
        private Point dragStartScreen;
        private Point formStartLoc;

        public HunterOverlayForm(MainForm owner)
        {
            ownerForm = owner;
            FormBorderStyle = FormBorderStyle.None;
            TopMost = true;
            ShowInTaskbar = false;
            Size = new Size(76, 76);
            StartPosition = FormStartPosition.Manual;
            var area = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(area.Right - 104, area.Top + 36);
            BackColor = Color.FromArgb(14, 18, 26);
            Cursor = Cursors.SizeAll;

            var path = new GraphicsPath();
            path.AddEllipse(2, 2, 72, 72);
            Region = new Region(path);

            Paint += OnPaintTarget;
            MouseDown += OnMouseDownTarget;
            MouseMove += OnMouseMoveTarget;
            MouseUp += OnMouseUpTarget;
        }

        private void OnPaintTarget(object sender, PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var bg = new SolidBrush(Color.FromArgb(20, 26, 36))) e.Graphics.FillEllipse(bg, 4, 4, 68, 68);
            using (var outer = new Pen(Color.FromArgb(74, 222, 128), 3f)) e.Graphics.DrawEllipse(outer, 6, 6, 64, 64);
            using (var inner = new Pen(Color.FromArgb(56, 189, 248), 2f)) e.Graphics.DrawEllipse(inner, 18, 18, 40, 40);
            using (var cross = new Pen(Color.FromArgb(248, 113, 113), 2f))
            {
                e.Graphics.DrawLine(cross, 38, 8, 38, 28);
                e.Graphics.DrawLine(cross, 38, 48, 38, 68);
                e.Graphics.DrawLine(cross, 8, 38, 28, 38);
                e.Graphics.DrawLine(cross, 48, 38, 68, 38);
            }
            using (var dot = new SolidBrush(Color.FromArgb(74, 222, 128))) e.Graphics.FillEllipse(dot, 34, 34, 8, 8);
        }

        private void OnMouseDownTarget(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                dragging = true;
                moved = false;
                dragStartScreen = PointToScreen(e.Location);
                formStartLoc = Location;
            }
            else if (e.Button == MouseButtons.Right)
            {
                InspectUnderlyingWindow(PointToScreen(new Point(Width / 2, Height / 2)));
            }
        }

        private void OnMouseMoveTarget(object sender, MouseEventArgs e)
        {
            if (!dragging) return;
            Point cur = PointToScreen(e.Location);
            int dx = cur.X - dragStartScreen.X;
            int dy = cur.Y - dragStartScreen.Y;
            if (Math.Abs(dx) > 3 || Math.Abs(dy) > 3) moved = true;
            Location = new Point(formStartLoc.X + dx, formStartLoc.Y + dy);
        }

        private void OnMouseUpTarget(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            dragging = false;
            Point probe = PointToScreen(new Point(Width / 2, Height / 2));
            if (moved) InspectUnderlyingWindow(probe);
        }

        private void InspectUnderlyingWindow(Point screenPoint)
        {
            Visible = false;
            Application.DoEvents();
            int pidInt = 0;
            string procName = "";
            string exePath = "";
            try
            {
                Win32.POINT pt;
                pt.X = screenPoint.X;
                pt.Y = screenPoint.Y;
                IntPtr hwnd = Win32.WindowFromPoint(pt);
                IntPtr root = Win32.GetAncestor(hwnd, 2);
                if (root != IntPtr.Zero) hwnd = root;
                uint pid = 0;
                Win32.GetWindowThreadProcessId(hwnd, out pid);
                pidInt = (int)pid;
                if (pidInt > 0 && pidInt != Process.GetCurrentProcess().Id)
                {
                    var p = Process.GetProcessById(pidInt);
                    procName = p.ProcessName;
                    try { if (p.MainModule != null) exePath = p.MainModule.FileName; } catch { }
                }
            }
            catch { }
            finally { Visible = true; }

            string target = !string.IsNullOrEmpty(exePath) ? exePath : procName;
            var menu = new ContextMenuStrip();
            var header = new ToolStripMenuItem(string.IsNullOrEmpty(procName) ? "No target window detected" : (procName + " (PID " + pidInt + ")"));
            header.Enabled = false;
            menu.Items.Add(header);
            menu.Items.Add(new ToolStripSeparator());

            if (!string.IsNullOrEmpty(target))
            {
                var mHunt = new ToolStripMenuItem("Inspect & Uninstall in Main Window");
                mHunt.Click += (s, ev) => ownerForm.RestoreFromHunterOverlay(target, false);
                var mLeft = new ToolStripMenuItem("Scan Leftovers");
                mLeft.Click += (s, ev) => ownerForm.RestoreFromHunterOverlay(target, true);
                var mFolder = new ToolStripMenuItem("Open Executable Folder");
                mFolder.Enabled = !string.IsNullOrEmpty(exePath) && File.Exists(exePath);
                mFolder.Click += (s, ev) => { try { Process.Start("explorer.exe", "/select,\"" + exePath + "\""); } catch { } };
                var mKill = new ToolStripMenuItem("Kill Process (PID " + pidInt + ")");
                mKill.Click += (s, ev) =>
                {
                    if (MessageBox.Show(this, "Terminate process " + procName + " (PID " + pidInt + ")?", "Confirm Kill", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                    try { Bridge.Call("action", "kill-process", new { pid = pidInt }); } catch (Exception ex) { MessageBox.Show(this, ex.Message); }
                };
                menu.Items.AddRange(new ToolStripItem[] { mHunt, mLeft, mFolder, mKill, new ToolStripSeparator() });
            }

            var mMain = new ToolStripMenuItem("Exit Hunter Crosshair");
            mMain.Click += (s, ev) => { ownerForm.RestoreFromHunterOverlay(null, false); Close(); };
            menu.Items.Add(mMain);
            menu.Show(screenPoint);
        }
    }

    internal class MainForm : Form
    {
        private ThemePalette theme = ThemePalette.Get("dark");
        private readonly FlatTabControl tabs = new FlatTabControl();
        private Panel sidebar, headerPanel, dashCardContainer;
        private FlowLayoutPanel navFlow, headerActions;
        private Label lHeaderTitle, lHeaderSub, lProgDetails, lTraceActive, lJunkSummary, lBrandTitle, lBrandSub;
        private readonly List<NavItemControl> navButtons = new List<NavItemControl>();
        private readonly List<Label> navSectionLabels = new List<Label>();
        private readonly List<KpiCardControl> kpiCards = new List<KpiCardControl>();
        private readonly List<Panel> toolbarPanels = new List<Panel>();
        private readonly List<DataGridView> allGrids = new List<DataGridView>();
        private DrivesBarPanel drivesBar;
        private TabPage tpDashboard, tpPrograms, tpHunter, tpLeftovers, tpTraces, tpAutoruns, tpWinApps, tpTracks, tpExtensions, tpJunk, tpBackups, tpRegistry, tpTools, tpEvidence, tpHistory, tpSettings;
        private DataGridView gDashRecent, gPrograms, gHuntProc, gHuntProg, gLeftFs, gLeftReg, gTraces, gTraceItems, gAutoruns, gWinApps, gTracks, gExtensions, gJunk, gBackups, gRegistry, gEvidenceTargets, gDrives, gHistory;
        private TextBox tSearch, tWinAppFilter, tHunt, tLeft, tTraceName, tTraceInstaller, tJunkCustom, tWipeFolder, tWipeMb, tTrackOut, tBackupOut, tHistorySearch, tSettingsPreview, tSetProduct, tSetHistoryLimit;
        private CheckBox chkSystemProgs, chkJunkRecent, chkSetConfirm, chkSetBackupOnUninstall, chkSetDryRun;
        private ComboBox cProgCategory, cLeftMode, cHistorySource, cEvidencePasses, cSetLeftMode, cSetTheme;
        private ListBox lTools;
        private string[] toolIds;
        private object[] cachedPrograms = new object[0];
        private StatusStrip status;
        private ToolStripStatusLabel statusLabel;
        private MenuStrip mainMenu;
        private bool initialLoadFinished = false;
        private string initialLoadError = null;
        private HunterOverlayForm overlayForm = null;

        private static readonly string[] ProgramColumns = new string[]
        {
            "DisplayName", "DisplayVersion", "Publisher", "SizeMB", "InstallDate", "Architecture", "Category", "InstallLocation", "RegistryKey"
        };

        private static readonly string[] TabSubtitles = new string[]
        {
            "Overview, storage health, and recent cleanup activity",
            "Installed desktop software, guided uninstall wizard, and forced cleanup",
            "Target running windows, processes, or desktop shortcuts (.lnk)",
            "Deep filesystem and registry remnant discovery across 3 modes",
            "Monitor installations with before/after snapshots stored in SQLite",
            "Inspect, enable/disable, launch, or clean Windows startup items",
            "Manage Microsoft Store / UWP packages and reset app caches",
            "Remove browser history, Office MRU lists, and Windows usage tracks",
            "Inspect and toggle Chrome, Edge, Brave, Firefox, and BHO add-ons",
            "Find and clean individual temporary, log, dump, and cache files",
            "Manage and restore registry and leftover folder backups",
            "Scan and safely remove stale uninstall, startup, and App Path keys",
            "Quick-launch built-in Windows maintenance and diagnostic utilities",
            "3-pass unrecoverable shredding and drive unallocated free-space wipe",
            "Browse the local operation history or search an optional reference database",
            "Configure visual theme, backup policy, and default scan mode"
        };

        public MainForm()
        {
            Text = "Open Uninstaller - Lifetime Personal Edition";
            Size = new Size(1280, 820);
            MinimumSize = new Size(1000, 650);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = theme.BgApp;
            ForeColor = theme.TextPrimary;
            Font = new Font("Segoe UI", 9f);

            tabs.Dock = DockStyle.Fill;
            BuildMenu();
            BuildStatus();
            BuildSidebar();
            BuildHeader();
            Controls.Add(tabs);

            BuildTabs();
            WireNavigation();
            tabs.BringToFront();
            headerPanel.SendToBack();
            sidebar.SendToBack();
            status.SendToBack();
            mainMenu.SendToBack();

            ApplyTheme("dark");
            SelectNavTab(0);
            Status("Loading local system inventory...");
            LoadAllAsync();
        }

        private void BuildMenu()
        {
            mainMenu = new MenuStrip { BackColor = theme.BgSidebar, ForeColor = theme.TextPrimary };
            var file = new ToolStripMenuItem("File");
            var refresh = new ToolStripMenuItem("Refresh All");
            refresh.Click += (s, e) => { LoadAllAsync(); };
            var openLog = new ToolStripMenuItem("Open App Log");
            openLog.Click += (s, e) => { if (File.Exists(Bridge.LogPath)) Process.Start("notepad.exe", Bridge.LogPath); };
            var exit = new ToolStripMenuItem("Exit");
            exit.Click += (s, e) => Close();
            file.DropDownItems.AddRange(new ToolStripItem[] { refresh, openLog, exit });

            var modules = new ToolStripMenuItem("Modules");
            var mForced = new ToolStripMenuItem("Forced Uninstall Wizard...");
            mForced.Click += (s, e) => OpenForcedUninstall(null, null);
            var mHunter = new ToolStripMenuItem("Launch Floating Hunter Crosshair");
            mHunter.Click += (s, e) => LaunchHunterOverlay();
            var mExportCsv = new ToolStripMenuItem("Export Installed Programs (CSV)");
            mExportCsv.Click += (s, e) => ExportProgramsAsync("csv");
            var mExportHtml = new ToolStripMenuItem("Export Installed Programs (HTML)");
            mExportHtml.Click += (s, e) => ExportProgramsAsync("html");
            modules.DropDownItems.AddRange(new ToolStripItem[] { mForced, mHunter, new ToolStripSeparator(), mExportCsv, mExportHtml });

            var help = new ToolStripMenuItem("Help");
            var about = new ToolStripMenuItem("About");
            about.Click += (s, e) => MessageBox.Show(this,
                "Open Uninstaller v0.6.0\n\nOpen source under the MIT License. Provided as-is, without warranty.\nThis tool deletes files and registry keys: review every item before cleaning.\nNo cloud, no account, no telemetry.\n\nAll features run locally using SQLite, PowerShell, and .NET.",
                "About Open Uninstaller", MessageBoxButtons.OK, MessageBoxIcon.Information);
            help.DropDownItems.Add(about);
            mainMenu.Items.AddRange(new ToolStripItem[] { file, modules, help });
            MainMenuStrip = mainMenu;
            Controls.Add(mainMenu);
        }

        private void BuildStatus()
        {
            status = new StatusStrip { BackColor = theme.BgHeader, ForeColor = theme.TextMuted };
            statusLabel = new ToolStripStatusLabel { ForeColor = theme.TextMuted };
            status.Items.Add(statusLabel);
            Controls.Add(status);
        }

        private void BuildSidebar()
        {
            sidebar = new Panel { Dock = DockStyle.Left, Width = 228, BackColor = theme.BgSidebar, Padding = new Padding(0) };
            var brand = new Panel { Dock = DockStyle.Top, Height = 64, BackColor = theme.BgSidebar, Padding = new Padding(12, 10, 10, 8) };
            brand.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (var br = new SolidBrush(theme.AccentGreen)) e.Graphics.FillRectangle(br, 14, 15, 32, 32);
                using (var bf = new Font("Segoe UI", 10f, FontStyle.Bold))
                    TextRenderer.DrawText(e.Graphics, "OU", bf, new Rectangle(14, 15, 32, 32), theme.BtnFore, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                using (var pen = new Pen(theme.Border, 1f)) e.Graphics.DrawLine(pen, 12, 63, brand.Width - 12, 63);
            };
            lBrandTitle = new Label { Left = 54, Top = 12, AutoSize = true, Text = "OPEN UNINSTALLER", Font = new Font("Segoe UI", 9.2f, FontStyle.Bold), ForeColor = theme.TextPrimary };
            lBrandSub = new Label { Left = 54, Top = 31, AutoSize = true, Text = "v0.6.0 · Local Only", Font = new Font("Segoe UI", 7.8f), ForeColor = theme.TextMuted };
            brand.Controls.AddRange(new Control[] { lBrandTitle, lBrandSub });

            navFlow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                Padding = new Padding(0, 6, 0, 10),
                BackColor = theme.BgSidebar,
            };

            AddNavHeader("OVERVIEW");
            AddNavBtn("Dashboard", 0, theme.AccentBlue);
            AddNavHeader("UNINSTALLER");
            AddNavBtn("Uninstaller", 1, theme.AccentGreen);
            AddNavBtn("Hunter Mode", 2, theme.AccentAmber);
            AddNavBtn("Leftover Scanner", 3, theme.AccentBlue);
            AddNavBtn("Traced Programs", 4, theme.AccentGreen);
            AddNavBtn("Windows Apps", 6, theme.AccentBlue);
            AddNavHeader("OPTIMIZATION & CLEANERS");
            AddNavBtn("Autorun Manager", 5, theme.AccentGreen);
            AddNavBtn("Tracks Cleaner", 7, theme.AccentAmber);
            AddNavBtn("Browser Extensions", 8, theme.AccentBlue);
            AddNavBtn("Junk Files", 9, theme.AccentRed);
            AddNavBtn("Registry Optimizer", 11, theme.AccentRed);
            AddNavHeader("SAFETY & TOOLS");
            AddNavBtn("Backup & Restore", 10, theme.AccentBlue);
            AddNavBtn("Windows Tools", 12, theme.AccentGreen);
            AddNavBtn("Evidence Remover", 13, theme.AccentAmber);
            AddNavBtn("Logs & History", 14, theme.AccentBlue);
            AddNavBtn("Settings", 15, Color.FromArgb(148, 163, 184));

            navFlow.Resize += (s, e) =>
            {
                int w = Math.Max(150, navFlow.ClientSize.Width - 10);
                foreach (Control c in navFlow.Controls) c.Width = w;
            };

            sidebar.Controls.Add(navFlow);
            sidebar.Controls.Add(brand);
            Controls.Add(sidebar);
        }

        private void AddNavHeader(string title)
        {
            var l = new Label
            {
                Text = title,
                UseMnemonic = false,
                Height = 22,
                Width = 196,
                Padding = new Padding(14, 7, 4, 0),
                Font = new Font("Segoe UI", 7.4f, FontStyle.Bold),
                ForeColor = theme.TextMuted,
            };
            navSectionLabels.Add(l);
            navFlow.Controls.Add(l);
        }

        private void AddNavBtn(string title, int tabIdx, Color accent)
        {
            var btn = new NavItemControl(title, tabIdx, theme, accent) { Width = 196 };
            btn.Click += (s, e) => SelectNavTab(tabIdx);
            navButtons.Add(btn);
            navFlow.Controls.Add(btn);
        }

        private void BuildHeader()
        {
            headerPanel = new Panel { Dock = DockStyle.Top, Height = 58, BackColor = theme.BgHeader, Padding = new Padding(16, 8, 14, 8) };
            headerPanel.Paint += (s, e) =>
            {
                using (var pen = new Pen(theme.Border, 1f)) e.Graphics.DrawLine(pen, 0, headerPanel.Height - 1, headerPanel.Width, headerPanel.Height - 1);
            };
            lHeaderTitle = new Label { Left = 16, Top = 8, AutoSize = true, Text = "Dashboard", Font = new Font("Segoe UI", 12.5f, FontStyle.Bold), ForeColor = theme.TextPrimary };
            lHeaderSub = new Label { Left = 16, Top = 33, AutoSize = true, Text = TabSubtitles[0], Font = new Font("Segoe UI", 8.4f), ForeColor = theme.TextMuted };

            headerActions = new FlowLayoutPanel
            {
                Dock = DockStyle.Right,
                AutoSize = true,
                WrapContents = false,
                Padding = new Padding(0, 5, 0, 0),
                BackColor = Color.Transparent,
            };
            var bTopForced = B("Forced Uninstall...", theme.AccentAmber);
            var bTopTrace = B("Install & Monitor", theme.AccentGreen);
            var bTopFloat = B("Float Crosshair", theme.AccentBlue);
            var bTopBackup = B("Backup Registry", Color.FromArgb(148, 163, 184));
            var bTopRefresh = B("Refresh", theme.AccentBlue);
            bTopForced.Click += (s, e) => OpenForcedUninstall(null, null);
            bTopTrace.Click += (s, e) => SelectNavTab(4);
            bTopFloat.Click += (s, e) => LaunchHunterOverlay();
            bTopBackup.Click += (s, e) =>
            {
                DoActionAsync("backup", null, o =>
                {
                    if (tBackupOut != null) tBackupOut.Text = Bridge.PrettyDump(o);
                    LoadAsync("backups", gBackups, false, false, null, null);
                    Status("Registry backup created.");
                });
            };
            bTopRefresh.Click += (s, e) => LoadAllAsync();

            headerActions.Controls.AddRange(new Control[] { bTopForced, bTopTrace, bTopFloat, bTopBackup, bTopRefresh });
            headerPanel.Controls.AddRange(new Control[] { lHeaderTitle, lHeaderSub, headerActions });
            Controls.Add(headerPanel);
        }

        private void WireNavigation()
        {
            tabs.SelectedIndexChanged += (s, e) =>
            {
                int idx = tabs.SelectedIndex;
                foreach (var b in navButtons) b.IsActive = (b.TabIndexTarget == idx);
                if (idx >= 0 && idx < tabs.TabPages.Count)
                {
                    lHeaderTitle.Text = tabs.TabPages[idx].Text;
                    lHeaderSub.Text = idx < TabSubtitles.Length ? TabSubtitles[idx] : "";
                }
            };
        }

        private void SelectNavTab(int idx)
        {
            if (idx >= 0 && idx < tabs.TabPages.Count) tabs.SelectedIndex = idx;
            foreach (var b in navButtons) b.IsActive = (b.TabIndexTarget == idx);
            if (idx >= 0 && idx < tabs.TabPages.Count)
            {
                lHeaderTitle.Text = tabs.TabPages[idx].Text;
                lHeaderSub.Text = idx < TabSubtitles.Length ? TabSubtitles[idx] : "";
            }
        }

        private void Status(string text)
        {
            Bridge.Log("STATUS " + text);
            if (statusLabel != null) statusLabel.Text = text;
        }

        private TabPage NewGridTab(string title, out FlowLayoutPanel top, out DataGridView grid)
        {
            var tp = new TabPage(title) { BackColor = theme.BgApp };
            grid = new DataGridView { Dock = DockStyle.Fill };
            GridHelper.Style(grid, theme);
            allGrids.Add(grid);
            top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8), WrapContents = true, BackColor = theme.BgToolbar };
            toolbarPanels.Add(top);
            tp.Controls.Add(grid);
            tp.Controls.Add(top);
            tabs.TabPages.Add(tp);
            return tp;
        }

        private TabPage NewCustomTab(string title, out FlowLayoutPanel top)
        {
            var tp = new TabPage(title) { BackColor = theme.BgApp };
            top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8), WrapContents = true, BackColor = theme.BgToolbar };
            toolbarPanels.Add(top);
            tp.Controls.Add(top);
            tabs.TabPages.Add(tp);
            return tp;
        }

        private Button B(string text, Color color)
        {
            return new Button
            {
                Text = text,
                UseMnemonic = false,
                BackColor = color,
                ForeColor = theme.BtnFore,
                FlatStyle = FlatStyle.Flat,
                AutoSize = true,
                Height = 29,
                Padding = new Padding(8, 0, 8, 0),
                Margin = new Padding(3),
                Cursor = Cursors.Hand,
                Font = new Font("Segoe UI", 8.6f, FontStyle.Bold),
            };
        }

        private TextBox T(string placeholder, int w)
        {
            var tb = new TextBox
            {
                Width = w,
                Text = placeholder,
                Tag = placeholder,
                ForeColor = theme.TextMuted,
                BackColor = theme.BgInput,
                BorderStyle = BorderStyle.FixedSingle,
                Margin = new Padding(3, 6, 3, 3),
            };
            tb.Enter += (s, e) =>
            {
                if (tb.Text == (string)tb.Tag) { tb.Text = ""; tb.ForeColor = theme.TextPrimary; }
            };
            tb.Leave += (s, e) =>
            {
                if (string.IsNullOrWhiteSpace(tb.Text)) { tb.Text = (string)tb.Tag; tb.ForeColor = theme.TextMuted; }
            };
            return tb;
        }

        private static string GetInput(TextBox tb)
        {
            if (tb == null) return "";
            string ph = tb.Tag as string;
            string val = (tb.Text ?? "").Trim();
            if (ph != null && val == ph) return "";
            return val;
        }

        private void SetInput(TextBox tb, string value)
        {
            if (tb == null) return;
            if (string.IsNullOrWhiteSpace(value)) { tb.Text = (string)tb.Tag; tb.ForeColor = theme.TextMuted; }
            else { tb.Text = value; tb.ForeColor = theme.TextPrimary; }
        }

        private Label L(string text)
        {
            return new Label { Text = text, UseMnemonic = false, AutoSize = true, Padding = new Padding(5, 8, 2, 0), ForeColor = theme.TextMuted };
        }

        private void BuildTabs()
        {
            FlowLayoutPanel top;

            // 0. Dashboard (Visual KPI Cards + Drive Bars + Recent Activity)
            tpDashboard = NewCustomTab("Dashboard", out top);
            var bDashRefresh = B("Refresh Inventory", theme.AccentBlue);
            var bJumpLeft = B("Leftover Scanner", theme.AccentAmber);
            var bJumpTracks = B("Tracks Cleaner", theme.AccentAmber);
            var bJumpJunk = B("Junk Cleaner", theme.AccentRed);
            var bJumpReg = B("Registry Optimizer", theme.AccentRed);
            top.Controls.AddRange(new Control[] { bDashRefresh, bJumpLeft, bJumpTracks, bJumpJunk, bJumpReg });
            bDashRefresh.Click += (s, e) => LoadAllAsync();
            bJumpLeft.Click += (s, e) => SelectNavTab(3);
            bJumpTracks.Click += (s, e) => SelectNavTab(7);
            bJumpJunk.Click += (s, e) => SelectNavTab(9);
            bJumpReg.Click += (s, e) => SelectNavTab(11);

            var dashScroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(12), BackColor = theme.BgApp };
            dashCardContainer = dashScroll;

            var kpiGrid = new TableLayoutPanel { Dock = DockStyle.Top, Height = 224, ColumnCount = 4, RowCount = 2 };
            for (int c = 0; c < 4; c++) kpiGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25f));
            for (int r = 0; r < 2; r++) kpiGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 50f));

            AddKpiCard(kpiGrid, 0, 0, "INSTALLED PROGRAMS", "0", "Open Uninstaller", theme.AccentGreen, 1);
            AddKpiCard(kpiGrid, 1, 0, "STORE / UWP APPS", "0", "Open Windows Apps", theme.AccentBlue, 6);
            AddKpiCard(kpiGrid, 2, 0, "TRACED INSTALLATIONS", "0", "Open Traced Programs", theme.AccentGreen, 4);
            AddKpiCard(kpiGrid, 3, 0, "STARTUP ENTRIES", "0", "Open Autorun Manager", theme.AccentAmber, 5);
            AddKpiCard(kpiGrid, 0, 1, "BROWSER EXTENSIONS", "0", "Inspect Add-ons", theme.AccentBlue, 8);
            AddKpiCard(kpiGrid, 1, 1, "JUNK FILES FOUND", "0", "Open Junk Cleaner", theme.AccentRed, 9);
            AddKpiCard(kpiGrid, 2, 1, "STALE REGISTRY KEYS", "0", "Open Registry Optimizer", theme.AccentRed, 11);
            AddKpiCard(kpiGrid, 3, 1, "REGISTRY BACKUPS", "0", "Open Backup & Restore", theme.AccentBlue, 10);

            drivesBar = new DrivesBarPanel { Dock = DockStyle.Top, Height = 126, Margin = new Padding(6) };

            var recentLabel = new Label
            {
                Dock = DockStyle.Top,
                Height = 26,
                Padding = new Padding(4, 6, 0, 0),
                Font = new Font("Segoe UI", 9.2f, FontStyle.Bold),
                ForeColor = theme.TextPrimary,
                Text = "Recent Local Activity (SQLite Log)",
            };
            gDashRecent = new DataGridView { Dock = DockStyle.Fill };
            GridHelper.Style(gDashRecent, theme);
            allGrids.Add(gDashRecent);

            dashScroll.Controls.Add(gDashRecent);
            dashScroll.Controls.Add(recentLabel);
            dashScroll.Controls.Add(drivesBar);
            dashScroll.Controls.Add(kpiGrid);
            gDashRecent.BringToFront();
            tpDashboard.Controls.Add(dashScroll);
            dashScroll.BringToFront();

            // 1. Uninstaller
            tpPrograms = NewGridTab("Uninstaller", out top, out gPrograms);
            tSearch = T("Filter (name or * wildcard)", 190);
            cProgCategory = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 145, BackColor = theme.BgInput, ForeColor = theme.TextPrimary, Margin = new Padding(3, 5, 3, 3) };
            cProgCategory.Items.AddRange(new object[] { "All", "New / Recent (30d)", "Internet", "Games", "Multimedia", "Utilities", "System & Runtimes", "General", "64-bit", "32-bit", "User" });
            cProgCategory.SelectedIndex = 0;
            chkSystemProgs = new CheckBox { Text = "System", AutoSize = true, ForeColor = theme.TextMuted, Margin = new Padding(6, 8, 6, 0) };
            var bSearch = B("Search", theme.AccentGreen);
            var bUninstall = B("Uninstall...", theme.AccentRed);
            var bQuickUn = B("Quick Uninstall", theme.AccentAmber);
            var bChangeRep = B("Repair", theme.AccentBlue);
            var bScanProgLeft = B("Leftovers", theme.AccentBlue);
            var bExportCsv = B("Export CSV", Color.FromArgb(148, 163, 184));
            var bExportHtml = B("HTML", Color.FromArgb(148, 163, 184));
            top.Controls.AddRange(new Control[] { L("Filter:"), tSearch, L("Group:"), cProgCategory, chkSystemProgs, bSearch, bUninstall, bQuickUn, bChangeRep, bScanProgLeft, bExportCsv, bExportHtml });

            lProgDetails = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 52,
                BackColor = theme.BgHeader,
                ForeColor = theme.TextMuted,
                Padding = new Padding(10, 6, 10, 6),
                Text = "Select a program for details, double-click for Uninstall Wizard, or check multiple boxes for Batch Quick Uninstall.",
            };
            tpPrograms.Controls.Add(lProgDetails);
            gPrograms.BringToFront();

            bSearch.Click += (s, e) => SearchProgramsAsync(GetInput(tSearch));
            tSearch.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; SearchProgramsAsync(GetInput(tSearch)); } };
            cProgCategory.SelectedIndexChanged += (s, e) => ApplyProgramFilter();
            chkSystemProgs.CheckedChanged += (s, e) => SearchProgramsAsync(GetInput(tSearch));
            bUninstall.Click += (s, e) => HandleProgramUninstallClick(false);
            bQuickUn.Click += (s, e) => HandleProgramUninstallClick(true);
            bChangeRep.Click += (s, e) => ChangeOrRepairSelectedProgram();
            bScanProgLeft.Click += (s, e) => SendSelectedProgramToLeftovers();
            bExportCsv.Click += (s, e) => ExportProgramsAsync("csv");
            bExportHtml.Click += (s, e) => ExportProgramsAsync("html");
            gPrograms.SelectionChanged += (s, e) => UpdateSelectedProgramDetails();
            gPrograms.CellDoubleClick += (s, e) => { if (e.RowIndex >= 0 && e.ColumnIndex > 0) OpenUninstallWizard(false); };
            AttachProgramContextMenu();

            // 2. Hunter Mode
            tpHunter = NewGridTab("Hunter Mode", out top, out gHuntProc);
            tHunt = T("process, program name, or .lnk path", 250);
            var bHunt = B("Hunt", theme.AccentGreen);
            var bFloatCross = B("Float Crosshair", theme.AccentAmber);
            var bPickWin = B("Pick Window (3s)", theme.AccentAmber);
            var bHuntUninst = B("Uninstall...", theme.AccentRed);
            var bKillProc = B("Kill Process", theme.AccentRed);
            var bHuntLeft = B("Scan Leftovers", theme.AccentBlue);
            var bHuntRefresh = B("Refresh", theme.AccentBlue);
            top.Controls.AddRange(new Control[] { L("Find:"), tHunt, bHunt, bFloatCross, bPickWin, bHuntUninst, bKillProc, bHuntLeft, bHuntRefresh });
            gHuntProg = new DataGridView { Dock = DockStyle.Bottom, Height = 210 };
            GridHelper.Style(gHuntProg, theme);
            allGrids.Add(gHuntProg);
            tpHunter.Controls.Add(gHuntProg);
            gHuntProc.BringToFront();
            bHunt.Click += (s, e) => HuntAsync(GetInput(tHunt));
            tHunt.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; HuntAsync(GetInput(tHunt)); } };
            bFloatCross.Click += (s, e) => LaunchHunterOverlay();
            bPickWin.Click += (s, e) => PickWindowAsync(bPickWin);
            bHuntUninst.Click += (s, e) =>
            {
                if (gHuntProg.SelectedRows.Count == 0) { MessageBox.Show(this, "Hunt and select a matched program in the bottom grid first."); return; }
                var d = GridHelper.GetRowDict(gHuntProg, gHuntProg.SelectedRows[0].Index);
                if (d != null) OpenWizardForDict(d, false);
            };
            bKillProc.Click += (s, e) => KillSelectedHuntedProcess();
            bHuntLeft.Click += (s, e) => SendHuntedToLeftovers();
            bHuntRefresh.Click += (s, e) => LoadAsync("processes", gHuntProc, false, false, null, ProcIconSelector);
            AttachHunterContextMenus();

            // 3. Leftover Scanner
            tpLeftovers = NewGridTab("Leftover Scanner", out top, out gLeftFs);
            tLeft = T("program name or shortcut path", 250);
            cLeftMode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110, BackColor = theme.BgInput, ForeColor = theme.TextPrimary, Margin = new Padding(3, 5, 3, 3) };
            cLeftMode.Items.AddRange(new object[] { "safe", "moderate", "advanced" });
            cLeftMode.SelectedIndex = 1;
            var bLeft = B("Scan", theme.AccentGreen);
            var bLeftForced = B("Forced Probe...", theme.AccentAmber);
            var bLeftAll = B("Check All", theme.AccentBlue);
            var bLeftNone = B("Uncheck All", Color.FromArgb(148, 163, 184));
            var bLeftClean = B("Backup & Delete Checked", theme.AccentRed);
            top.Controls.AddRange(new Control[] { L("Name:"), tLeft, L("Mode:"), cLeftMode, bLeft, bLeftForced, bLeftAll, bLeftNone, bLeftClean });
            gLeftReg = new DataGridView { Dock = DockStyle.Bottom, Height = 230 };
            GridHelper.Style(gLeftReg, theme);
            allGrids.Add(gLeftReg);
            tpLeftovers.Controls.Add(gLeftReg);
            gLeftFs.BringToFront();
            bLeft.Click += (s, e) => LeftoversAsync(GetInput(tLeft), Convert.ToString(cLeftMode.SelectedItem));
            tLeft.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; LeftoversAsync(GetInput(tLeft), Convert.ToString(cLeftMode.SelectedItem)); } };
            bLeftForced.Click += (s, e) => OpenForcedUninstall(GetInput(tLeft), GetInput(tLeft));
            bLeftAll.Click += (s, e) => { GridHelper.ToggleAllChecks(gLeftFs, true); GridHelper.ToggleAllChecks(gLeftReg, true); };
            bLeftNone.Click += (s, e) => { GridHelper.ToggleAllChecks(gLeftFs, false); GridHelper.ToggleAllChecks(gLeftReg, false); };
            bLeftClean.Click += (s, e) => CleanSelectedLeftovers();

            // 4. Traced Programs
            tpTraces = NewGridTab("Traced Programs", out top, out gTraces);
            tTraceName = T("Program name for trace", 165);
            tTraceInstaller = T("Optional installer (.exe/.msi)", 200);
            var bTraceBrowse = B("Browse...", Color.FromArgb(148, 163, 184));
            var bTraceStart = B("Install & Monitor", theme.AccentGreen);
            var bTraceStop = B("Stop & Save", theme.AccentAmber);
            var bTraceExpHtml = B("Export HTML", theme.AccentBlue);
            var bTraceExpJson = B("Export JSON", theme.AccentBlue);
            var bTraceImport = B("Import...", Color.FromArgb(148, 163, 184));
            var bTraceClean = B("Delete Checked Items", theme.AccentRed);
            var bTraceDel = B("Remove Log", Color.FromArgb(148, 163, 184));
            top.Controls.AddRange(new Control[] { L("Name:"), tTraceName, L("Setup:"), tTraceInstaller, bTraceBrowse, bTraceStart, bTraceStop, bTraceExpHtml, bTraceExpJson, bTraceImport, bTraceClean, bTraceDel });
            lTraceActive = new Label
            {
                Dock = DockStyle.Top,
                Height = 26,
                BackColor = theme.BgHeader,
                ForeColor = theme.AccentBlue,
                Padding = new Padding(10, 5, 10, 0),
                Text = "Monitor idle. Click 'Install & Monitor' before running an installer to record every created folder, file, and registry key.",
            };
            gTraceItems = new DataGridView { Dock = DockStyle.Bottom, Height = 230 };
            GridHelper.Style(gTraceItems, theme);
            allGrids.Add(gTraceItems);
            tpTraces.Controls.Add(lTraceActive);
            tpTraces.Controls.Add(gTraceItems);
            gTraces.BringToFront();
            lTraceActive.SendToBack();
            top.SendToBack();

            bTraceBrowse.Click += (s, e) =>
            {
                using (var d = new OpenFileDialog { Filter = "Installers (*.exe;*.msi)|*.exe;*.msi|All files (*.*)|*.*" })
                {
                    if (d.ShowDialog(this) == DialogResult.OK)
                    {
                        SetInput(tTraceInstaller, d.FileName);
                        if (string.IsNullOrEmpty(GetInput(tTraceName))) SetInput(tTraceName, Path.GetFileNameWithoutExtension(d.FileName));
                    }
                }
            };
            bTraceStart.Click += (s, e) => StartTraceAsync();
            bTraceStop.Click += (s, e) => StopTraceAsync();
            bTraceExpHtml.Click += (s, e) => ExportSelectedTraceAsync("html");
            bTraceExpJson.Click += (s, e) => ExportSelectedTraceAsync("json");
            bTraceImport.Click += (s, e) => ImportTraceAsync();
            gTraces.SelectionChanged += (s, e) => { if (tabs.SelectedTab == tpTraces && gTraces.SelectedRows.Count > 0) LoadSelectedTraceItemsAsync(); };
            bTraceClean.Click += (s, e) => DeleteCheckedTraceItems();
            bTraceDel.Click += (s, e) => DeleteSelectedTraceLog();

            // 5. Autorun Manager
            tpAutoruns = NewGridTab("Autorun Manager", out top, out gAutoruns);
            var bAAdd = B("Add Entry...", theme.AccentGreen);
            var bALaunch = B("Launch Now", theme.AccentBlue);
            var bAToggle = B("Enable / Disable", theme.AccentAmber);
            var bAKill = B("Kill Running", theme.AccentRed);
            var bARemove = B("Remove", theme.AccentRed);
            var bACleanInvalid = B("Remove All Invalid", theme.AccentAmber);
            var bAR = B("Refresh", theme.AccentBlue);
            top.Controls.AddRange(new Control[] { bAAdd, bALaunch, bAToggle, bAKill, bARemove, bACleanInvalid, bAR });
            bAAdd.Click += (s, e) => AddAutorunDialog();
            bALaunch.Click += (s, e) => LaunchSelectedAutorun();
            bAToggle.Click += (s, e) => ToggleSelectedAutorun();
            bAKill.Click += (s, e) => KillSelectedAutorunProcess();
            bARemove.Click += (s, e) => RemoveSelectedAutorun();
            bACleanInvalid.Click += (s, e) =>
            {
                if (MessageBox.Show(this, "Remove all INVALID startup entries whose target executables do not exist?", "Confirm Clean", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                DoActionAsync("autorun-clean-invalid", null, o => LoadAsync("autoruns", gAutoruns, false, false, null, AutorunIconSelector));
            };
            bAR.Click += (s, e) => LoadAsync("autoruns", gAutoruns, false, false, null, AutorunIconSelector);
            AttachAutorunContextMenu();

            // 6. Windows Apps
            tpWinApps = NewGridTab("Windows Apps", out top, out gWinApps);
            tWinAppFilter = T("Filter Store apps...", 210);
            var bWSearch = B("Filter", theme.AccentBlue);
            var bWUninstall = B("Uninstall UWP", theme.AccentRed);
            var bWReset = B("Reset Cache/App", theme.AccentAmber);
            var bWLeft = B("Scan Leftovers", theme.AccentBlue);
            var bWFolder = B("Open Folder", Color.FromArgb(148, 163, 184));
            var bWR = B("Refresh", theme.AccentBlue);
            top.Controls.AddRange(new Control[] { L("Search:"), tWinAppFilter, bWSearch, bWUninstall, bWReset, bWLeft, bWFolder, bWR });
            bWSearch.Click += (s, e) => FilterWinApps();
            tWinAppFilter.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; FilterWinApps(); } };
            bWUninstall.Click += (s, e) => UninstallSelectedWinApp();
            bWReset.Click += (s, e) => ResetSelectedWinApp();
            bWLeft.Click += (s, e) =>
            {
                if (gWinApps.SelectedRows.Count == 0) return;
                string nm = GridHelper.GetCell(gWinApps, gWinApps.SelectedRows[0].Index, "DisplayName");
                SetInput(tLeft, nm);
                SelectNavTab(3);
                LeftoversAsync(nm, Convert.ToString(cLeftMode.SelectedItem));
            };
            bWFolder.Click += (s, e) => OpenSelectedWinAppFolder();
            bWR.Click += (s, e) => LoadAsync("winapps", gWinApps, false, false, null, null);
            AttachWinAppsContextMenu();

            // 7. Tracks Cleaner
            tpTracks = NewGridTab("Tracks Cleaner", out top, out gTracks);
            var bTracksAll = B("Check All", theme.AccentBlue);
            var bTracksNone = B("Uncheck All", Color.FromArgb(148, 163, 184));
            var bClean = B("Clean Selected", theme.AccentRed);
            var bTRef = B("Rescan", theme.AccentBlue);
            top.Controls.AddRange(new Control[] { bTracksAll, bTracksNone, bClean, bTRef });
            tTrackOut = new TextBox { Dock = DockStyle.Bottom, Height = 90, Multiline = true, ReadOnly = true, BackColor = theme.BgGridHeader, ForeColor = theme.TextPrimary, ScrollBars = ScrollBars.Both };
            tpTracks.Controls.Add(tTrackOut);
            gTracks.BringToFront();
            bTracksAll.Click += (s, e) => GridHelper.ToggleAllChecks(gTracks, true);
            bTracksNone.Click += (s, e) => GridHelper.ToggleAllChecks(gTracks, false);
            bClean.Click += (s, e) => CleanTracks();
            bTRef.Click += (s, e) => LoadAsync("tracks", gTracks, true, false, null, null);

            // 8. Browser Extensions
            tpExtensions = NewGridTab("Browser Extensions", out top, out gExtensions);
            var bToggle = B("Enable / Disable", theme.AccentAmber);
            var bExtOpen = B("Open Extension Folder", Color.FromArgb(148, 163, 184));
            var bER = B("Refresh", theme.AccentBlue);
            top.Controls.AddRange(new Control[] { bToggle, bExtOpen, bER });
            bToggle.Click += (s, e) => ToggleExtension();
            bExtOpen.Click += (s, e) =>
            {
                if (gExtensions.SelectedRows.Count == 0) return;
                string p = GridHelper.GetCell(gExtensions, gExtensions.SelectedRows[0].Index, "path");
                if (!string.IsNullOrEmpty(p) && Directory.Exists(p)) Process.Start("explorer.exe", p);
            };
            bER.Click += (s, e) => LoadAsync("extensions", gExtensions, false, false, null, null);

            // 9. Junk Files
            tpJunk = NewGridTab("Junk Files", out top, out gJunk);
            chkJunkRecent = new CheckBox { Text = "Include < 24h", AutoSize = true, ForeColor = theme.TextMuted, Margin = new Padding(6, 8, 6, 0) };
            tJunkCustom = T("Optional custom folder to scan", 220);
            var bJunkBrowse = B("Browse...", Color.FromArgb(148, 163, 184));
            var bJR = B("Scan Junk", theme.AccentBlue);
            var bJunkAll = B("Check All", theme.AccentBlue);
            var bJunkNone = B("Uncheck All", Color.FromArgb(148, 163, 184));
            var bJClean = B("Clean Checked Files", theme.AccentRed);
            top.Controls.AddRange(new Control[] { chkJunkRecent, L("Folder:"), tJunkCustom, bJunkBrowse, bJR, bJunkAll, bJunkNone, bJClean });
            lJunkSummary = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 42,
                BackColor = theme.BgHeader,
                ForeColor = theme.TextMuted,
                Padding = new Padding(10, 6, 10, 6),
                Text = "Individual junk files (*.tmp, *.log, *.dmp, Prefetch, INetCache, WER) will appear above.",
            };
            tpJunk.Controls.Add(lJunkSummary);
            gJunk.BringToFront();
            bJunkBrowse.Click += (s, e) => { using (var d = new FolderBrowserDialog()) { if (d.ShowDialog(this) == DialogResult.OK) SetInput(tJunkCustom, d.SelectedPath); } };
            bJR.Click += (s, e) => LoadJunkDetailedAsync();
            bJunkAll.Click += (s, e) => GridHelper.ToggleAllChecks(gJunk, true);
            bJunkNone.Click += (s, e) => GridHelper.ToggleAllChecks(gJunk, false);
            bJClean.Click += (s, e) => CleanSelectedJunk();

            // 10. Backup & Restore
            tpBackups = NewGridTab("Backup & Restore", out top, out gBackups);
            var bMake = B("Create Registry Backup", theme.AccentGreen);
            var bRestore = B("Restore Selected", theme.AccentBlue);
            var bBkDelete = B("Delete Selected", theme.AccentRed);
            var bBkPurge = B("Purge >30 Days", theme.AccentAmber);
            var bBkOpen = B("Open Backups Folder", Color.FromArgb(148, 163, 184));
            var bBkRef = B("Refresh", theme.AccentBlue);
            top.Controls.AddRange(new Control[] { bMake, bRestore, bBkDelete, bBkPurge, bBkOpen, bBkRef });
            tBackupOut = new TextBox { Dock = DockStyle.Bottom, Height = 90, Multiline = true, ReadOnly = true, BackColor = theme.BgGridHeader, ForeColor = theme.TextPrimary, ScrollBars = ScrollBars.Both };
            tpBackups.Controls.Add(tBackupOut);
            gBackups.BringToFront();
            bMake.Click += (s, e) => DoActionAsync("backup", null, o => { tBackupOut.Text = Bridge.PrettyDump(o); LoadAsync("backups", gBackups, false, false, null, null); });
            bRestore.Click += (s, e) => RestoreBackup();
            bBkDelete.Click += (s, e) => DeleteSelectedBackup();
            bBkPurge.Click += (s, e) =>
            {
                if (MessageBox.Show(this, "Delete all backup files/folders older than 30 days?", "Confirm Purge", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                DoActionAsync("backup-clean-old", new { days = 30 }, o => { tBackupOut.Text = Bridge.PrettyDump(o); LoadAsync("backups", gBackups, false, false, null, null); });
            };
            bBkOpen.Click += (s, e) => { Directory.CreateDirectory(Bridge.BackupsPath); Process.Start("explorer.exe", Bridge.BackupsPath); };
            bBkRef.Click += (s, e) => LoadAsync("backups", gBackups, false, false, null, null);

            // 11. Registry Optimizer
            tpRegistry = NewGridTab("Registry Optimizer", out top, out gRegistry);
            var bRegAll = B("Check All", theme.AccentBlue);
            var bRegNone = B("Uncheck All", Color.FromArgb(148, 163, 184));
            var bRegClean = B("Backup & Remove Checked", theme.AccentRed);
            var bRR = B("Rescan", theme.AccentBlue);
            top.Controls.AddRange(new Control[] { bRegAll, bRegNone, bRegClean, bRR });
            bRegAll.Click += (s, e) => GridHelper.ToggleAllChecks(gRegistry, true);
            bRegNone.Click += (s, e) => GridHelper.ToggleAllChecks(gRegistry, false);
            bRegClean.Click += (s, e) => CleanSelectedRegistry();
            bRR.Click += (s, e) => LoadAsync("registry-invalid", gRegistry, true, true, null, null);

            // 12. Windows Tools
            tpTools = NewCustomTab("Windows Tools", out top);
            var bLaunch = B("Launch Selected Tool", theme.AccentGreen);
            var bTR = B("Refresh", theme.AccentBlue);
            top.Controls.AddRange(new Control[] { bLaunch, bTR });
            lTools = new ListBox
            {
                Dock = DockStyle.Fill,
                BackColor = theme.BgGrid,
                ForeColor = theme.TextPrimary,
                Font = new Font("Segoe UI", 10.2f),
                ItemHeight = 28,
                BorderStyle = BorderStyle.None,
            };
            lTools.DoubleClick += (s, e) => LaunchTool();
            tpTools.Controls.Add(lTools);
            lTools.BringToFront();
            bLaunch.Click += (s, e) => LaunchTool();
            bTR.Click += (s, e) => LoadToolsAsync();

            // 13. Evidence Remover
            tpEvidence = NewCustomTab("Evidence Remover", out top);
            var bAddEvFile = B("Add File...", theme.AccentBlue);
            var bAddEvDir = B("Add Folder...", theme.AccentBlue);
            var bRemEvRow = B("Remove Selected", Color.FromArgb(148, 163, 184));
            var bClearEvList = B("Clear List", Color.FromArgb(148, 163, 184));
            cEvidencePasses = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 80, BackColor = theme.BgInput, ForeColor = theme.TextPrimary, Margin = new Padding(3, 5, 3, 3) };
            cEvidencePasses.Items.AddRange(new object[] { "1", "3", "7" });
            cEvidencePasses.SelectedIndex = 1;
            var bDelBatch = B("Unrecoverable Delete Listed", theme.AccentRed);
            top.Controls.AddRange(new Control[] { bAddEvFile, bAddEvDir, bRemEvRow, bClearEvList, L("Passes:"), cEvidencePasses, bDelBatch });

            gEvidenceTargets = new DataGridView { Dock = DockStyle.Top, Height = 175 };
            GridHelper.Style(gEvidenceTargets, theme);
            allGrids.Add(gEvidenceTargets);
            gEvidenceTargets.Columns.Add(new DataGridViewTextBoxColumn { Name = "Kind", HeaderText = "Kind", Width = 90, AutoSizeMode = DataGridViewAutoSizeColumnMode.None });
            gEvidenceTargets.Columns.Add(new DataGridViewTextBoxColumn { Name = "Path", HeaderText = "Target File / Folder Path" });
            gEvidenceTargets.Columns.Add(new DataGridViewTextBoxColumn { Name = "Info", HeaderText = "Info", Width = 160, AutoSizeMode = DataGridViewAutoSizeColumnMode.None });

            var wipePanel = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8), BackColor = theme.BgHeader };
            toolbarPanels.Add(wipePanel);
            tWipeFolder = T("select a drive below or enter folder (e.g. D:\\)", 300);
            tWipeMb = T("16", 60);
            SetInput(tWipeMb, "16");
            var bWipe = B("Wipe Free Space (MB)", theme.AccentAmber);
            var bDriveRef = B("Refresh Drives", theme.AccentBlue);
            wipePanel.Controls.AddRange(new Control[] { L("Free-space target:"), tWipeFolder, L("MB:"), tWipeMb, bWipe, bDriveRef });

            gDrives = new DataGridView { Dock = DockStyle.Fill };
            GridHelper.Style(gDrives, theme);
            allGrids.Add(gDrives);
            gDrives.SelectionChanged += (s, e) =>
            {
                if (gDrives.SelectedRows.Count > 0)
                {
                    string drvPath = GridHelper.GetCell(gDrives, gDrives.SelectedRows[0].Index, "Path");
                    if (!string.IsNullOrEmpty(drvPath)) SetInput(tWipeFolder, drvPath);
                }
            };

            var warn = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 60,
                Padding = new Padding(10, 6, 10, 6),
                Font = new Font("Segoe UI", 8.6f),
                ForeColor = theme.AccentAmber,
                BackColor = theme.BgToolbar,
                Text = "Unrecoverable Delete overwrites files (0x00, 0xFF, CSPRNG random bytes), renames randomly, and unlinks.\r\n" +
                       "Wipe Free Space writes bounded random bytes into unallocated space on the selected drive and removes temp wipe data.\r\n" +
                       "Limitation: SSD/NVMe wear leveling and NTFS journaling mean file-level overwrites cannot guarantee physical erasure.",
            };

            tpEvidence.Controls.Add(gDrives);
            tpEvidence.Controls.Add(warn);
            tpEvidence.Controls.Add(wipePanel);
            tpEvidence.Controls.Add(gEvidenceTargets);
            gDrives.BringToFront();
            wipePanel.SendToBack();
            gEvidenceTargets.SendToBack();
            top.SendToBack();

            bAddEvFile.Click += (s, e) =>
            {
                using (var d = new OpenFileDialog { Multiselect = true })
                {
                    if (d.ShowDialog(this) == DialogResult.OK) foreach (var f in d.FileNames) AddEvidenceTarget("File", f);
                }
            };
            bAddEvDir.Click += (s, e) =>
            {
                using (var d = new FolderBrowserDialog())
                {
                    if (d.ShowDialog(this) == DialogResult.OK) AddEvidenceTarget("Folder", d.SelectedPath);
                }
            };
            bRemEvRow.Click += (s, e) => { if (gEvidenceTargets.SelectedRows.Count > 0) gEvidenceTargets.Rows.RemoveAt(gEvidenceTargets.SelectedRows[0].Index); };
            bClearEvList.Click += (s, e) => gEvidenceTargets.Rows.Clear();
            bDelBatch.Click += (s, e) => SecureDeleteBatch();
            bWipe.Click += (s, e) => WipeFreeSpace();
            bDriveRef.Click += (s, e) => LoadAsync("drives", gDrives, false, false, null, null);

            // 14. Logs & History
            tpHistory = NewGridTab("Logs & History", out top, out gHistory);
            cHistorySource = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 180, BackColor = theme.BgInput, ForeColor = theme.TextPrimary, Margin = new Padding(3, 5, 3, 3) };
            cHistorySource.Items.AddRange(new object[] { "Local Operation Log", "Reference DB (optional)" });
            cHistorySource.SelectedIndex = 0;
            tHistorySearch = T("search reference logs (e.g. Chrome)", 230);
            var bHR = B("Load / Search", theme.AccentBlue);
            var bHScanLeft = B("Scan Leftovers for Selected", theme.AccentGreen);
            var bHClear = B("Clear Local Log", theme.AccentRed);
            top.Controls.AddRange(new Control[] { cHistorySource, tHistorySearch, bHR, bHScanLeft, bHClear });
            bHR.Click += (s, e) => LoadHistoryOrRefLogsAsync();
            tHistorySearch.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; LoadHistoryOrRefLogsAsync(); } };
            cHistorySource.SelectedIndexChanged += (s, e) => LoadHistoryOrRefLogsAsync();
            bHScanLeft.Click += (s, e) => SendHistoryRowToLeftovers();
            bHClear.Click += (s, e) =>
            {
                if (MessageBox.Show(this, "Clear all local operation history rows?", "Confirm", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                DoActionAsync("history-clear", null, o => LoadHistoryOrRefLogsAsync());
            };

            // 15. Settings
            tpSettings = NewCustomTab("Settings", out top);
            var bSave = B("Save & Apply Settings", theme.AccentGreen);
            var bReloadSet = B("Reload", theme.AccentBlue);
            top.Controls.AddRange(new Control[] { bSave, bReloadSet });

            var formPanel = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 222,
                ColumnCount = 2,
                Padding = new Padding(20, 12, 20, 10),
                BackColor = theme.BgHeader,
            };
            formPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 270));
            formPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            tSetProduct = new TextBox { Width = 280, BackColor = theme.BgInput, ForeColor = theme.TextPrimary };
            cSetLeftMode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160, BackColor = theme.BgInput, ForeColor = theme.TextPrimary };
            cSetLeftMode.Items.AddRange(new object[] { "safe", "moderate", "advanced" });
            cSetLeftMode.SelectedIndex = 1;
            cSetTheme = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160, BackColor = theme.BgInput, ForeColor = theme.TextPrimary };
            cSetTheme.Items.AddRange(new object[] { "dark", "midnight", "light" });
            cSetTheme.SelectedIndex = 0;
            tSetHistoryLimit = new TextBox { Width = 100, BackColor = theme.BgInput, ForeColor = theme.TextPrimary };
            chkSetConfirm = new CheckBox { Text = "Prompt for confirmation before cleaning/deleting", AutoSize = true, ForeColor = theme.TextPrimary };
            chkSetBackupOnUninstall = new CheckBox { Text = "Create Registry backup of Uninstall key before uninstalling", AutoSize = true, ForeColor = theme.TextPrimary };
            chkSetDryRun = new CheckBox { Text = "Dry-run default where supported", AutoSize = true, ForeColor = theme.TextPrimary };

            formPanel.Controls.Add(L("Application Title:"), 0, 0); formPanel.Controls.Add(tSetProduct, 1, 0);
            formPanel.Controls.Add(L("Default Leftover Scan Mode:"), 0, 1); formPanel.Controls.Add(cSetLeftMode, 1, 1);
            formPanel.Controls.Add(L("Color Theme (dark/midnight/light):"), 0, 2); formPanel.Controls.Add(cSetTheme, 1, 2);
            formPanel.Controls.Add(L("Operation History Limit:"), 0, 3); formPanel.Controls.Add(tSetHistoryLimit, 1, 3);
            formPanel.Controls.Add(L("Safety Prompts:"), 0, 4); formPanel.Controls.Add(chkSetConfirm, 1, 4);
            formPanel.Controls.Add(L("Pre-Uninstall Backup:"), 0, 5); formPanel.Controls.Add(chkSetBackupOnUninstall, 1, 5);
            formPanel.Controls.Add(L("Simulation Mode:"), 0, 6); formPanel.Controls.Add(chkSetDryRun, 1, 6);

            tSettingsPreview = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                TabStop = false,
                BackColor = theme.BgGridHeader,
                ForeColor = theme.TextMuted,
                Font = new Font("Consolas", 9.5f),
                ScrollBars = ScrollBars.Both,
                BorderStyle = BorderStyle.None,
                Padding = new Padding(10),
            };
            tpSettings.Controls.Add(tSettingsPreview);
            tpSettings.Controls.Add(formPanel);
            tSettingsPreview.BringToFront();
            formPanel.SendToBack();
            top.SendToBack();
            bSave.Click += (s, e) => SaveSettings();
            bReloadSet.Click += (s, e) => LoadSettingsAsync();
        }

        private void AddKpiCard(TableLayoutPanel grid, int col, int row, string title, string val, string sub, Color accent, int targetTab)
        {
            var card = new KpiCardControl(title, val, sub, accent, theme) { Dock = DockStyle.Fill };
            card.Click += (s, e) => SelectNavTab(targetTab);
            kpiCards.Add(card);
            grid.Controls.Add(card, col, row);
        }

        private Bitmap ProgramIconSelector(Dictionary<string, object> d)
        {
            if (d == null) return IconCache.GetIcon("", "", "?");
            string ico = d.ContainsKey("DisplayIcon") ? Convert.ToString(d["DisplayIcon"]) : "";
            string loc = d.ContainsKey("InstallLocation") ? Convert.ToString(d["InstallLocation"]) : "";
            string dn = d.ContainsKey("DisplayName") ? Convert.ToString(d["DisplayName"]) : "?";
            return IconCache.GetIcon(ico, loc, dn);
        }

        private Bitmap ProcIconSelector(Dictionary<string, object> d)
        {
            if (d == null) return IconCache.GetIcon("", "", "?");
            string p = d.ContainsKey("Path") ? Convert.ToString(d["Path"]) : "";
            string n = d.ContainsKey("Name") ? Convert.ToString(d["Name"]) : "?";
            return IconCache.GetIcon(p, "", n);
        }

        private Bitmap AutorunIconSelector(Dictionary<string, object> d)
        {
            if (d == null) return IconCache.GetIcon("", "", "?");
            string cmd = d.ContainsKey("Command") ? Convert.ToString(d["Command"]) : "";
            string n = d.ContainsKey("Name") ? Convert.ToString(d["Name"]) : "?";
            return IconCache.GetIcon(cmd, "", n);
        }

        private void ApplyTheme(string themeName)
        {
            theme = ThemePalette.Get(themeName);
            BackColor = theme.BgApp;
            ForeColor = theme.TextPrimary;
            if (mainMenu != null) { mainMenu.BackColor = theme.BgSidebar; mainMenu.ForeColor = theme.TextPrimary; }
            if (status != null) { status.BackColor = theme.BgHeader; status.ForeColor = theme.TextMuted; }
            if (statusLabel != null) statusLabel.ForeColor = theme.TextMuted;
            if (sidebar != null) sidebar.BackColor = theme.BgSidebar;
            if (navFlow != null) navFlow.BackColor = theme.BgSidebar;
            if (lBrandTitle != null) lBrandTitle.ForeColor = theme.TextPrimary;
            if (lBrandSub != null) lBrandSub.ForeColor = theme.TextMuted;
            if (headerPanel != null) headerPanel.BackColor = theme.BgHeader;
            if (lHeaderTitle != null) lHeaderTitle.ForeColor = theme.TextPrimary;
            if (lHeaderSub != null) lHeaderSub.ForeColor = theme.TextMuted;
            if (dashCardContainer != null) dashCardContainer.BackColor = theme.BgApp;
            if (drivesBar != null) { drivesBar.Theme = theme; drivesBar.Invalidate(); }
            if (lProgDetails != null) { lProgDetails.BackColor = theme.BgHeader; lProgDetails.ForeColor = theme.TextMuted; }
            if (lTraceActive != null) { lTraceActive.BackColor = theme.BgHeader; }
            if (lJunkSummary != null) { lJunkSummary.BackColor = theme.BgHeader; lJunkSummary.ForeColor = theme.TextMuted; }
            if (lTools != null) { lTools.BackColor = theme.BgGrid; lTools.ForeColor = theme.TextPrimary; }
            if (tSettingsPreview != null) { tSettingsPreview.BackColor = theme.BgGridHeader; tSettingsPreview.ForeColor = theme.TextMuted; }

            foreach (TabPage tp in tabs.TabPages) tp.BackColor = theme.BgApp;
            foreach (var p in toolbarPanels) p.BackColor = theme.BgToolbar;
            foreach (var l in navSectionLabels) l.ForeColor = theme.TextMuted;
            foreach (var b in navButtons) { b.Theme = theme; b.Invalidate(); }
            foreach (var c in kpiCards) { c.Theme = theme; c.Invalidate(); }
            foreach (var g in allGrids) GridHelper.Style(g, theme);
            Invalidate(true);
        }

        private void AttachProgramContextMenu()
        {
            var cm = new ContextMenuStrip();
            var mUninst = new ToolStripMenuItem("Uninstall (Guided Wizard)...");
            mUninst.Click += (s, e) => OpenUninstallWizard(false);
            var mQuiet = new ToolStripMenuItem("Quick / Quiet Uninstall");
            mQuiet.Click += (s, e) => HandleProgramUninstallClick(true);
            var mForced = new ToolStripMenuItem("Forced Uninstall for Program...");
            mForced.Click += (s, e) =>
            {
                if (gPrograms.SelectedRows.Count == 0) return;
                string loc = GridHelper.GetCell(gPrograms, gPrograms.SelectedRows[0].Index, "InstallLocation");
                string nm = GridHelper.GetCell(gPrograms, gPrograms.SelectedRows[0].Index, "DisplayName");
                OpenForcedUninstall(loc, nm);
            };
            var mRepair = new ToolStripMenuItem("Change / Repair");
            mRepair.Click += (s, e) => ChangeOrRepairSelectedProgram();
            var mLeft = new ToolStripMenuItem("Scan Leftovers");
            mLeft.Click += (s, e) => SendSelectedProgramToLeftovers();
            var mFolder = new ToolStripMenuItem("Open Install Folder");
            mFolder.Click += (s, e) => OpenSelectedProgramFolder();
            var mRegEdit = new ToolStripMenuItem("Open in Registry Editor (RegEdit)");
            mRegEdit.Click += (s, e) => OpenSelectedProgramInRegEdit();
            var mUrl = new ToolStripMenuItem("Open Publisher URL");
            mUrl.Click += (s, e) => OpenSelectedProgramUrl();
            var mCopyCmd = new ToolStripMenuItem("Copy Uninstall Command");
            mCopyCmd.Click += (s, e) => CopySelectedUninstallCommand();
            var mDelEntry = new ToolStripMenuItem("Remove Entry from Registry (Stale)");
            mDelEntry.Click += (s, e) => RemoveSelectedProgramEntry();

            cm.Items.AddRange(new ToolStripItem[]
            {
                mUninst, mQuiet, mForced, mRepair, mLeft, new ToolStripSeparator(),
                mFolder, mRegEdit, mUrl, mCopyCmd, new ToolStripSeparator(), mDelEntry
            });
            gPrograms.ContextMenuStrip = cm;
        }

        private void AttachHunterContextMenus()
        {
            var cmProc = new ContextMenuStrip();
            var mHunt = new ToolStripMenuItem("Hunt Linked Program");
            mHunt.Click += (s, e) =>
            {
                if (gHuntProc.SelectedRows.Count == 0) return;
                string p = GridHelper.GetCell(gHuntProc, gHuntProc.SelectedRows[0].Index, "Path");
                string n = GridHelper.GetCell(gHuntProc, gHuntProc.SelectedRows[0].Index, "Name");
                string target = !string.IsNullOrEmpty(p) ? p : n;
                SetInput(tHunt, target);
                HuntAsync(target);
            };
            var mOpenExe = new ToolStripMenuItem("Open Process Folder");
            mOpenExe.Click += (s, e) =>
            {
                if (gHuntProc.SelectedRows.Count == 0) return;
                string p = GridHelper.GetCell(gHuntProc, gHuntProc.SelectedRows[0].Index, "Path");
                if (!string.IsNullOrEmpty(p) && File.Exists(p)) Process.Start("explorer.exe", "/select,\"" + p + "\"");
            };
            var mKill = new ToolStripMenuItem("Kill Process");
            mKill.Click += (s, e) => KillSelectedHuntedProcess();
            cmProc.Items.AddRange(new ToolStripItem[] { mHunt, mOpenExe, new ToolStripSeparator(), mKill });
            gHuntProc.ContextMenuStrip = cmProc;

            var cmProg = new ContextMenuStrip();
            var mW = new ToolStripMenuItem("Uninstall (Guided Wizard)...");
            mW.Click += (s, e) =>
            {
                if (gHuntProg.SelectedRows.Count == 0) return;
                var d = GridHelper.GetRowDict(gHuntProg, gHuntProg.SelectedRows[0].Index);
                if (d != null) OpenWizardForDict(d, false);
            };
            var mSL = new ToolStripMenuItem("Scan Leftovers");
            mSL.Click += (s, e) => SendHuntedToLeftovers();
            cmProg.Items.AddRange(new ToolStripItem[] { mW, mSL });
            gHuntProg.ContextMenuStrip = cmProg;
        }

        private void AttachAutorunContextMenu()
        {
            var cm = new ContextMenuStrip();
            var mLaunch = new ToolStripMenuItem("Launch Now");
            mLaunch.Click += (s, e) => LaunchSelectedAutorun();
            var mTog = new ToolStripMenuItem("Enable / Disable");
            mTog.Click += (s, e) => ToggleSelectedAutorun();
            var mKill = new ToolStripMenuItem("Kill Running Process");
            mKill.Click += (s, e) => KillSelectedAutorunProcess();
            var mFolder = new ToolStripMenuItem("Open Containing Folder");
            mFolder.Click += (s, e) => OpenSelectedAutorunFolder();
            var mRem = new ToolStripMenuItem("Remove Entry");
            mRem.Click += (s, e) => RemoveSelectedAutorun();
            cm.Items.AddRange(new ToolStripItem[] { mLaunch, mTog, mFolder, mKill, new ToolStripSeparator(), mRem });
            gAutoruns.ContextMenuStrip = cm;
        }

        private void AttachWinAppsContextMenu()
        {
            var cm = new ContextMenuStrip();
            var mUn = new ToolStripMenuItem("Uninstall Store App");
            mUn.Click += (s, e) => UninstallSelectedWinApp();
            var mRst = new ToolStripMenuItem("Reset App & Clear Cache");
            mRst.Click += (s, e) => ResetSelectedWinApp();
            var mFolder = new ToolStripMenuItem("Open Install Folder");
            mFolder.Click += (s, e) => OpenSelectedWinAppFolder();
            var mStore = new ToolStripMenuItem("Search in Microsoft Store");
            mStore.Click += (s, e) =>
            {
                if (gWinApps.SelectedRows.Count == 0) return;
                string nm = GridHelper.GetCell(gWinApps, gWinApps.SelectedRows[0].Index, "DisplayName");
                if (!string.IsNullOrEmpty(nm)) Process.Start("ms-windows-store://search/?query=" + Uri.EscapeDataString(nm));
            };
            cm.Items.AddRange(new ToolStripItem[] { mUn, mRst, mFolder, mStore });
            gWinApps.ContextMenuStrip = cm;
        }

        private void LaunchHunterOverlay()
        {
            if (overlayForm != null && !overlayForm.IsDisposed) { overlayForm.BringToFront(); return; }
            overlayForm = new HunterOverlayForm(this);
            overlayForm.FormClosed += (s, e) => { overlayForm = null; };
            overlayForm.Show();
            WindowState = FormWindowState.Minimized;
            Status("Floating Hunter Crosshair active — drag it over any window and release, or right-click it.");
        }

        public async void RestoreFromHunterOverlay(string target, bool goToLeftovers)
        {
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            BringToFront();
            Activate();
            if (string.IsNullOrEmpty(target)) return;
            if (goToLeftovers)
            {
                SelectNavTab(3);
                SetInput(tLeft, Path.GetFileNameWithoutExtension(target));
                await LeftoversAsync(GetInput(tLeft), Convert.ToString(cLeftMode.SelectedItem));
            }
            else
            {
                SelectNavTab(2);
                SetInput(tHunt, target);
                await HuntAsync(target);
            }
        }

        private async Task LoadAllAsync()
        {
            try
            {
                Status("Loading all system modules via local bridge...");
                var all = await Task.Run(() => Bridge.Call("data", "all", null)) as Dictionary<string, object>;
                if (all != null)
                {
                    cachedPrograms = (all.ContainsKey("programs") ? all["programs"] as object[] : null) ?? new object[0];
                    ApplyProgramFilter();

                    if (all.ContainsKey("traces")) PopulateTraces(all["traces"] as Dictionary<string, object>);
                    GridHelper.Fill(gHuntProc, all.ContainsKey("processes") ? all["processes"] : null, false, false, null, ProcIconSelector);
                    GridHelper.Fill(gAutoruns, all.ContainsKey("autoruns") ? all["autoruns"] : null, false, false, null, AutorunIconSelector);
                    GridHelper.Fill(gWinApps, all.ContainsKey("winapps") ? all["winapps"] : null, false, false, null, null);

                    if (all.ContainsKey("junkDetailed")) PopulateJunkDetailed(all["junkDetailed"] as Dictionary<string, object>);
                    else GridHelper.Fill(gJunk, all.ContainsKey("junk") ? all["junk"] : null, true, true, null, null);

                    GridHelper.Fill(gTracks, all.ContainsKey("tracks") ? all["tracks"] : null, true, false, null, null);
                    GridHelper.Fill(gExtensions, all.ContainsKey("extensions") ? all["extensions"] : null, false, false, null, null);
                    GridHelper.Fill(gBackups, all.ContainsKey("backups") ? all["backups"] : null, false, false, null, null);
                    GridHelper.Fill(gRegistry, all.ContainsKey("registryInvalid") ? all["registryInvalid"] : null, true, true, null, null);

                    object[] drvArr = all.ContainsKey("drives") ? all["drives"] as object[] : null;
                    GridHelper.Fill(gDrives, drvArr, false, false, null, null);
                    if (drivesBar != null) { drivesBar.Drives = drvArr; drivesBar.Invalidate(); }

                    object[] histArr = all.ContainsKey("history") ? all["history"] as object[] : null;
                    GridHelper.Fill(gHistory, histArr, false, false, null, null);
                    GridHelper.Fill(gDashRecent, histArr != null ? histArr.Take(8).ToArray() : null, false, false, null, null);

                    PopulateTools(all.ContainsKey("wintools") ? all["wintools"] as object[] : null);
                    if (all.ContainsKey("settings")) PopulateSettingsUi(all["settings"] as Dictionary<string, object>);
                    UpdateDashboardSummary();
                    Status("Loaded: " + gPrograms.Rows.Count + " programs, " + gWinApps.Rows.Count + " Store apps, " +
                           gExtensions.Rows.Count + " extensions, " + gJunk.Rows.Count + " junk files.");
                }
            }
            catch (Exception ex)
            {
                initialLoadError = ex.Message;
                Status("Load error: " + ex.Message);
            }
            finally
            {
                initialLoadFinished = true;
            }
        }

        private void ApplyProgramFilter()
        {
            string q = GetInput(tSearch);
            Regex qRegex = null;
            if (!string.IsNullOrEmpty(q) && (q.IndexOf('*') >= 0 || q.IndexOf('?') >= 0))
            {
                string pat = "^" + Regex.Escape(q).Replace("\\*", ".*").Replace("\\?", ".") + "$";
                qRegex = new Regex(pat, RegexOptions.IgnoreCase);
            }
            string qLower = q.ToLowerInvariant();
            string cat = cProgCategory != null && cProgCategory.SelectedItem != null ? Convert.ToString(cProgCategory.SelectedItem) : "All";
            var filtered = new List<object>();
            foreach (var o in cachedPrograms)
            {
                var d = o as Dictionary<string, object>;
                if (d == null) continue;
                string name = d.ContainsKey("DisplayName") && d["DisplayName"] != null ? Convert.ToString(d["DisplayName"]) : "";
                string pub = d.ContainsKey("Publisher") && d["Publisher"] != null ? Convert.ToString(d["Publisher"]) : "";
                string arch = d.ContainsKey("Architecture") && d["Architecture"] != null ? Convert.ToString(d["Architecture"]) : "";
                string progCat = d.ContainsKey("Category") && d["Category"] != null ? Convert.ToString(d["Category"]) : "";
                bool recent = d.ContainsKey("RecentInstall") && (d["RecentInstall"] is bool) && (bool)d["RecentInstall"];

                if (!string.IsNullOrEmpty(q))
                {
                    if (qRegex != null)
                    {
                        if (!qRegex.IsMatch(name) && !qRegex.IsMatch(pub)) continue;
                    }
                    else if (name.ToLowerInvariant().IndexOf(qLower) < 0 && pub.ToLowerInvariant().IndexOf(qLower) < 0)
                    {
                        continue;
                    }
                }
                if (cat != "All")
                {
                    if (cat == "New / Recent (30d)")
                    {
                        if (!recent) continue;
                    }
                    else if (cat == "64-bit" || cat == "32-bit" || cat == "User")
                    {
                        if (!arch.Equals(cat, StringComparison.OrdinalIgnoreCase)) continue;
                    }
                    else if (!progCat.Equals(cat, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                }
                filtered.Add(d);
            }
            GridHelper.Fill(gPrograms, filtered.ToArray(), true, false, ProgramColumns, ProgramIconSelector);
            UpdateSelectedProgramDetails();
            UpdateDashboardSummary();
        }

        private void UpdateSelectedProgramDetails()
        {
            if (lProgDetails == null) return;
            if (gPrograms == null || gPrograms.SelectedRows.Count == 0)
            {
                lProgDetails.Text = "Showing " + (gPrograms != null ? gPrograms.Rows.Count : 0) + " program(s). Double-click any row for Uninstall Wizard or check multiple rows for Batch Quick Uninstall.";
                return;
            }
            var d = GridHelper.GetRowDict(gPrograms, gPrograms.SelectedRows[0].Index);
            if (d == null) return;
            string name = d.ContainsKey("DisplayName") ? Convert.ToString(d["DisplayName"]) : "";
            string cmd = d.ContainsKey("UninstallString") ? Convert.ToString(d["UninstallString"]) : "";
            string loc = d.ContainsKey("InstallLocation") ? Convert.ToString(d["InstallLocation"]) : "";
            string url = d.ContainsKey("URLInfoAbout") ? Convert.ToString(d["URLInfoAbout"]) : "";
            lProgDetails.Text = string.Format("Selected: {0}   |   InstallLocation: {1}\r\nUninstallString: {2}   {3}",
                name, string.IsNullOrEmpty(loc) ? "-" : loc, string.IsNullOrEmpty(cmd) ? "-" : cmd, string.IsNullOrEmpty(url) ? "" : "|  URL: " + url);
        }

        private void PopulateTraces(Dictionary<string, object> data)
        {
            if (data == null) return;
            var active = data.ContainsKey("active") ? data["active"] as Dictionary<string, object> : null;
            if (active != null && active.ContainsKey("name"))
            {
                lTraceActive.ForeColor = theme.AccentAmber;
                lTraceActive.Text = string.Format("MONITORING ACTIVE: '{0}' (started {1}) — finish setup then click 'Stop & Save'.",
                    Convert.ToString(active["name"]), Convert.ToString(active["startedAt"]));
            }
            else
            {
                lTraceActive.ForeColor = theme.AccentBlue;
                lTraceActive.Text = "Monitor idle. Click 'Install & Monitor' before running any installer to log created folders, files, and registry keys.";
            }
            GridHelper.Fill(gTraces, data.ContainsKey("logs") ? data["logs"] : null, false, false,
                new string[] { "id", "name", "ItemsCount", "Status", "StartedAt", "StoppedAt", "InstallLocation" }, null);
            if (gTraceItems.Columns.Count == 0)
                GridHelper.Fill(gTraceItems, null, true, false, new string[] { "Kind", "Path", "Detail" }, null);
        }

        private void PopulateJunkDetailed(Dictionary<string, object> jd)
        {
            if (jd == null) return;
            GridHelper.Fill(gJunk, jd.ContainsKey("files") ? jd["files"] : null, true, true, null, null);
            var sum = jd.ContainsKey("summary") ? jd["summary"] as object[] : null;
            if (lJunkSummary != null && sum != null)
            {
                long totalFiles = 0;
                double totalMb = 0;
                foreach (var o in sum)
                {
                    var d = o as Dictionary<string, object>;
                    if (d == null) continue;
                    if (d.ContainsKey("Files") && d["Files"] != null) totalFiles += Convert.ToInt64(d["Files"]);
                    if (d.ContainsKey("SizeMB") && d["SizeMB"] != null) totalMb += Convert.ToDouble(d["SizeMB"]);
                }
                lJunkSummary.Text = string.Format("Scanned {0} root location(s): {1} total junk file(s) ({2:F2} MB). Showing top {3} file(s) above.",
                    sum.Length, totalFiles, totalMb, gJunk.Rows.Count);
            }
        }

        private void PopulateSettingsUi(Dictionary<string, object> s)
        {
            if (s == null) return;
            tSettingsPreview.Text = Bridge.PrettyDump(s);
            tSettingsPreview.SelectionStart = 0;
            tSettingsPreview.SelectionLength = 0;
            if (s.ContainsKey("productName")) tSetProduct.Text = Convert.ToString(s["productName"]);
            if (s.ContainsKey("leftoversDepth"))
            {
                string ld = Convert.ToString(s["leftoversDepth"]);
                if (ld == "1") ld = "safe";
                else if (ld == "2") ld = "moderate";
                else if (ld == "3") ld = "advanced";
                int idx = cSetLeftMode.Items.IndexOf(ld);
                if (idx >= 0) cSetLeftMode.SelectedIndex = idx;
            }
            if (s.ContainsKey("theme"))
            {
                string th = Convert.ToString(s["theme"]);
                int idx = cSetTheme.Items.IndexOf(th);
                if (idx >= 0) cSetTheme.SelectedIndex = idx;
                ApplyTheme(th);
            }
            if (s.ContainsKey("historyLimit")) tSetHistoryLimit.Text = Convert.ToString(s["historyLimit"]);
            chkSetConfirm.Checked = !s.ContainsKey("confirmBeforeClean") || (s["confirmBeforeClean"] is bool && (bool)s["confirmBeforeClean"]);
            chkSetBackupOnUninstall.Checked = s.ContainsKey("backupBeforeUninstall") && (s["backupBeforeUninstall"] is bool) ? (bool)s["backupBeforeUninstall"] : true;
            chkSetDryRun.Checked = s.ContainsKey("dryRunDefault") && (s["dryRunDefault"] is bool) && (bool)s["dryRunDefault"];
        }

        private void UpdateDashboardSummary()
        {
            if (kpiCards.Count >= 8)
            {
                kpiCards[0].ValueText = gPrograms.Rows.Count.ToString();
                kpiCards[1].ValueText = gWinApps.Rows.Count.ToString();
                kpiCards[2].ValueText = gTraces.Rows.Count.ToString();
                kpiCards[3].ValueText = gAutoruns.Rows.Count.ToString();
                kpiCards[4].ValueText = gExtensions.Rows.Count.ToString();
                kpiCards[5].ValueText = gJunk.Rows.Count.ToString();
                kpiCards[6].ValueText = gRegistry.Rows.Count.ToString();
                kpiCards[7].ValueText = gBackups.Rows.Count.ToString();
                foreach (var c in kpiCards) c.Invalidate();
            }

            var countsByTab = new Dictionary<int, string>
            {
                { 1, gPrograms.Rows.Count.ToString() },
                { 2, gHuntProc.Rows.Count.ToString() },
                { 4, gTraces.Rows.Count.ToString() },
                { 5, gAutoruns.Rows.Count.ToString() },
                { 6, gWinApps.Rows.Count.ToString() },
                { 7, gTracks.Rows.Count.ToString() },
                { 8, gExtensions.Rows.Count.ToString() },
                { 9, gJunk.Rows.Count.ToString() },
                { 10, gBackups.Rows.Count.ToString() },
                { 11, gRegistry.Rows.Count.ToString() },
                { 12, lTools.Items.Count.ToString() },
                { 13, gDrives.Rows.Count.ToString() },
                { 14, gHistory.Rows.Count.ToString() },
            };
            foreach (var b in navButtons)
            {
                string val;
                b.BadgeText = countsByTab.TryGetValue(b.TabIndexTarget, out val) ? val : "";
                b.Invalidate();
            }
        }

        private void PopulateTools(object[] d)
        {
            lTools.Items.Clear();
            var ids = new List<string>();
            if (d != null)
            {
                foreach (var o in d)
                {
                    var m = o as Dictionary<string, object>;
                    if (m == null) continue;
                    ids.Add(Convert.ToString(m["id"]));
                    lTools.Items.Add(Convert.ToString(m["label"]) + "  (" + Convert.ToString(m["command"]) + ")");
                }
            }
            toolIds = ids.ToArray();
        }

        private async Task LoadAsync(string name, DataGridView grid, bool withCheck, bool defChecked, object body, Func<Dictionary<string, object>, Bitmap> iconSel)
        {
            try
            {
                var d = await Task.Run(() => Bridge.Call("data", name, body));
                GridHelper.Fill(grid, d, withCheck, defChecked, null, iconSel);
                UpdateDashboardSummary();
                Status("Refreshed " + name + ": " + grid.Rows.Count + " item(s).");
            }
            catch (Exception ex) { Status("Error loading " + name + ": " + ex.Message); }
        }

        private async Task LoadJunkDetailedAsync()
        {
            try
            {
                string customDir = GetInput(tJunkCustom);
                bool recent = chkJunkRecent != null && chkJunkRecent.Checked;
                Status("Scanning junk files...");
                var d = await Task.Run(() => Bridge.Call("data", "junk-detailed", new { customDir = customDir, includeRecent = recent })) as Dictionary<string, object>;
                PopulateJunkDetailed(d);
                UpdateDashboardSummary();
                Status("Junk scan complete: " + gJunk.Rows.Count + " file(s) listed.");
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Junk scan error"); }
        }

        private async Task LoadTracesAsync()
        {
            try
            {
                var d = await Task.Run(() => Bridge.Call("data", "traces", null)) as Dictionary<string, object>;
                PopulateTraces(d);
                UpdateDashboardSummary();
            }
            catch (Exception ex) { Status("Error loading traces: " + ex.Message); }
        }

        private async Task LoadToolsAsync()
        {
            try
            {
                var d = await Task.Run(() => Bridge.Call("data", "wintools", null)) as object[];
                PopulateTools(d);
                UpdateDashboardSummary();
            }
            catch (Exception ex) { Status("Error loading tools: " + ex.Message); }
        }

        private async Task LoadSettingsAsync()
        {
            try
            {
                var d = await Task.Run(() => Bridge.Call("data", "settings", null)) as Dictionary<string, object>;
                PopulateSettingsUi(d);
            }
            catch (Exception ex) { Status("Error loading settings: " + ex.Message); }
        }

        private async Task LoadHistoryOrRefLogsAsync()
        {
            if (cHistorySource.SelectedIndex == 0)
            {
                await LoadAsync("history", gHistory, false, false, null, null);
                return;
            }
            try
            {
                string pat = GetInput(tHistorySearch);
                var d = await Task.Run(() => Bridge.Call("action", "ref-logs", new { pattern = pat, limit = 100 })) as Dictionary<string, object>;
                if (d != null && d.ContainsKey("items")) GridHelper.Fill(gHistory, d["items"], false, false, null, null);
                Status("Reference DB matches: " + gHistory.Rows.Count + " shown (total " + (d != null && d.ContainsKey("total") ? d["total"] : 0) + ").");
            }
            catch (Exception ex) { Status("Ref DB error: " + ex.Message); }
        }

        private async Task SearchProgramsAsync(string pattern)
        {
            try
            {
                var d = await Task.Run(() => Bridge.Call("action", "search", new { pattern = pattern, includeSystem = chkSystemProgs.Checked })) as object[];
                cachedPrograms = d ?? new object[0];
                ApplyProgramFilter();
                Status("Programs shown: " + gPrograms.Rows.Count);
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Search error"); }
        }

        private async void ExportProgramsAsync(string format)
        {
            try
            {
                var res = await Task.Run(() => Bridge.Call("action", "export-programs", new { format = format, includeSystem = chkSystemProgs.Checked })) as Dictionary<string, object>;
                if (res != null && res.ContainsKey("file"))
                {
                    string file = Convert.ToString(res["file"]);
                    Status("Exported " + res["count"] + " program(s) to " + file);
                    if (MessageBox.Show(this, "Exported " + res["count"] + " program(s) to:\r\n" + file + "\r\n\r\nOpen exported file?", "Export Complete", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                    {
                        Process.Start(file);
                    }
                }
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Export error"); }
        }

        private async void HandleProgramUninstallClick(bool quiet)
        {
            var checkedRows = GridHelper.GetCheckedRows(gPrograms);
            if (checkedRows.Count <= 1)
            {
                if (checkedRows.Count == 1)
                {
                    var d = GridHelper.GetRowDict(gPrograms, checkedRows[0]);
                    if (d != null) { OpenWizardForDict(d, quiet); return; }
                }
                OpenUninstallWizard(quiet);
                return;
            }

            if (MessageBox.Show(this, "Batch Quick Uninstall " + checkedRows.Count + " checked program(s) in sequence?", "Confirm Batch Uninstall", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;

            string mode = cSetLeftMode != null && cSetLeftMode.SelectedItem != null ? Convert.ToString(cSetLeftMode.SelectedItem) : "moderate";
            foreach (int rowIdx in checkedRows)
            {
                var d = GridHelper.GetRowDict(gPrograms, rowIdx);
                if (d == null) continue;
                string name = d.ContainsKey("DisplayName") ? Convert.ToString(d["DisplayName"]) : "";
                string pub = d.ContainsKey("Publisher") ? Convert.ToString(d["Publisher"]) : "";
                string loc = d.ContainsKey("InstallLocation") ? Convert.ToString(d["InstallLocation"]) : "";
                string rk = d.ContainsKey("RegistryKey") ? Convert.ToString(d["RegistryKey"]) : "";
                string hp = d.ContainsKey("HivePath") ? Convert.ToString(d["HivePath"]) : "";
                string us = d.ContainsKey("UninstallString") ? Convert.ToString(d["UninstallString"]) : "";
                string di = d.ContainsKey("DisplayIcon") ? Convert.ToString(d["DisplayIcon"]) : "";

                Status("Batch uninstalling: " + name + "...");
                await Task.Run(() => Bridge.Call("action", "uninstall", new { pattern = name, quiet = true, exact = true }));
                var lRes = await Task.Run(() => Bridge.Call("action", "leftovers", new { pattern = name, mode = mode, displayName = name, publisher = pub, installLocation = loc, registryKey = rk, hivePath = hp, uninstallString = us, displayIcon = di })) as Dictionary<string, object>;
                if (lRes != null)
                {
                    var fsArr = (lRes.ContainsKey("filesystem") ? lRes["filesystem"] as object[] : null) ?? new object[0];
                    var regArr = (lRes.ContainsKey("registry") ? lRes["registry"] as object[] : null) ?? new object[0];
                    var files = fsArr.Select(x => { var m = x as Dictionary<string, object>; return m != null && m.ContainsKey("Path") ? Convert.ToString(m["Path"]) : ""; }).Where(s => !string.IsNullOrEmpty(s)).ToArray();
                    var regsList = new List<string>();
                    var regVals = new List<Dictionary<string, object>>();
                    foreach (var x in regArr)
                    {
                        var m = x as Dictionary<string, object>;
                        if (m == null || !m.ContainsKey("Key")) continue;
                        string k = Convert.ToString(m["Key"]);
                        string kd = m.ContainsKey("Kind") ? Convert.ToString(m["Kind"]) : "Key";
                        if (kd == "Value" && m.ContainsKey("ValueName"))
                            regVals.Add(new Dictionary<string, object> { { "Key", k }, { "ValueName", Convert.ToString(m["ValueName"]) } });
                        else if (!string.IsNullOrEmpty(k))
                            regsList.Add(k);
                    }
                    if (files.Length > 0 || regsList.Count > 0 || regVals.Count > 0)
                    {
                        await Task.Run(() => Bridge.Call("action", "leftovers-clean", new { files = files, registry = regsList.ToArray(), regValues = regVals.ToArray(), backupFirst = true }));
                    }
                }
            }
            await SearchProgramsAsync(GetInput(tSearch));
            Status("Batch uninstall finished for " + checkedRows.Count + " program(s).");
        }

        private void OpenUninstallWizard(bool quiet)
        {
            if (gPrograms.SelectedRows.Count == 0) { MessageBox.Show(this, "Select a program first."); return; }
            var d = GridHelper.GetRowDict(gPrograms, gPrograms.SelectedRows[0].Index);
            if (d != null) OpenWizardForDict(d, quiet);
        }

        private void OpenWizardForDict(Dictionary<string, object> prog, bool quiet)
        {
            string defMode = cSetLeftMode != null && cSetLeftMode.SelectedItem != null ? Convert.ToString(cSetLeftMode.SelectedItem) : "moderate";
            bool defBk = chkSetBackupOnUninstall == null || chkSetBackupOnUninstall.Checked;
            using (var wiz = new UninstallWizardForm(prog, quiet, defMode, defBk, theme, () => SearchProgramsAsync(GetInput(tSearch))))
            {
                wiz.ShowDialog(this);
            }
        }

        private void OpenForcedUninstall(string initialPath, string initialName)
        {
            string defMode = cSetLeftMode != null && cSetLeftMode.SelectedItem != null ? Convert.ToString(cSetLeftMode.SelectedItem) : "moderate";
            using (var dlg = new ForcedUninstallForm(initialPath, initialName, defMode, theme, () => SearchProgramsAsync(GetInput(tSearch))))
            {
                dlg.ShowDialog(this);
            }
        }

        private void ChangeOrRepairSelectedProgram()
        {
            if (gPrograms.SelectedRows.Count == 0) { MessageBox.Show(this, "Select a program first."); return; }
            var d = GridHelper.GetRowDict(gPrograms, gPrograms.SelectedRows[0].Index);
            if (d == null) return;
            string mod = d.ContainsKey("ModifyPath") ? Convert.ToString(d["ModifyPath"]) : "";
            string name = d.ContainsKey("DisplayName") ? Convert.ToString(d["DisplayName"]) : "";
            if (string.IsNullOrWhiteSpace(mod))
            {
                MessageBox.Show(this, "No ModifyPath / Repair command is registered for '" + name + "'.", "Repair Not Available");
                return;
            }
            string exe, args;
            if (!Win32.SplitCommand(mod, out exe, out args)) return;
            try
            {
                Process.Start(new ProcessStartInfo { FileName = exe, Arguments = args, UseShellExecute = true });
                Status("Launched Change/Repair for " + name);
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Repair Launch Error"); }
        }

        private void RemoveSelectedProgramEntry()
        {
            if (gPrograms.SelectedRows.Count == 0) return;
            var d = GridHelper.GetRowDict(gPrograms, gPrograms.SelectedRows[0].Index);
            if (d == null) return;
            string name = d.ContainsKey("DisplayName") ? Convert.ToString(d["DisplayName"]) : "";
            string hive = d.ContainsKey("HivePath") ? Convert.ToString(d["HivePath"]) : "";
            string key = d.ContainsKey("RegistryKey") ? Convert.ToString(d["RegistryKey"]) : "";
            if (MessageBox.Show(this, "Remove only the Uninstall registry entry for:\r\n" + name + "\r\n(A .reg backup will be saved first.)?", "Remove Registry Entry", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;
            DoActionAsync("remove-entry", new { hivePath = hive, registryKey = key, name = name }, o => SearchProgramsAsync(GetInput(tSearch)));
        }

        private void OpenSelectedProgramInRegEdit()
        {
            if (gPrograms.SelectedRows.Count == 0) return;
            var d = GridHelper.GetRowDict(gPrograms, gPrograms.SelectedRows[0].Index);
            if (d == null) return;
            string hive = d.ContainsKey("HivePath") ? Convert.ToString(d["HivePath"]) : "";
            string key = d.ContainsKey("RegistryKey") ? Convert.ToString(d["RegistryKey"]) : "";
            Win32.OpenInRegEdit(hive, key);
        }

        private void SendSelectedProgramToLeftovers()
        {
            if (gPrograms.SelectedRows.Count == 0) { MessageBox.Show(this, "Select a program first."); return; }
            string name = GridHelper.GetCell(gPrograms, gPrograms.SelectedRows[0].Index, "DisplayName");
            SetInput(tLeft, name);
            SelectNavTab(3);
            LeftoversAsync(name, Convert.ToString(cLeftMode.SelectedItem));
        }

        private void OpenSelectedProgramFolder()
        {
            if (gPrograms.SelectedRows.Count == 0) { MessageBox.Show(this, "Select a program first."); return; }
            string loc = GridHelper.GetCell(gPrograms, gPrograms.SelectedRows[0].Index, "InstallLocation");
            if (string.IsNullOrWhiteSpace(loc) || !Directory.Exists(loc)) { MessageBox.Show(this, "InstallLocation is missing or does not exist."); return; }
            Process.Start("explorer.exe", loc);
        }

        private void OpenSelectedProgramUrl()
        {
            if (gPrograms.SelectedRows.Count == 0) return;
            var d = GridHelper.GetRowDict(gPrograms, gPrograms.SelectedRows[0].Index);
            string url = (d != null && d.ContainsKey("URLInfoAbout")) ? Convert.ToString(d["URLInfoAbout"]) : "";
            if (string.IsNullOrWhiteSpace(url) || (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
            {
                MessageBox.Show(this, "No valid HTTP/HTTPS publisher URL recorded.");
                return;
            }
            Process.Start(url);
        }

        private void CopySelectedUninstallCommand()
        {
            if (gPrograms.SelectedRows.Count == 0) return;
            var d = GridHelper.GetRowDict(gPrograms, gPrograms.SelectedRows[0].Index);
            string cmd = (d != null && d.ContainsKey("UninstallString")) ? Convert.ToString(d["UninstallString"]) : "";
            if (string.IsNullOrWhiteSpace(cmd)) return;
            Clipboard.SetText(cmd);
            Status("Copied UninstallString to clipboard.");
        }

        private async Task HuntAsync(string pattern)
        {
            if (string.IsNullOrWhiteSpace(pattern))
            {
                await LoadAsync("processes", gHuntProc, false, false, null, ProcIconSelector);
                GridHelper.Fill(gHuntProg, null, false, false, null, null);
                return;
            }
            try
            {
                var d = await Task.Run(() => Bridge.Call("action", "hunt", new { pattern = pattern })) as Dictionary<string, object>;
                GridHelper.Fill(gHuntProc, d["processes"], false, false, null, ProcIconSelector);
                GridHelper.Fill(gHuntProg, d["programs"], false, false, ProgramColumns, ProgramIconSelector);
                Status("Hunter found " + gHuntProc.Rows.Count + " process(es), " + gHuntProg.Rows.Count + " program(s).");
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Hunt error"); }
        }

        private async void PickWindowAsync(Button btn)
        {
            btn.Enabled = false;
            try
            {
                for (int sec = 3; sec >= 1; sec--) { btn.Text = "Hover window (" + sec + "s)..."; await Task.Delay(1000); }
                Win32.POINT pt;
                Win32.GetCursorPos(out pt);
                IntPtr hwnd = Win32.WindowFromPoint(pt);
                IntPtr root = Win32.GetAncestor(hwnd, 2);
                if (root != IntPtr.Zero) hwnd = root;
                uint pid = 0;
                Win32.GetWindowThreadProcessId(hwnd, out pid);
                if (pid > 0)
                {
                    var proc = Process.GetProcessById((int)pid);
                    string target = proc.ProcessName;
                    try { if (proc.MainModule != null) target = proc.MainModule.FileName; } catch { }
                    SetInput(tHunt, target);
                    await HuntAsync(target);
                }
            }
            catch (Exception ex) { Status("Pick window failed: " + ex.Message); }
            finally { btn.Text = "Pick Window (3s)"; btn.Enabled = true; }
        }

        private void KillSelectedHuntedProcess()
        {
            if (gHuntProc.SelectedRows.Count == 0) { MessageBox.Show(this, "Select a process row first."); return; }
            string pidStr = GridHelper.GetCell(gHuntProc, gHuntProc.SelectedRows[0].Index, "ProcessId");
            string name = GridHelper.GetCell(gHuntProc, gHuntProc.SelectedRows[0].Index, "Name");
            int pid;
            if (!int.TryParse(pidStr, out pid)) return;
            if (MessageBox.Show(this, "Terminate process " + name + " (PID " + pid + ")?", "Confirm Kill", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            DoActionAsync("kill-process", new { pid = pid }, o => HuntAsync(GetInput(tHunt)));
        }

        private void SendHuntedToLeftovers()
        {
            string name = gHuntProg.SelectedRows.Count > 0 ? GridHelper.GetCell(gHuntProg, gHuntProg.SelectedRows[0].Index, "DisplayName") : GetInput(tHunt);
            if (string.IsNullOrEmpty(name) && gHuntProc.SelectedRows.Count > 0) name = GridHelper.GetCell(gHuntProc, gHuntProc.SelectedRows[0].Index, "Name");
            if (string.IsNullOrEmpty(name)) { MessageBox.Show(this, "Enter or select a target first."); return; }
            SetInput(tLeft, name);
            SelectNavTab(3);
            LeftoversAsync(name, Convert.ToString(cLeftMode.SelectedItem));
        }

        private async Task LeftoversAsync(string pattern, string mode)
        {
            if (string.IsNullOrWhiteSpace(pattern)) { MessageBox.Show(this, "Enter a program name or shortcut path."); return; }
            try
            {
                var d = await Task.Run(() => Bridge.Call("action", "leftovers", new { pattern = pattern, mode = mode })) as Dictionary<string, object>;
                GridHelper.Fill(gLeftFs, d["filesystem"], true, true, null, null);
                GridHelper.Fill(gLeftReg, d["registry"], true, true, null, null);
                Status("Leftovers (" + mode + "): " + gLeftFs.Rows.Count + " path(s), " + gLeftReg.Rows.Count + " registry key(s).");
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Scan error"); }
        }

        private void CleanSelectedLeftovers()
        {
            var files = GridHelper.GetCheckedRows(gLeftFs).Select(i => GridHelper.GetCell(gLeftFs, i, "Path")).Where(s => !string.IsNullOrEmpty(s)).ToArray();
            string[] regs;
            Dictionary<string, object>[] regValues;
            GridHelper.CollectCheckedRegCleanup(gLeftReg, out regs, out regValues);
            if (files.Length == 0 && regs.Length == 0 && regValues.Length == 0) { MessageBox.Show(this, "Check at least one folder/file or registry entry."); return; }
            if (MessageBox.Show(this, string.Format("Back up and delete {0} filesystem item(s), {1} registry key(s), and {2} value(s)?", files.Length, regs.Length, regValues.Length), "Confirm Cleanup", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            DoActionAsync("leftovers-clean", new { files = files, registry = regs, regValues = regValues, backupFirst = true }, o =>
            {
                Status("Leftovers cleaned.");
                LeftoversAsync(GetInput(tLeft), Convert.ToString(cLeftMode.SelectedItem));
            });
        }

        private async void StartTraceAsync()
        {
            string name = GetInput(tTraceName);
            string inst = GetInput(tTraceInstaller);
            try
            {
                Status("Taking before-installation baseline snapshot...");
                await Task.Run(() => Bridge.Call("action", "trace-start", new { name = name, installerPath = inst }));
                await LoadTracesAsync();
                if (!string.IsNullOrEmpty(inst) && File.Exists(inst))
                {
                    try { Process.Start(new ProcessStartInfo { FileName = inst, UseShellExecute = true }); }
                    catch (Exception ex) { MessageBox.Show(this, "Trace started, but installer launch failed: " + ex.Message); }
                }
                Status("Installation monitoring active. Click 'Stop & Save' when setup finishes.");
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Trace start error"); }
        }

        private async void StopTraceAsync()
        {
            string name = GetInput(tTraceName);
            try
            {
                Status("Taking after-installation snapshot and computing diff...");
                var res = await Task.Run(() => Bridge.Call("action", "trace-stop", new { name = name })) as Dictionary<string, object>;
                await LoadTracesAsync();
                if (res != null)
                {
                    GridHelper.Fill(gTraceItems, res.ContainsKey("items") ? res["items"] : null, true, false, null, null);
                    Status("Saved trace '" + res["name"] + "' with " + res["itemsCount"] + " logged change(s).");
                }
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Trace stop error"); }
        }

        private async void LoadSelectedTraceItemsAsync()
        {
            if (gTraces.SelectedRows.Count == 0) return;
            string idStr = GridHelper.GetCell(gTraces, gTraces.SelectedRows[0].Index, "id");
            int id;
            if (!int.TryParse(idStr, out id)) return;
            try
            {
                var res = await Task.Run(() => Bridge.Call("action", "trace-items", new { id = id })) as Dictionary<string, object>;
                if (res != null && res.ContainsKey("items")) GridHelper.Fill(gTraceItems, res["items"], true, true, null, null);
            }
            catch (Exception ex) { Status("Error loading trace items: " + ex.Message); }
        }

        private void ExportSelectedTraceAsync(string format)
        {
            if (gTraces.SelectedRows.Count == 0) { MessageBox.Show(this, "Select a traced program log first."); return; }
            int id;
            if (!int.TryParse(GridHelper.GetCell(gTraces, gTraces.SelectedRows[0].Index, "id"), out id)) return;
            DoActionAsync("trace-export", new { id = id, format = format }, o =>
            {
                var d = o as Dictionary<string, object>;
                if (d != null && d.ContainsKey("file"))
                {
                    string f = Convert.ToString(d["file"]);
                    Status("Exported trace: " + f);
                    if (MessageBox.Show(this, "Trace exported to:\r\n" + f + "\r\n\r\nOpen file?", "Trace Export", MessageBoxButtons.YesNo) == DialogResult.Yes)
                        Process.Start(f);
                }
            });
        }

        private void ImportTraceAsync()
        {
            using (var d = new OpenFileDialog { Filter = "Trace Export JSON (*.json)|*.json" })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                DoActionAsync("trace-import", new { file = d.FileName }, o => LoadTracesAsync());
            }
        }

        private void DeleteCheckedTraceItems()
        {
            var indices = GridHelper.GetCheckedRows(gTraceItems);
            if (indices.Count == 0) { MessageBox.Show(this, "Check traced items in the bottom grid to delete."); return; }
            var files = new List<string>();
            var regs = new List<string>();
            foreach (int i in indices)
            {
                string kind = GridHelper.GetCell(gTraceItems, i, "Kind");
                string p = GridHelper.GetCell(gTraceItems, i, "Path");
                if (kind == "Dir" || kind == "File") files.Add(p);
                else if (kind == "RegKey") regs.Add(p);
            }
            if (files.Count == 0 && regs.Count == 0) { MessageBox.Show(this, "No deletable filesystem or registry keys checked."); return; }
            if (MessageBox.Show(this, "Back up and delete " + files.Count + " traced filesystem item(s) and " + regs.Count + " registry key(s)?", "Confirm Traced Uninstall", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            DoActionAsync("leftovers-clean", new { files = files.ToArray(), registry = regs.ToArray(), backupFirst = true }, o =>
            {
                Status("Deleted traced items.");
                LoadSelectedTraceItemsAsync();
            });
        }

        private void DeleteSelectedTraceLog()
        {
            if (gTraces.SelectedRows.Count == 0) return;
            string idStr = GridHelper.GetCell(gTraces, gTraces.SelectedRows[0].Index, "id");
            string name = GridHelper.GetCell(gTraces, gTraces.SelectedRows[0].Index, "name");
            int id;
            if (!int.TryParse(idStr, out id)) return;
            if (MessageBox.Show(this, "Delete trace log '" + name + "'?", "Confirm", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            DoActionAsync("trace-delete", new { id = id }, o => { GridHelper.Fill(gTraceItems, null, false, false, null, null); LoadTracesAsync(); });
        }

        private void AddAutorunDialog()
        {
            using (var dlg = new Form
            {
                Text = "Add Startup Entry",
                Size = new Size(470, 235),
                StartPosition = FormStartPosition.CenterParent,
                BackColor = theme.BgApp,
                ForeColor = theme.TextPrimary,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
            })
            {
                var tN = new TextBox { Left = 120, Top = 18, Width = 315, BackColor = theme.BgInput, ForeColor = theme.TextPrimary };
                var tC = new TextBox { Left = 120, Top = 52, Width = 240, BackColor = theme.BgInput, ForeColor = theme.TextPrimary };
                var bBr = B("...", theme.AccentBlue);
                bBr.Left = 368; bBr.Top = 49; bBr.Width = 67; bBr.Height = 25;
                bBr.Click += (s, e) => { using (var od = new OpenFileDialog { Filter = "Programs (*.exe;*.cmd;*.bat)|*.exe;*.cmd;*.bat|All (*.*)|*.*" }) { if (od.ShowDialog(dlg) == DialogResult.OK) tC.Text = "\"" + od.FileName + "\""; } };
                var cSc = new ComboBox { Left = 120, Top = 88, Width = 315, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = theme.BgInput, ForeColor = theme.TextPrimary };
                cSc.Items.AddRange(new object[] { "HKCU (Current User - Run)", "HKCU_RunOnce (Current User - Once)", "HKLM (All Users - Run)" });
                cSc.SelectedIndex = 0;

                var bOk = B("Add Entry", theme.AccentGreen);
                bOk.Left = 340; bOk.Top = 135;
                bOk.Click += (s, e) => { dlg.DialogResult = DialogResult.OK; dlg.Close(); };
                dlg.Controls.AddRange(new Control[]
                {
                    new Label { Text = "Entry Name:", Left = 14, Top = 22, AutoSize = true }, tN,
                    new Label { Text = "Command:", Left = 14, Top = 56, AutoSize = true }, tC, bBr,
                    new Label { Text = "Scope:", Left = 14, Top = 92, AutoSize = true }, cSc,
                    bOk
                });
                if (dlg.ShowDialog(this) == DialogResult.OK && !string.IsNullOrWhiteSpace(tN.Text) && !string.IsNullOrWhiteSpace(tC.Text))
                {
                    string sc = cSc.SelectedIndex == 1 ? "HKCU_RunOnce" : (cSc.SelectedIndex == 2 ? "HKLM" : "HKCU");
                    DoActionAsync("autorun-add", new { name = tN.Text.Trim(), command = tC.Text.Trim(), scope = sc }, o => LoadAsync("autoruns", gAutoruns, false, false, null, AutorunIconSelector));
                }
            }
        }

        private void LaunchSelectedAutorun()
        {
            if (gAutoruns.SelectedRows.Count == 0) return;
            string cmd = GridHelper.GetCell(gAutoruns, gAutoruns.SelectedRows[0].Index, "Command");
            string exe, args;
            if (!Win32.SplitCommand(cmd, out exe, out args)) return;
            try { Process.Start(new ProcessStartInfo { FileName = exe, Arguments = args, UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Launch Error"); }
        }

        private void OpenSelectedAutorunFolder()
        {
            if (gAutoruns.SelectedRows.Count == 0) return;
            string cmd = GridHelper.GetCell(gAutoruns, gAutoruns.SelectedRows[0].Index, "Command");
            string exe, args;
            if (Win32.SplitCommand(cmd, out exe, out args) && File.Exists(exe))
                Process.Start("explorer.exe", "/select,\"" + exe + "\"");
        }

        private void KillSelectedAutorunProcess()
        {
            if (gAutoruns.SelectedRows.Count == 0) return;
            string pidStr = GridHelper.GetCell(gAutoruns, gAutoruns.SelectedRows[0].Index, "PID");
            string name = GridHelper.GetCell(gAutoruns, gAutoruns.SelectedRows[0].Index, "Name");
            int pid;
            if (!int.TryParse(pidStr, out pid) || pid <= 0) { MessageBox.Show(this, "Selected startup item is not currently running."); return; }
            if (MessageBox.Show(this, "Terminate process for '" + name + "' (PID " + pid + ")?", "Confirm Kill", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            DoActionAsync("kill-process", new { pid = pid }, o => LoadAsync("autoruns", gAutoruns, false, false, null, AutorunIconSelector));
        }

        private void ToggleSelectedAutorun()
        {
            if (gAutoruns.SelectedRows.Count == 0) { MessageBox.Show(this, "Select a startup entry."); return; }
            int idx = gAutoruns.SelectedRows[0].Index;
            string name = GridHelper.GetCell(gAutoruns, idx, "Name");
            string hive = GridHelper.GetCell(gAutoruns, idx, "HivePath");
            bool enabled = GridHelper.GetCell(gAutoruns, idx, "Enabled").Equals("True", StringComparison.OrdinalIgnoreCase);
            DoActionAsync("autorun-toggle", new { name = name, hivePath = hive, enabled = !enabled }, o => LoadAsync("autoruns", gAutoruns, false, false, null, AutorunIconSelector));
        }

        private void RemoveSelectedAutorun()
        {
            if (gAutoruns.SelectedRows.Count == 0) { MessageBox.Show(this, "Select a startup entry."); return; }
            int idx = gAutoruns.SelectedRows[0].Index;
            string name = GridHelper.GetCell(gAutoruns, idx, "Name");
            string hive = GridHelper.GetCell(gAutoruns, idx, "HivePath");
            string cmd = GridHelper.GetCell(gAutoruns, idx, "Command");
            if (MessageBox.Show(this, "Remove startup entry '" + name + "'?", "Confirm Remove", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            DoActionAsync("autorun-remove", new { name = name, hivePath = hive, command = cmd }, o => LoadAsync("autoruns", gAutoruns, false, false, null, AutorunIconSelector));
        }

        private void FilterWinApps()
        {
            string q = GetInput(tWinAppFilter).ToLowerInvariant();
            foreach (DataGridViewRow row in gWinApps.Rows)
            {
                string n = GridHelper.GetCell(gWinApps, row.Index, "DisplayName").ToLowerInvariant();
                string p = GridHelper.GetCell(gWinApps, row.Index, "Publisher").ToLowerInvariant();
                row.Visible = string.IsNullOrEmpty(q) || n.IndexOf(q) >= 0 || p.IndexOf(q) >= 0;
            }
        }

        private void UninstallSelectedWinApp()
        {
            if (gWinApps.SelectedRows.Count == 0) { MessageBox.Show(this, "Select a Windows Store app first."); return; }
            int idx = gWinApps.SelectedRows[0].Index;
            string full = GridHelper.GetCell(gWinApps, idx, "PackageFullName");
            string name = GridHelper.GetCell(gWinApps, idx, "DisplayName");
            if (MessageBox.Show(this, "Uninstall Store app '" + name + "'?\r\n" + full, "Confirm Uninstall", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            DoActionAsync("winapp-remove", new { packageFullName = full }, o => LoadAsync("winapps", gWinApps, false, false, null, null));
        }

        private void ResetSelectedWinApp()
        {
            if (gWinApps.SelectedRows.Count == 0) return;
            int idx = gWinApps.SelectedRows[0].Index;
            string full = GridHelper.GetCell(gWinApps, idx, "PackageFullName");
            string fam = GridHelper.GetCell(gWinApps, idx, "PackageFamilyName");
            string name = GridHelper.GetCell(gWinApps, idx, "DisplayName");
            if (MessageBox.Show(this, "Reset & clear temporary/cache state for '" + name + "'?", "Confirm Reset", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            DoActionAsync("winapp-reset", new { packageFullName = full, packageFamilyName = fam }, o => Status("Reset UWP app '" + name + "': " + Bridge.Dump(o)));
        }

        private void OpenSelectedWinAppFolder()
        {
            if (gWinApps.SelectedRows.Count == 0) return;
            string loc = GridHelper.GetCell(gWinApps, gWinApps.SelectedRows[0].Index, "InstallLocation");
            if (!string.IsNullOrEmpty(loc) && Directory.Exists(loc)) Process.Start("explorer.exe", loc);
        }

        private void CleanTracks()
        {
            var ids = GridHelper.GetCheckedRows(gTracks).Select(i => GridHelper.GetCell(gTracks, i, "id")).Where(s => !string.IsNullOrEmpty(s)).ToArray();
            if (ids.Length == 0) { MessageBox.Show(this, "Tick at least one category in the Select column."); return; }
            if (MessageBox.Show(this, "Clean " + ids.Length + " track category/categories? This deletes matching files and keys.", "Confirm Clean", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            DoActionAsync("tracks-clean", new { ids = ids }, o =>
            {
                tTrackOut.Text = Bridge.PrettyDump(o);
                LoadAsync("tracks", gTracks, true, false, null, null);
            });
        }

        private void ToggleExtension()
        {
            if (gExtensions.SelectedRows.Count == 0) { MessageBox.Show(this, "Select an extension."); return; }
            int idx = gExtensions.SelectedRows[0].Index;
            string path = GridHelper.GetCell(gExtensions, idx, "path");
            string statusVal = GridHelper.GetCell(gExtensions, idx, "status");
            if (string.IsNullOrEmpty(path)) return;
            bool enable = statusVal.Equals("disabled", StringComparison.OrdinalIgnoreCase);
            DoActionAsync("extension-toggle", new { path = path, enabled = enable }, o => LoadAsync("extensions", gExtensions, false, false, null, null));
        }

        private void CleanSelectedJunk()
        {
            var files = GridHelper.GetCheckedRows(gJunk).Select(i => GridHelper.GetCell(gJunk, i, "Path")).Where(s => !string.IsNullOrEmpty(s)).ToArray();
            if (files.Length == 0) { MessageBox.Show(this, "Check at least one junk file row."); return; }
            if (MessageBox.Show(this, "Permanently delete " + files.Length + " checked junk file(s)?", "Confirm Clean", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            DoActionAsync("junk-clean", new { files = files }, o => LoadJunkDetailedAsync());
        }

        private void CleanSelectedRegistry()
        {
            var indices = GridHelper.GetCheckedRows(gRegistry);
            if (indices.Count == 0) { MessageBox.Show(this, "Check at least one stale registry entry."); return; }
            var items = indices.Select(i => new Dictionary<string, object>
            {
                { "Issue", GridHelper.GetCell(gRegistry, i, "Issue") },
                { "Name", GridHelper.GetCell(gRegistry, i, "Name") },
                { "Key", GridHelper.GetCell(gRegistry, i, "Key") },
                { "HivePath", GridHelper.GetCell(gRegistry, i, "HivePath") },
            }).ToArray();
            if (MessageBox.Show(this, "Back up and remove " + items.Length + " stale registry entry/entries?", "Confirm Clean", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            DoActionAsync("registry-clean", new { items = items }, o => LoadAsync("registry-invalid", gRegistry, true, true, null, null));
        }

        private void RestoreBackup()
        {
            if (gBackups.SelectedRows.Count == 0) { MessageBox.Show(this, "Select a backup."); return; }
            string file = GridHelper.GetCell(gBackups, gBackups.SelectedRows[0].Index, "file");
            if (string.IsNullOrEmpty(file)) return;
            if (MessageBox.Show(this, "Import registry entries from '" + file + "'?", "Confirm Restore", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            DoActionAsync("restore", new { file = file }, o => tBackupOut.Text = Bridge.PrettyDump(o));
        }

        private void DeleteSelectedBackup()
        {
            if (gBackups.SelectedRows.Count == 0) return;
            string file = GridHelper.GetCell(gBackups, gBackups.SelectedRows[0].Index, "file");
            if (string.IsNullOrEmpty(file)) return;
            if (MessageBox.Show(this, "Delete backup '" + file + "'?", "Confirm Delete", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            DoActionAsync("backup-delete", new { file = file }, o => { tBackupOut.Text = Bridge.PrettyDump(o); LoadAsync("backups", gBackups, false, false, null, null); });
        }

        private void LaunchTool()
        {
            if (lTools.SelectedIndex < 0 || toolIds == null || lTools.SelectedIndex >= toolIds.Length)
            {
                MessageBox.Show(this, "Select a tool from the list first.");
                return;
            }
            DoActionAsync("wintool", new { id = toolIds[lTools.SelectedIndex] }, o => Status("Tool launched: " + toolIds[lTools.SelectedIndex]));
        }

        private void AddEvidenceTarget(string kind, string targetPath)
        {
            foreach (DataGridViewRow r in gEvidenceTargets.Rows)
            {
                if (string.Equals(Convert.ToString(r.Cells["Path"].Value), targetPath, StringComparison.OrdinalIgnoreCase)) return;
            }
            string info = kind == "File" && File.Exists(targetPath) ? (new FileInfo(targetPath).Length + " bytes") : "Directory tree";
            gEvidenceTargets.Rows.Add(kind, targetPath, info);
        }

        private void SecureDeleteBatch()
        {
            if (gEvidenceTargets.Rows.Count == 0) { MessageBox.Show(this, "Add one or more files/folders to the list first."); return; }
            var paths = new List<string>();
            foreach (DataGridViewRow r in gEvidenceTargets.Rows)
            {
                string p = Convert.ToString(r.Cells["Path"].Value);
                if (!string.IsNullOrWhiteSpace(p)) paths.Add(p);
            }
            int passes = 3;
            int.TryParse(Convert.ToString(cEvidencePasses.SelectedItem), out passes);
            if (MessageBox.Show(this, "Permanently overwrite (" + passes + "-pass) and delete " + paths.Count + " listed target(s)?\r\nThis cannot be undone.", "Confirm Unrecoverable Delete", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

            DoActionAsync("secure-delete-batch", new { paths = paths.ToArray(), passes = passes }, o =>
            {
                gEvidenceTargets.Rows.Clear();
                Status("Unrecoverable delete complete: " + Bridge.Dump(o));
                MessageBox.Show(this, "Unrecoverable delete result:\r\n" + Bridge.PrettyDump(o), "Evidence Remover");
            });
        }

        private void WipeFreeSpace()
        {
            string dir = GetInput(tWipeFolder);
            int mb;
            if (!int.TryParse(GetInput(tWipeMb), out mb)) mb = 16;
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) { MessageBox.Show(this, "Select a drive or enter an existing folder path."); return; }
            if (MessageBox.Show(this, "Write and delete " + mb + " MB of random data in unallocated space of:\r\n" + dir + "?", "Confirm Free-Space Wipe", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            DoActionAsync("wipe-freespace", new { path = dir, megabytes = mb }, o =>
            {
                Status("Free-space wipe completed (" + mb + " MB).");
                LoadAsync("drives", gDrives, false, false, null, null);
            });
        }

        private void SendHistoryRowToLeftovers()
        {
            if (gHistory.SelectedRows.Count == 0) { MessageBox.Show(this, "Select a log entry first."); return; }
            int idx = gHistory.SelectedRows[0].Index;
            string pat = GridHelper.GetCell(gHistory, idx, "Name");
            if (string.IsNullOrEmpty(pat)) pat = GridHelper.GetCell(gHistory, idx, "RDN");
            if (string.IsNullOrEmpty(pat)) pat = GridHelper.GetCell(gHistory, idx, "target");
            if (string.IsNullOrEmpty(pat)) return;
            SetInput(tLeft, pat);
            SelectNavTab(3);
            LeftoversAsync(pat, Convert.ToString(cLeftMode.SelectedItem));
        }

        private void SaveSettings()
        {
            int histLimit = 200;
            if (!int.TryParse(tSetHistoryLimit.Text, out histLimit) || histLimit < 50) histLimit = 200;
            string newTheme = cSetTheme.SelectedItem != null ? Convert.ToString(cSetTheme.SelectedItem) : "dark";
            var patch = new Dictionary<string, object>
            {
                { "productName", string.IsNullOrWhiteSpace(tSetProduct.Text) ? "Open Uninstaller" : tSetProduct.Text.Trim() },
                { "leftoversDepth", cSetLeftMode.SelectedItem != null ? Convert.ToString(cSetLeftMode.SelectedItem) : "moderate" },
                { "theme", newTheme },
                { "historyLimit", histLimit },
                { "confirmBeforeClean", chkSetConfirm.Checked },
                { "backupBeforeUninstall", chkSetBackupOnUninstall.Checked },
                { "dryRunDefault", chkSetDryRun.Checked },
            };
            DoActionAsync("settings", new { patch = patch }, o =>
            {
                var updated = o as Dictionary<string, object>;
                if (updated != null) PopulateSettingsUi(updated);
                ApplyTheme(newTheme);
                Status("Settings saved & theme applied (" + newTheme + ").");
            });
        }

        private async void DoActionAsync(string name, object body, Action<object> onDone)
        {
            try
            {
                Cursor = Cursors.WaitCursor;
                var d = await Task.Run(() => Bridge.Call("action", name, body));
                if (onDone != null) onDone(d);
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Action error"); }
            finally { Cursor = Cursors.Default; }
        }

        private static void CountControlItems(Control parent, ref int rows, ref int cols)
        {
            foreach (Control c in parent.Controls)
            {
                var g = c as DataGridView;
                if (g != null) { rows += g.Rows.Count; cols = Math.Max(cols, g.Columns.Count); }
                var lb = c as ListBox;
                if (lb != null) { rows += lb.Items.Count; }
                if (c.HasChildren) CountControlItems(c, ref rows, ref cols);
            }
        }

        public int RunSelfTest()
        {
            WindowState = FormWindowState.Normal;
            StartPosition = FormStartPosition.Manual;
            Bounds = new Rectangle(20, 20, 1280, 820);
            PerformLayout();
            var sw = Stopwatch.StartNew();
            while (!initialLoadFinished && sw.ElapsedMilliseconds < 120000)
            {
                Application.DoEvents();
                System.Threading.Thread.Sleep(50);
            }
            WindowState = FormWindowState.Normal;
            Bounds = new Rectangle(20, 20, 1280, 820);
            PerformLayout();
            Application.DoEvents();

            Directory.CreateDirectory(Bridge.ScreenshotsPath);
            var captureMap = new Dictionary<int, string>
            {
                { 0, "dashboard.png" },
                { 1, "uninstaller.png" },
                { 2, "hunter.png" },
                { 4, "traced.png" },
                { 9, "junk.png" },
                { 13, "evidence.png" },
                { 15, "settings.png" },
            };
            var savedShots = new List<string>();
            var tabReports = new List<Dictionary<string, object>>();

            for (int i = 0; i < tabs.TabPages.Count; i++)
            {
                SelectNavTab(i);
                Refresh();
                Application.DoEvents();
                var tp = tabs.TabPages[i];
                int rows = 0;
                int cols = 0;
                CountControlItems(tp, ref rows, ref cols);
                tabReports.Add(new Dictionary<string, object>
                {
                    { "index", i },
                    { "title", tp.Text },
                    { "rowsOrItems", rows },
                    { "columns", cols },
                    { "visible", tp.Visible },
                });

                string fileName;
                if (captureMap.TryGetValue(i, out fileName))
                {
                    try
                    {
                        using (var bmp = new Bitmap(Width, Height))
                        {
                            DrawToBitmap(bmp, new Rectangle(0, 0, Width, Height));
                            string fullShot = Path.Combine(Bridge.ScreenshotsPath, fileName);
                            bmp.Save(fullShot, System.Drawing.Imaging.ImageFormat.Png);
                            savedShots.Add(fullShot);
                        }
                    }
                    catch { }
                }
            }

            var huntRes = Bridge.Call("action", "hunt", new { pattern = "\"C:\\Users\\Public\\Desktop\\VLC media player.lnk\"" }) as Dictionary<string, object>;
            var leftSafeRes = Bridge.Call("action", "leftovers", new { pattern = "VLC media player", mode = "safe" }) as Dictionary<string, object>;
            var leftRes = Bridge.Call("action", "leftovers", new { pattern = "VLC media player", mode = "moderate" }) as Dictionary<string, object>;
            var leftAdvRes = Bridge.Call("action", "leftovers", new { pattern = "VLC media player", mode = "advanced" }) as Dictionary<string, object>;
            var leftHintRes = Bridge.Call("action", "leftovers", new { pattern = "Simulated Uninstalled App", displayName = "Simulated Uninstalled App", publisher = "VideoLAN", installLocation = "C:\\Program Files\\VideoLAN\\VLC", mode = "moderate" }) as Dictionary<string, object>;
            var forcedRes = Bridge.Call("action", "forced-probe", new { programName = "VLC media player", mode = "moderate" }) as Dictionary<string, object>;
            var refRes = Bridge.Call("action", "ref-logs", new { pattern = "7-Zip", limit = 5 }) as Dictionary<string, object>;
            var drivesRes = Bridge.Call("data", "drives", null) as object[];
            var junkDet = Bridge.Call("data", "junk-detailed", new { }) as Dictionary<string, object>;

            Func<Dictionary<string, object>, int> countTotalLeft = (res) =>
            {
                if (res == null) return 0;
                int fsCnt = res.ContainsKey("filesystem") && res["filesystem"] is object[] ? ((object[])res["filesystem"]).Length : 0;
                int rgCnt = res.ContainsKey("registry") && res["registry"] is object[] ? ((object[])res["registry"]).Length : 0;
                return fsCnt + rgCnt;
            };

            var report = new Dictionary<string, object>
            {
                { "ok", string.IsNullOrEmpty(initialLoadError) },
                { "loadError", initialLoadError },
                { "tabCount", tabs.TabPages.Count },
                { "tabs", tabReports },
                { "huntVlcPrograms", huntRes != null && huntRes.ContainsKey("programs") ? ((object[])huntRes["programs"]).Length : 0 },
                { "leftoverSafeCount", countTotalLeft(leftSafeRes) },
                { "leftoverModerateCount", countTotalLeft(leftRes) },
                { "leftoverAdvancedCount", countTotalLeft(leftAdvRes) },
                { "leftoverPostUninstallHintCount", countTotalLeft(leftHintRes) },
                { "leftoverVlcFolders", leftRes != null && leftRes.ContainsKey("filesystem") ? ((object[])leftRes["filesystem"]).Length : 0 },
                { "forcedProbeFs", forcedRes != null && forcedRes.ContainsKey("filesystem") ? ((object[])forcedRes["filesystem"]).Length : 0 },
                { "refLogs7Zip", refRes != null && refRes.ContainsKey("items") ? ((object[])refRes["items"]).Length : 0 },
                { "drivesCount", drivesRes != null ? drivesRes.Length : 0 },
                { "junkDetailedFiles", junkDet != null && junkDet.ContainsKey("files") ? ((object[])junkDet["files"]).Length : 0 },
                { "windowSize", Width + "x" + Height },
                { "screenshots", savedShots.ToArray() },
            };

            string outPath = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "data", "selftest.json");
            Directory.CreateDirectory(Path.GetDirectoryName(outPath));
            File.WriteAllText(outPath, Bridge.PrettyDump(report));
            Bridge.Log("SELFTEST COMPLETE ok=" + report["ok"] + " screenshots=" + savedShots.Count);
            return string.IsNullOrEmpty(initialLoadError) ? 0 : 1;
        }
    }

    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var form = new MainForm();
            if (args != null && args.Contains("--self-test"))
            {
                form.Show();
                int code = form.RunSelfTest();
                form.Close();
                return code;
            }
            Application.Run(form);
            return 0;
        }
    }
}
