// =====================================================================
//  GWQuota · 上游额度悬浮窗（球 / 条 两种外形）
//  编译目标：.NET Framework 4.8（Win10 自带），csc.exe 直接出单文件 exe
//  设计要点：
//    * 全部自绘 GDI+，不放子控件；无动画时 CPU 为 0
//    * 接口只读，key 只从 %APPDATA%\GWQuota\key.txt 读取，不编进 exe
//    * 失败一律保留上一次成功数据，界面不清零、不弹窗
// =====================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;
using WinTimer = System.Windows.Forms.Timer;

namespace GWQuota
{
    // ============================ 常量 ============================
    internal static class Consts
    {
        public const string DefaultPath = "/api/upstream/quota";   // 用户只填网关根地址，路径在这里补
        public const string MutexName = @"Local\GWQuotaWidget_Single";
        public const string RunValue = "GWQuota";
        public const string Version = "1.0.0";

        public const int BaseBall = 92;      // 球直径（96 DPI 基准）
        public const int BaseBarW = 348;
        public const int BaseBarH = 78;
        public const int BaseDetailW = 346;
    }

    internal static class Native
    {
        [DllImport("user32.dll")]
        public static extern bool SetProcessDPIAware();

        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr hIcon);
    }

    // ============================ 路径 ============================
    internal static class AppPaths
    {
        static string _dir;

        // 数据目录逐级探测：%APPDATA% → %LOCALAPPDATA% → exe 同目录 data\
        // 这样在受限环境（例如被策略限制写漫游目录）也能落盘，不至于丢配置。
        public static string Dir
        {
            get
            {
                if (_dir == null) _dir = Resolve();
                return _dir;
            }
        }

        public static string KeyFile { get { return Path.Combine(Dir, "key.txt"); } }
        public static string IniFile { get { return Path.Combine(Dir, "config.ini"); } }
        public static string LogFile { get { return Path.Combine(Dir, "gwquota.log"); } }

        static string Resolve()
        {
            string[] cands = new string[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GWQuota"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GWQuota"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data")
            };
            for (int i = 0; i < cands.Length; i++)
            {
                try
                {
                    Directory.CreateDirectory(cands[i]);
                    string probe = Path.Combine(cands[i], ".probe");
                    File.WriteAllText(probe, "1");
                    File.Delete(probe);
                    return cands[i];
                }
                catch { }
            }
            return cands[0];
        }

        public static void Ensure()
        {
            try { Directory.CreateDirectory(Dir); }
            catch { }
        }

        // 钥匙文件按「数据目录 → %APPDATA% 标准位 → exe 同目录」依次查找
        public static string ReadKey()
        {
            string[] paths = new string[]
            {
                KeyFile,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GWQuota", "key.txt"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "key.txt")
            };
            for (int i = 0; i < paths.Length; i++)
            {
                try
                {
                    if (!File.Exists(paths[i])) continue;
                    string[] lines = File.ReadAllText(paths[i], Encoding.UTF8).Split('\n');
                    for (int j = 0; j < lines.Length; j++)
                    {
                        string t = lines[j].Trim().Trim('"', '\'', '\r', ' ');
                        if (t.Length > 0 && t[0] != '#') return t;
                    }
                }
                catch { }
            }
            return "";
        }

        public static void WriteKey(string key)
        {
            try
            {
                Ensure();
                File.WriteAllText(KeyFile, (key ?? "").Trim() + "\r\n", Encoding.UTF8);
            }
            catch { }
        }


        public static void Log(string msg)
        {
            try
            {
                Ensure();
                FileInfo fi = new FileInfo(LogFile);
                if (fi.Exists && fi.Length > 200000) fi.Delete();
                File.AppendAllText(LogFile,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "  " + msg + "\r\n",
                    Encoding.UTF8);
            }
            catch { }
        }
    }

    // ============================ 颜色 ============================
    internal static class ColorUtil
    {
        public static double Clamp01(double v)
        {
            if (v < 0) return 0;
            if (v > 1) return 1;
            return v;
        }

        public static Color Hsl(double h, double s, double l)
        {
            h = ((h % 360) + 360) % 360;
            s = Clamp01(s);
            l = Clamp01(l);
            double c = (1 - Math.Abs(2 * l - 1)) * s;
            double hp = h / 60.0;
            double x = c * (1 - Math.Abs((hp % 2) - 1));
            double r = 0, g = 0, b = 0;
            if (hp < 1) { r = c; g = x; }
            else if (hp < 2) { r = x; g = c; }
            else if (hp < 3) { g = c; b = x; }
            else if (hp < 4) { g = x; b = c; }
            else if (hp < 5) { r = x; b = c; }
            else { r = c; b = x; }
            double m = l - c / 2.0;
            return Color.FromArgb(B(r + m), B(g + m), B(b + m));
        }

        static int B(double v) { return (int)Math.Round(Clamp01(v) * 255.0); }

        public static Color Mix(Color a, Color b, double t)
        {
            t = Clamp01(t);
            return Color.FromArgb(
                (int)Math.Round(a.R + (b.R - a.R) * t),
                (int)Math.Round(a.G + (b.G - a.G) * t),
                (int)Math.Round(a.B + (b.B - a.B) * t));
        }

        public static Color A(Color c, int alpha)
        {
            if (alpha < 0) alpha = 0;
            if (alpha > 255) alpha = 255;
            return Color.FromArgb(alpha, c.R, c.G, c.B);
        }
    }

    // ============================ 绘图工具 ============================
    internal static class Gfx
    {
        // 每次返回新建的 Font，由调用方的 using 释放（不缓存，避免缓存对象被 Dispose 后复用）
        public static Font F(string family, float size, FontStyle style, GraphicsUnit unit)
        {
            try { return new Font(family, size, style, unit); }
            catch { }
            try { return new Font("Microsoft YaHei UI", size, style, unit); }
            catch { }
            return new Font(FontFamily.GenericSansSerif, size, style, unit);
        }

        public static Font FP(string family, float pt, FontStyle style)
        {
            return F(family, pt, style, GraphicsUnit.Point);
        }

        public static Font FX(string family, float px, FontStyle style)
        {
            return F(family, px, style, GraphicsUnit.Pixel);
        }

        public static GraphicsPath RoundRect(RectangleF r, float radius)
        {
            GraphicsPath p = new GraphicsPath();
            if (radius <= 0.1f) { p.AddRectangle(r); return p; }
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static RectangleF Inset(RectangleF r, float d)
        {
            return new RectangleF(r.X + d, r.Y + d, Math.Max(1f, r.Width - 2 * d), Math.Max(1f, r.Height - 2 * d));
        }

        public static string Ellipsis(Graphics g, string text, Font f, float maxWidth)
        {
            if (string.IsNullOrEmpty(text)) return "";
            if (g.MeasureString(text, f).Width <= maxWidth) return text;
            string s = text;
            while (s.Length > 1 && g.MeasureString(s + "…", f).Width > maxWidth) s = s.Substring(0, s.Length - 1);
            return s + "…";
        }

        // 托盘图标：整块亮色圆角底 + 深色内圈
        // 之前是深色底 + 细环，压到 16px 后在深色任务栏上几乎看不见；
        // 现在整块都是主题色，缩到 16px 依旧醒目。
        public static Icon TrayIcon(Color accent, Color ringColor, bool alert)
        {
            const int S = 32;
            using (Bitmap bmp = new Bitmap(S, S, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    // 不透明深色打底，避免 HICON 转换丢掉 alpha 后露黑边
                    g.Clear(Color.FromArgb(255, 16, 18, 22));
                    RectangleF body = new RectangleF(1f, 1f, S - 2f, S - 2f);
                    Color bright = ColorUtil.Mix(accent, Color.White, 0.25);
                    Color deep = ColorUtil.Mix(accent, Color.Black, 0.28);
                    using (GraphicsPath p = RoundRect(body, 9f))
                    using (LinearGradientBrush br = new LinearGradientBrush(body, bright, deep, 60f))
                        g.FillPath(br, p);

                    // 内圈挖空，缩到 16px 也能看出是个圆环
                    using (SolidBrush hole = new SolidBrush(Color.FromArgb(255, 13, 15, 19)))
                        g.FillEllipse(hole, new RectangleF(9.5f, 9.5f, 13f, 13f));

                    // 左下高光，立体一点
                    using (SolidBrush hi = new SolidBrush(Color.FromArgb(90, 255, 255, 255)))
                        g.FillEllipse(hi, new RectangleF(6f, 5f, 9f, 7f));

                    if (alert)
                    {
                        using (SolidBrush b = new SolidBrush(Color.FromArgb(255, 242, 98, 94)))
                            g.FillEllipse(b, new RectangleF(18f, 18f, 12f, 12f));
                        using (Pen pen = new Pen(Color.FromArgb(255, 16, 18, 22), 1.6f))
                            g.DrawEllipse(pen, new RectangleF(18f, 18f, 12f, 12f));
                    }
                }
                IntPtr h = bmp.GetHicon();
                try
                {
                    using (Icon tmp = Icon.FromHandle(h))
                        return (Icon)tmp.Clone();
                }
                finally { Native.DestroyIcon(h); }
            }
        }

        public static Image Swatch(Color c)
        {
            Bitmap bmp = new Bitmap(16, 16, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (SolidBrush b = new SolidBrush(c)) g.FillEllipse(b, 2, 2, 12, 12);
                using (Pen p = new Pen(Color.FromArgb(120, 255, 255, 255), 1f)) g.DrawEllipse(p, 2, 2, 12, 12);
            }
            return bmp;
        }
    }

    // ============================ 主题 ============================
    internal sealed class Theme
    {
        public int Hue;
        public int Sat;
        public Color Accent;
        public Color AccentDeep;

        public static readonly Color TextMain = Color.FromArgb(245, 247, 250);
        public static readonly Color TextDim = Color.FromArgb(206, 213, 224);
        public static readonly Color Warn = Color.FromArgb(242, 98, 94);
        public static readonly Color Stale = Color.FromArgb(240, 178, 74);

        public Theme(int hue, int sat)
        {
            Set(hue, sat);
        }

        public void Set(int hue, int sat)
        {
            Hue = ((hue % 360) + 360) % 360;
            Sat = Math.Max(0, Math.Min(100, sat));
            double s = Sat / 100.0;
            Accent = ColorUtil.Hsl(Hue, s, 0.66);
            AccentDeep = ColorUtil.Hsl(Hue, s * 0.9, 0.45);
        }

        public Color Top { get { return ColorUtil.Mix(Color.FromArgb(27, 30, 36), Accent, 0.22); } }
        public Color Bottom { get { return ColorUtil.Mix(Color.FromArgb(13, 15, 19), Accent, 0.10); } }
        public Color TextOn { get { return ColorUtil.Mix(TextMain, Accent, 0.26); } }
    }

    internal sealed class Palette
    {
        public string Id;
        public string Name;
        public int Hue;
        public int Sat;
    }

    internal static class Palettes
    {
        public static readonly List<Palette> All = new List<Palette>(new Palette[]
        {
            new Palette{ Id="cyan",   Name="青",   Hue=190, Sat=78 },
            new Palette{ Id="blue",   Name="蓝",   Hue=216, Sat=82 },
            new Palette{ Id="green",  Name="绿",   Hue=146, Sat=68 },
            new Palette{ Id="amber",  Name="琥珀", Hue=36,  Sat=92 },
            new Palette{ Id="purple", Name="紫",   Hue=268, Sat=66 },
            new Palette{ Id="rose",   Name="玫红", Hue=342, Sat=74 },
            new Palette{ Id="gray",   Name="灰",   Hue=214, Sat=10 }
        });

        public static Palette ById(string id)
        {
            for (int i = 0; i < All.Count; i++) if (All[i].Id == id) return All[i];
            return null;
        }

        public static Color ColorOf(Palette p)
        {
            return ColorUtil.Hsl(p.Hue, p.Sat / 100.0, 0.66);
        }
    }

    // ============================ 配置 ============================
    internal sealed class Config
    {
        public string Endpoint = "";       // 网关根地址，例如 https://gateway.example.com
        public string Form = "ball";
        public int X = int.MinValue;
        public int Y = int.MinValue;
        public int OpacityPct = 88;
        public int Hue = 190;
        public int Sat = 78;
        public string PaletteId = "cyan";
        public bool WarnHigh = true;
        public int IntervalSec = 300;
        public bool Preview = false;
        public bool TrayHintShown = false;

        public bool HasPos { get { return X != int.MinValue && Y != int.MinValue; } }
        public double Opacity { get { return Math.Max(40, Math.Min(100, OpacityPct)) / 100.0; } }

        public static Config Load()
        {
            Config c = new Config();
            try
            {
                if (!File.Exists(AppPaths.IniFile)) return c;
                string[] lines = File.ReadAllLines(AppPaths.IniFile, Encoding.UTF8);
                for (int i = 0; i < lines.Length; i++)
                {
                    string ln = lines[i].Trim();
                    if (ln.Length == 0 || ln[0] == '#' || ln[0] == ';') continue;
                    int eq = ln.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = ln.Substring(0, eq).Trim().ToLowerInvariant();
                    string v = ln.Substring(eq + 1).Trim();
                    switch (k)
                    {
                        case "endpoint": c.Endpoint = v; break;
                        case "form": c.Form = (v == "bar") ? "bar" : "ball"; break;
                        case "x": c.X = ToInt(v, c.X); break;
                        case "y": c.Y = ToInt(v, c.Y); break;
                        case "opacity": c.OpacityPct = ToInt(v, c.OpacityPct); break;
                        case "hue": c.Hue = ToInt(v, c.Hue); break;
                        case "sat": c.Sat = ToInt(v, c.Sat); break;
                        case "palette": c.PaletteId = v; break;
                        case "warn": c.WarnHigh = v == "1" || v.ToLowerInvariant() == "true"; break;
                        case "interval": c.IntervalSec = ToInt(v, c.IntervalSec); break;
                        case "preview": c.Preview = v == "1" || v.ToLowerInvariant() == "true"; break;
                        case "trayhint": c.TrayHintShown = v == "1" || v.ToLowerInvariant() == "true"; break;
                    }
                }
            }
            catch { }
            c.OpacityPct = Math.Max(40, Math.Min(100, c.OpacityPct));
            c.IntervalSec = Math.Max(60, Math.Min(3600, c.IntervalSec));
            c.Hue = ((c.Hue % 360) + 360) % 360;
            c.Sat = Math.Max(0, Math.Min(100, c.Sat));
            return c;
        }

        static int ToInt(string s, int dflt)
        {
            int v;
            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) return v;
            return dflt;
        }

        public void Save()
        {
            try
            {
                AppPaths.Ensure();
                try
                {
                    WindowsIdentity id = WindowsIdentity.GetCurrent();
                    string extra = "";
                    foreach (IdentityReference g in id.Groups)
                    {
                        try
                        {
                            string n = g.Translate(typeof(NTAccount)).Value;
                            if (n.IndexOf("Sandbox", StringComparison.OrdinalIgnoreCase) >= 0) extra += n + " ";
                        }
                        catch { }
                    }
                    AppPaths.Log("identity=" + id.Name + " sandboxGroups=" + (extra.Length > 0 ? extra : "none"));
                }
                catch { }
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("# GWQuota 配置（可手工编辑，程序退出时回写）");
                sb.AppendLine("endpoint=" + Endpoint);
                sb.AppendLine("form=" + Form);
                sb.AppendLine("x=" + X);
                sb.AppendLine("y=" + Y);
                sb.AppendLine("opacity=" + OpacityPct);
                sb.AppendLine("hue=" + Hue);
                sb.AppendLine("sat=" + Sat);
                sb.AppendLine("palette=" + PaletteId);
                sb.AppendLine("warn=" + (WarnHigh ? "1" : "0"));
                sb.AppendLine("interval=" + IntervalSec);
                sb.AppendLine("# preview=1 时详情面板常显（核对用）");
                sb.AppendLine("preview=" + (Preview ? "1" : "0"));
                sb.AppendLine("trayhint=" + (TrayHintShown ? "1" : "0"));
                File.WriteAllText(AppPaths.IniFile, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }
    }

    // ============================ 时区 ============================
    internal static class Tz
    {
        public static readonly TimeZoneInfo Cst = Find();

        static TimeZoneInfo Find()
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById("China Standard Time"); }
            catch
            {
                try { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai"); }
                catch
                {
                    return TimeZoneInfo.CreateCustomTimeZone("CST+8", TimeSpan.FromHours(8), "CST+8", "CST+8");
                }
            }
        }

        public static DateTime FromEpochMs(long ms)
        {
            DateTime utc = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds((double)ms);
            return TimeZoneInfo.ConvertTimeFromUtc(utc, Cst);
        }

        public static DateTime FromIso(string s)
        {
            if (string.IsNullOrEmpty(s)) return DateTime.MaxValue;
            DateTime dt;
            if (!DateTime.TryParse(s, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out dt))
                return DateTime.MaxValue;
            if (dt.Kind != DateTimeKind.Utc) dt = DateTime.SpecifyKind(dt, DateTimeKind.Utc);
            return TimeZoneInfo.ConvertTimeFromUtc(dt, Cst);
        }

        public static DateTime Now { get { return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Cst); } }
    }

    // ============================ JSON 读取 ============================
    internal static class J
    {
        public static Dictionary<string, object> Obj(Dictionary<string, object> d, string k)
        {
            if (d == null) return null;
            object o;
            if (!d.TryGetValue(k, out o)) return null;
            return o as Dictionary<string, object>;
        }

        public static object[] Arr(Dictionary<string, object> d, string k)
        {
            if (d == null) return null;
            object o;
            if (!d.TryGetValue(k, out o)) return null;
            return o as object[];
        }

        public static object Raw(Dictionary<string, object> d, string k)
        {
            if (d == null) return null;
            object o;
            return d.TryGetValue(k, out o) ? o : null;
        }

        public static string Str(Dictionary<string, object> d, string k)
        {
            object o = Raw(d, k);
            if (o == null) return null;
            string s = o as string;
            return s;
        }

        public static double? ToNum(object o)
        {
            if (o == null) return null;
            if (o is bool) return null;
            if (o is double) return (double)o;
            if (o is decimal) return (double)(decimal)o;
            if (o is int) return (double)(int)o;
            if (o is long) return (double)(long)o;
            if (o is float) return (double)(float)o;
            string s = o as string;
            if (s != null)
            {
                double d;
                if (double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
                return null;
            }
            return null;
        }

        public static double? Num(Dictionary<string, object> d, string k)
        {
            return ToNum(Raw(d, k));
        }

        public static int? Int(Dictionary<string, object> d, string k)
        {
            double? n = Num(d, k);
            if (!n.HasValue) return null;
            return (int)Math.Round(n.Value);
        }

        public static bool? Bool(Dictionary<string, object> d, string k)
        {
            object o = Raw(d, k);
            if (o == null) return null;
            if (o is bool) return (bool)o;
            string s = o as string;
            if (s != null)
            {
                s = s.Trim();
                if (s.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
                if (s.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
            }
            double? n = ToNum(o);
            if (n.HasValue) return n.Value != 0;
            return null;
        }

        public static Dictionary<string, object> Parse(string json)
        {
            JavaScriptSerializer ser = new JavaScriptSerializer();
            ser.MaxJsonLength = 8 * 1024 * 1024;
            return ser.DeserializeObject(json) as Dictionary<string, object>;
        }
    }

    // ============================ 数据模型 ============================
    internal sealed class Balance
    {
        public bool Present;
        public bool Ok = true;
        public string Currency = "CNY";
        public double? Total;
        public double? Granted;
        public double? ToppedUp;
        public double? Available;
        public double? Cash;
        public double? Voucher;
        public string BaseUrl;

        public double? Amount
        {
            get
            {
                if (Total.HasValue) return Total;
                if (Available.HasValue) return Available;
                return null;
            }
        }

        public string Text
        {
            get
            {
                if (!Present) return "—";
                if (!Ok) return "读取失败";
                if (!Amount.HasValue) return "—";
                string sym = Currency == "CNY" ? "¥" : (Currency == "USD" ? "$" : (Currency ?? "") + " ");
                return sym + Amount.Value.ToString("0.00", CultureInfo.InvariantCulture);
            }
        }
    }

    internal sealed class QuotaWindow
    {
        public bool Present;
        public bool Ok = true;
        public double? Percent;
        public string Status = "";
        public DateTime? Reset;

        public bool Good
        {
            get
            {
                return Present && Ok && (Status.Length == 0 ||
                       string.Equals(Status, "ok", StringComparison.OrdinalIgnoreCase));
            }
        }

        public double? Remain
        {
            get { if (!Percent.HasValue) return null; return Math.Max(0, 100 - Percent.Value); }
        }
    }

    internal sealed class ModelRow
    {
        public string Model;
        public double? H5, W7, D30, Cap;
        public int? N5, N7, N30;
        public bool Priced;
    }

    internal sealed class Snapshot
    {
        public DateTime FetchedAt;
        public DateTime? ServerTs;
        public Balance DeepSeek = new Balance();
        public Balance Kimi = new Balance();
        public QuotaWindow Rolling = new QuotaWindow();
        public QuotaWindow Weekly = new QuotaWindow();
        public QuotaWindow Monthly = new QuotaWindow();
        public bool QuotaOk;
        public double? WH5, WW7, WD30;
        public int? RH5, RW7, RD30;
        public List<ModelRow> Models = new List<ModelRow>();

        public bool HasQuota
        {
            get { return Rolling.Present || Weekly.Present || Monthly.Present; }
        }
    }

    internal enum NetState
    {
        Init,
        Ok,
        NoKey,
        Network,
        Unauthorized,
        Forbidden,
        RateLimited,
        GatewayOff,
        HttpError
    }

    internal sealed class Outcome
    {
        public NetState State;
        public Snapshot Snap;
        public string Message = "";
    }

    // ============================ 解析 ============================
    internal static class QuotaParser
    {
        public static bool TryParse(string json, out Snapshot snap, out string err)
        {
            snap = null;
            err = "";
            try
            {
                Dictionary<string, object> root = J.Parse(json);
                if (root == null) { err = "根节点不是对象"; return false; }

                Snapshot s = new Snapshot();
                s.FetchedAt = Tz.Now;

                double? ts = J.Num(root, "ts");
                if (ts.HasValue) s.ServerTs = Tz.FromEpochMs((long)ts.Value);

                ParseBalance(J.Obj(root, "deepseek"), s.DeepSeek);
                ParseBalance(J.Obj(root, "kimi"), s.Kimi);

                Dictionary<string, object> oc = J.Obj(root, "opencode");
                if (oc != null)
                {
                    Dictionary<string, object> q = J.Obj(oc, "quota");
                    if (q != null)
                    {
                        bool? ok = J.Bool(q, "ok");
                        s.QuotaOk = ok.HasValue ? ok.Value : true;
                        s.Rolling = ParseWindow(J.Obj(q, "rolling"));
                        s.Weekly = ParseWindow(J.Obj(q, "weekly"));
                        s.Monthly = ParseWindow(J.Obj(q, "monthly"));
                    }

                    Dictionary<string, object> w = J.Obj(oc, "windows");
                    if (w != null)
                    {
                        s.WH5 = J.Num(w, "h5");
                        s.WW7 = J.Num(w, "w7");
                        s.WD30 = J.Num(w, "d30");
                        Dictionary<string, object> rq = J.Obj(w, "reqs");
                        if (rq != null)
                        {
                            s.RH5 = J.Int(rq, "h5");
                            s.RW7 = J.Int(rq, "w7");
                            s.RD30 = J.Int(rq, "d30");
                        }
                    }

                    object[] arr = J.Arr(oc, "models");
                    if (arr != null)
                    {
                        for (int i = 0; i < arr.Length && i < 8; i++)
                        {
                            Dictionary<string, object> m = arr[i] as Dictionary<string, object>;
                            if (m == null) continue;
                            ModelRow r = new ModelRow();
                            r.Model = J.Str(m, "model");
                            r.H5 = J.Num(m, "h5");
                            r.W7 = J.Num(m, "w7");
                            r.D30 = J.Num(m, "d30");
                            r.Cap = J.Num(m, "cap");
                            r.N5 = J.Int(m, "n5");
                            r.N7 = J.Int(m, "n7");
                            r.N30 = J.Int(m, "n30");
                            bool? pr = J.Bool(m, "priced");
                            r.Priced = pr.HasValue ? pr.Value : true;
                            if (!string.IsNullOrEmpty(r.Model)) s.Models.Add(r);
                        }
                    }
                }

                snap = s;
                return true;
            }
            catch (Exception ex)
            {
                err = ex.Message;
                return false;
            }
        }

        static void ParseBalance(Dictionary<string, object> d, Balance b)
        {
            if (d == null) return;
            b.Present = true;
            bool? ok = J.Bool(d, "ok");
            b.Ok = ok.HasValue ? ok.Value : true;
            string cur = J.Str(d, "currency");
            if (!string.IsNullOrEmpty(cur)) b.Currency = cur;
            b.Total = J.Num(d, "total");
            b.Granted = J.Num(d, "granted");
            b.ToppedUp = J.Num(d, "topped_up");
            b.Cash = J.Num(d, "cash");
            b.Voucher = J.Num(d, "voucher");
            object av = J.Raw(d, "available");
            if (!(av is bool)) b.Available = J.ToNum(av);
            b.BaseUrl = J.Str(d, "base");
        }

        static QuotaWindow ParseWindow(Dictionary<string, object> d)
        {
            QuotaWindow w = new QuotaWindow();
            if (d == null) return w;
            w.Present = true;
            bool? ok = J.Bool(d, "ok");
            w.Ok = ok.HasValue ? ok.Value : true;
            w.Percent = J.Num(d, "percent");
            if (w.Percent.HasValue) w.Percent = Math.Max(0, Math.Min(100, w.Percent.Value));
            string st = J.Str(d, "status");
            w.Status = st == null ? "" : st;
            string rs = J.Str(d, "resets_at");
            if (!string.IsNullOrEmpty(rs))
            {
                DateTime t = Tz.FromIso(rs);
                if (t != DateTime.MaxValue) w.Reset = t;
            }
            return w;
        }
    }

    // ============================ 轮询 ============================
    internal sealed class Poller : IDisposable
    {
        readonly Func<string> _key;
        readonly Func<string> _endpoint;
        readonly int _intervalSec;
        readonly ManualResetEventSlim _pulse = new ManualResetEventSlim(false);
        volatile bool _stop;
        Thread _thread;

        public event Action<Outcome> Completed;

        public Poller(Func<string> keyGetter, Func<string> endpointGetter, int intervalSec)
        {
            _key = keyGetter;
            _endpoint = endpointGetter;
            _intervalSec = Math.Max(60, intervalSec);
        }

        public void Start()
        {
            _thread = new Thread(Loop);
            _thread.IsBackground = true;
            _thread.Name = "gwquota-poll";
            _thread.Start();
        }

        public void RefreshNow()
        {
            try { _pulse.Set(); }
            catch { }
        }

        public void Dispose()
        {
            _stop = true;
            try { _pulse.Set(); } catch { }
        }

        void Loop()
        {
            int delayMs = 400;
            DateTime lastFetch = DateTime.MinValue;
            while (!_stop)
            {
                try { _pulse.Wait(delayMs); }
                catch { }
                if (_stop) break;
                _pulse.Reset();

                int gap = (int)(DateTime.Now - lastFetch).TotalMilliseconds;
                if (gap < 2500)
                {
                    try { Thread.Sleep(2500 - gap); }
                    catch { }
                }
                if (_stop) break;
                Outcome o = FetchOnce();
                lastFetch = DateTime.Now;
                Action<Outcome> h = Completed;
                if (h != null)
                {
                    try { h(o); }
                    catch (Exception ex) { AppPaths.Log("handler: " + ex.Message); }
                }
                delayMs = NextDelay(o.State) * 1000;
            }
        }

        int NextDelay(NetState st)
        {
            switch (st)
            {
                case NetState.Unauthorized: return 600;
                case NetState.Forbidden: return 600;
                case NetState.RateLimited: return 8;
                case NetState.GatewayOff: return 120;
                case NetState.NoKey: return 30;
                case NetState.Network: return 60;
                case NetState.HttpError: return 120;
                default: return _intervalSec;
            }
        }

        Outcome FetchOnce()
        {
            string endpoint = (_endpoint() ?? "").Trim();
            if (endpoint.Length == 0)
            {
                Outcome noUrl = new Outcome();
                noUrl.State = NetState.NoKey;
                noUrl.Message = "未配置网关地址";
                return noUrl;
            }
            string key = (_key() ?? "").Trim();
            if (key.Length == 0)
            {
                Outcome no = new Outcome();
                no.State = NetState.NoKey;
                no.Message = "未配置只读钥匙";
                return no;
            }
            try
            {
                try
                {
                    ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | (SecurityProtocolType)0x3000;
                    ServicePointManager.Expect100Continue = false;
                }
                catch { }

                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(endpoint);
                req.Method = "GET";
                req.Timeout = 15000;
                req.ReadWriteTimeout = 15000;
                req.KeepAlive = false;
                req.UserAgent = "GWQuota/" + Consts.Version;
                req.Accept = "application/json";
                req.Headers[HttpRequestHeader.Authorization] = "Bearer " + key;

                string body;
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                        body = sr.ReadToEnd();
                }

                Snapshot snap;
                string perr;
                if (!QuotaParser.TryParse(body, out snap, out perr))
                {
                    AppPaths.Log("parse failed: " + perr);
                    Outcome bad = new Outcome();
                    bad.State = NetState.HttpError;
                    bad.Message = "数据解析失败";
                    return bad;
                }
                Outcome good = new Outcome();
                good.State = NetState.Ok;
                good.Snap = snap;
                return good;
            }
            catch (WebException ex)
            {
                HttpWebResponse hr = ex.Response as HttpWebResponse;
                int code = hr != null ? (int)hr.StatusCode : 0;
                Outcome o = new Outcome();
                switch (code)
                {
                    case 401: o.State = NetState.Unauthorized; o.Message = "只读钥匙失效（401）"; break;
                    case 403: o.State = NetState.Forbidden; o.Message = "调用了非只读接口（403）"; break;
                    case 429: o.State = NetState.RateLimited; o.Message = "触发限速（429），已退避"; break;
                    case 503: o.State = NetState.GatewayOff; o.Message = "网关总开关已关（503）"; break;
                    case 0: o.State = NetState.Network; o.Message = "网络不可达或超时"; break;
                    default: o.State = NetState.HttpError; o.Message = "HTTP " + code; break;
                }
                AppPaths.Log("fetch: " + o.Message + (code == 0 ? " " + ex.Status.ToString() : ""));
                return o;
            }
            catch (Exception ex)
            {
                AppPaths.Log("fetch ex: " + ex.GetType().Name + " " + ex.Message);
                Outcome o = new Outcome();
                o.State = NetState.Network;
                o.Message = "请求异常";
                return o;
            }
        }
    }

    // ============================ 分层窗口（逐像素透明） ============================
    // 用 UpdateLayeredWindow 直接提交 32 位带 alpha 的位图：
    //   * 边缘可以做真正的半透明渐变（不再被 Region 裁成硬边）
    //   * 球面光影、投影都画得出来
    //   * alpha=0 的像素自动鼠标穿透，不会挡住背后的窗口
    internal sealed class LayeredSurface : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        struct POINT { public int X, Y; public POINT(int x, int y) { X = x; Y = y; } }

        [StructLayout(LayoutKind.Sequential)]
        struct SIZE { public int CX, CY; public SIZE(int cx, int cy) { CX = cx; CY = cy; } }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

        [StructLayout(LayoutKind.Sequential)]
        struct BITMAPINFOHEADER
        {
            public int biSize; public int biWidth; public int biHeight;
            public short biPlanes; public short biBitCount; public int biCompression;
            public int biSizeImage; public int biXPelsPerMeter; public int biYPelsPerMeter;
            public int biClrUsed; public int biClrImportant;
        }

        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER pbmi,
            uint usage, out IntPtr bits, IntPtr hSection, uint offset);
        [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst,
            IntPtr pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, uint crKey,
            ref BLENDFUNCTION pblend, uint dwFlags);

        Bitmap _bmp;
        Graphics _g;
        IntPtr _screenDc, _memDc, _dib, _old, _bits;
        int _w, _h;

        public Graphics Begin(int w, int h)
        {
            if (_bmp == null || _w != w || _h != h) Create(w, h);
            _g.Clear(Color.Transparent);
            return _g;
        }

        void Create(int w, int h)
        {
            Release();
            _w = w;
            _h = h;
            _screenDc = GetDC(IntPtr.Zero);
            _memDc = CreateCompatibleDC(_screenDc);

            BITMAPINFOHEADER bi = new BITMAPINFOHEADER();
            bi.biSize = 40;
            bi.biWidth = w;
            bi.biHeight = -h;          // 负数 = 自上而下
            bi.biPlanes = 1;
            bi.biBitCount = 32;
            bi.biCompression = 0;      // BI_RGB
            _dib = CreateDIBSection(_memDc, ref bi, 0, out _bits, IntPtr.Zero, 0);
            _old = SelectObject(_memDc, _dib);

            _bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);   // 预乘 alpha，正好匹配 UpdateLayeredWindow
            _g = Graphics.FromImage(_bmp);
            _g.SmoothingMode = SmoothingMode.AntiAlias;
            _g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            _g.CompositingQuality = CompositingQuality.HighQuality;
            _g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        }

        public void End(IntPtr hwnd, byte alpha)
        {
            if (_bmp == null || !IsWindow(hwnd)) return;
            Rectangle rect = new Rectangle(0, 0, _w, _h);
            BitmapData bd = _bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                int srcStride = bd.Stride;
                int dstStride = _w * 4;
                if (srcStride == dstStride)
                {
                    byte[] buf = new byte[dstStride * _h];
                    Marshal.Copy(bd.Scan0, buf, 0, buf.Length);
                    Marshal.Copy(buf, 0, _bits, buf.Length);
                }
                else
                {
                    byte[] row = new byte[dstStride];
                    for (int y = 0; y < _h; y++)
                    {
                        Marshal.Copy(new IntPtr(bd.Scan0.ToInt64() + (long)y * srcStride), row, 0, dstStride);
                        Marshal.Copy(row, 0, new IntPtr(_bits.ToInt64() + (long)y * dstStride), dstStride);
                    }
                }
            }
            finally { _bmp.UnlockBits(bd); }

            SIZE size = new SIZE(_w, _h);
            POINT src = new POINT(0, 0);
            BLENDFUNCTION bf = new BLENDFUNCTION();
            bf.BlendOp = 0;                 // AC_SRC_OVER
            bf.BlendFlags = 0;
            bf.SourceConstantAlpha = alpha; // 整窗透明度
            bf.AlphaFormat = 1;             // AC_SRC_ALPHA
            UpdateLayeredWindow(hwnd, _screenDc, IntPtr.Zero, ref size, _memDc, ref src, 0, ref bf, 2);
        }

        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);

        void Release()
        {
            if (_g != null) { _g.Dispose(); _g = null; }
            if (_bmp != null) { _bmp.Dispose(); _bmp = null; }
            if (_memDc != IntPtr.Zero && _old != IntPtr.Zero) { SelectObject(_memDc, _old); _old = IntPtr.Zero; }
            if (_dib != IntPtr.Zero) { DeleteObject(_dib); _dib = IntPtr.Zero; }
            if (_memDc != IntPtr.Zero) { DeleteDC(_memDc); _memDc = IntPtr.Zero; }
            if (_screenDc != IntPtr.Zero) { ReleaseDC(IntPtr.Zero, _screenDc); _screenDc = IntPtr.Zero; }
            _bits = IntPtr.Zero;
            _w = 0;
            _h = 0;
        }

        public void Dispose() { Release(); }
    }

    // ============================ 悬浮窗 ============================
    internal sealed class WidgetForm : Form
    {
        readonly AppHost _app;
        readonly WinTimer _hover = new WinTimer();
        readonly StringFormat _measure = (StringFormat)StringFormat.GenericTypographic.Clone();
        readonly LayeredSurface _layer = new LayeredSurface();

        float _s = 1f;
        bool _dragging, _inside, _menuOpen;
        int _insideSince, _lastIn;
        Point _dragOffset;

        // 可见尺寸（96 DPI）；窗口再各留一圈给投影
        public const int BallDia = 55;        // 当前球径的 60%
        public const int BallMargin = 18;
        public const int BarW = 500;
        public const int BarH = 42;
        public const int BarMargin = 13;

        public WidgetForm(AppHost app)
        {
            _app = app;
            SetStyle(ControlStyles.Opaque | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Color.Black;
            Cursor = Cursors.SizeAll;

            _hover.Interval = 110;
            _hover.Tick += HoverTick;
            _hover.Start();
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x00000080;   // WS_EX_TOOLWINDOW
                cp.ExStyle |= 0x00080000;   // WS_EX_LAYERED
                return cp;
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e) { }

        public float UiScale { get { return _s; } }

        public Size VisibleSize
        {
            get
            {
                if (_app.Cfg.Form == "bar")
                    return new Size((int)(BarW * _s), (int)(BarH * _s));
                return new Size((int)(BallDia * _s), (int)(BallDia * _s));
            }
        }

        Size WindowSize(string form)
        {
            if (form == "bar")
                return new Size((int)((BarW + BarMargin * 2) * _s), (int)((BarH + BarMargin * 2) * _s));
            return new Size((int)((BallDia + BallMargin * 2) * _s), (int)((BallDia + BallMargin * 2) * _s));
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        const int HotkeyId = 0xA71;
        bool _hotkeyOk;

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                using (Graphics g = CreateGraphics())
                    _s = Math.Max(0.75f, Math.Min(2.5f, g.DpiX / 96f));
            }
            catch { _s = 1f; }
            ApplyForm(_app.Cfg.Form, true);
            // 托盘不可用时的备用入口：Ctrl+Alt+Q 在鼠标处弹出完整菜单
            try
            {
                _hotkeyOk = RegisterHotKey(Handle, HotkeyId, 0x0001 | 0x0002 /*ALT|CONTROL*/, 0x51 /*Q*/);
                AppPaths.Log("hotkey Ctrl+Alt+Q registered=" + _hotkeyOk);
            }
            catch (Exception ex) { AppPaths.Log("hotkey ex: " + ex.Message); }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0312 && m.WParam.ToInt32() == HotkeyId)   // WM_HOTKEY
            {
                _app.ShowMenuAtCursor();
                return;
            }
            base.WndProc(ref m);
        }

        public void SetMenuOpen(bool open)
        {
            _menuOpen = open;
        }

        public void ApplyForm(string name, bool initial)
        {
            _app.Cfg.Form = (name == "bar") ? "bar" : "ball";
            Size sz = WindowSize(_app.Cfg.Form);
            Point center = new Point(Left + Width / 2, Top + Height / 2);
            Size = sz;

            Point loc;
            if (initial)
            {
                if (_app.Cfg.HasPos) loc = new Point(_app.Cfg.X, _app.Cfg.Y);
                else
                {
                    Rectangle wa = Screen.PrimaryScreen.WorkingArea;
                    loc = new Point(wa.Left + (wa.Width - sz.Width) / 2, wa.Top + (wa.Height - sz.Height) / 2);
                }
            }
            else loc = new Point(center.X - sz.Width / 2, center.Y - sz.Height / 2);

            Location = ClampWidget(new Rectangle(loc, sz)).Location;
            if (!initial)
            {
                _app.Cfg.X = Location.X;
                _app.Cfg.Y = Location.Y;
                _app.Cfg.Save();
            }
            Render();
        }

        /// 当前图形距窗口边的留白（投影留白）
        float MarginNow
        {
            get { return (_app.Cfg.Form == "bar" ? BarMargin : BallMargin) * _s; }
        }

        /// 吸附到屏幕边缘时，允许窗口向屏幕外探出 2/3 留白，
        /// 这样“可见图形”离屏幕边只剩 1/3 留白，看起来贴边更紧。
        Rectangle ClampWidget(Rectangle r)
        {
            return ClampToScreen(r, (int)Math.Round(MarginNow * 2f / 3f));
        }

        public static Rectangle ClampToScreen(Rectangle r)
        {
            return ClampToScreen(r, 0);
        }

        public static Rectangle ClampToScreen(Rectangle r, int overhang)
        {
            Screen best = null;
            int bestArea = 0;
            Screen[] all = Screen.AllScreens;
            for (int i = 0; i < all.Length; i++)
            {
                Rectangle it = Rectangle.Intersect(all[i].WorkingArea, r);
                int a = it.Width * it.Height;
                if (a > bestArea) { bestArea = a; best = all[i]; }
            }
            if (best == null || bestArea == 0) best = Screen.PrimaryScreen;
            Rectangle wa = best.WorkingArea;
            int x = Math.Max(wa.Left - overhang, Math.Min(r.X, wa.Right - r.Width + overhang));
            int y = Math.Max(wa.Top - overhang, Math.Min(r.Y, wa.Bottom - r.Height + overhang));
            return new Rectangle(x, y, r.Width, r.Height);
        }

        // ---------------- 交互 ----------------
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            _dragging = true;
            _dragOffset = new Point(e.X, e.Y);
            Capture = true;
            _app.HideDetail();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!_dragging) return;
            Point p = Cursor.Position;
            Location = new Point(p.X - _dragOffset.X, p.Y - _dragOffset.Y);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left || !_dragging) return;
            _dragging = false;
            Capture = false;
            Location = ClampWidget(Bounds).Location;
            _app.Cfg.X = Location.X;
            _app.Cfg.Y = Location.Y;
            _app.Cfg.Save();
            _lastIn = Environment.TickCount;
            _inside = Bounds.Contains(Cursor.Position);
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (e.Button == MouseButtons.Left) _app.RefreshNow();
        }

        void HoverTick(object sender, EventArgs e)
        {
            if (!Visible || _dragging) return;
            Point p = Cursor.Position;
            bool overSelf = VisibleRect.Contains(p);
            bool overDetail = _app.DetailVisible && _app.DetailBounds.Contains(p);
            int now = Environment.TickCount;

            if (overSelf || overDetail)
            {
                if (!_inside) { _inside = true; _insideSince = now; }
                _lastIn = now;
                if (!_menuOpen && !_app.DetailVisible && now - _insideSince > 170) _app.ShowDetail();
            }
            else
            {
                _inside = false;
                if (_app.DetailVisible && now - _lastIn > 420) _app.HideDetail();
            }
        }

        /// 可见区域（不含投影留白），供悬停判定与面板定位用
        public Rectangle VisibleRect
        {
            get
            {
                Size vs = VisibleSize;
                Rectangle b = Bounds;
                return new Rectangle(b.X + (b.Width - vs.Width) / 2, b.Y + (b.Height - vs.Height) / 2, vs.Width, vs.Height);
            }
        }

        // ---------------- 渲染 ----------------
        public void Render()
        {
            if (!IsHandleCreated || Width <= 0 || Height <= 0) return;
            try
            {
                Graphics g = _layer.Begin(Width, Height);
                if (_app.Cfg.Form == "bar") PaintBar(g);
                else PaintBall(g);
                // 整窗 alpha 用 255：让文字保持实心；半透明只作用在“背景”绘制上
                _layer.End(Handle, 255);
            }
            catch (Exception ex)
            {
                AppPaths.Log("render: " + ex.Message);
            }
        }

        protected override void OnPaint(PaintEventArgs e) { Render(); }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            if (IsHandleCreated) Render();
        }

        Color RingColor(double pct)
        {
            if (_app.Cfg.WarnHigh && pct >= 90) return Theme.Warn;
            return _app.The.Accent;
        }

        // ---- 投影：由外向内叠加，边缘自然渐隐 ----
        void PaintRoundShadow(Graphics g, RectangleF body, float radius, int steps, int maxAlpha)
        {
            for (int i = steps; i >= 1; i--)
            {
                float grow = radius + i * 1.15f;
                RectangleF r = RectangleF.Inflate(body, i * 1.15f, i * 1.15f);
                int a = (int)Math.Round((double)maxAlpha * (1.0 - (double)i / (steps + 1)) / steps * 1.9);
                if (a <= 0) continue;
                using (SolidBrush b = new SolidBrush(Color.FromArgb(a, 0, 0, 0)))
                    g.FillRectangle(b, r);
            }
        }

        void PaintBall(Graphics g)
        {
            Theme th = _app.The;
            Snapshot snap = _app.Last;
            float s = _s;
            float S = BallDia * s;
            float cx = Width / 2f;
            float cy = Height / 2f;
            RectangleF ball = new RectangleF(cx - S / 2f, cy - S / 2f, S, S);
            bool dimmed = _app.State != NetState.Ok;
            int bgA = (int)Math.Round(Math.Max(0.35, Math.Min(1.0, _app.Cfg.Opacity)) * 255);

            // 1) 柔和外投影
            int steps = 14;
            float outer = Math.Min(Width, Height) / 2f - 0.5f;
            float innerR = S / 2f;
            for (int i = steps; i >= 0; i--)
            {
                double t = (double)i / steps;                 // 0=紧贴球，1=最外
                float r = (float)(innerR + (outer - innerR) * t);
                int a = (int)Math.Round(13.0 * Math.Pow(1.0 - t, 2.2));
                if (a <= 0) continue;
                using (SolidBrush b = new SolidBrush(Color.FromArgb(a, 0, 0, 0)))
                    g.FillEllipse(b, cx - r, cy - r, r * 2, r * 2);
            }

            // 2) 球体主光：偏左上聚焦的径向渐变
            using (GraphicsPath p = new GraphicsPath())
            {
                p.AddEllipse(ball);
                using (PathGradientBrush bg = new PathGradientBrush(p))
                {
                    bg.CenterPoint = new PointF(cx - S * 0.20f, cy - S * 0.22f);
                    bg.CenterColor = ColorUtil.A(ColorUtil.Mix(Color.FromArgb(96, 106, 120), th.Accent, 0.42), bgA);
                    bg.SurroundColors = new Color[] { ColorUtil.A(ColorUtil.Mix(Color.FromArgb(8, 10, 14), th.Accent, 0.08), bgA) };
                    bg.FocusScales = new PointF(0.06f, 0.06f);
                    g.FillPath(bg, p);
                }
            }
            // 3) 边缘压暗（球体转折）
            using (GraphicsPath p = new GraphicsPath())
            {
                p.AddEllipse(ball);
                using (PathGradientBrush rim = new PathGradientBrush(p))
                {
                    rim.CenterColor = Color.FromArgb(0, 0, 0, 0);
                    rim.SurroundColors = new Color[] { Color.FromArgb(dimmed ? 70 : 105, 0, 0, 0) };
                    rim.FocusScales = new PointF(0.60f, 0.60f);
                    g.FillPath(rim, p);
                }
            }
            // 4) 左上镜面高光（收小、上移、减弱，避免把数字冲白）
            RectangleF hl = new RectangleF(cx - S * 0.40f, cy - S * 0.46f, S * 0.44f, S * 0.26f);
            using (GraphicsPath p = new GraphicsPath())
            {
                p.AddEllipse(hl);
                using (PathGradientBrush hg = new PathGradientBrush(p))
                {
                    hg.CenterColor = Color.FromArgb(dimmed ? 55 : 88, 255, 255, 255);
                    hg.SurroundColors = new Color[] { Color.FromArgb(0, 255, 255, 255) };
                    g.FillPath(hg, p);
                }
            }
            // 数字底下压一层暗色，保证任何桌面背景下都读得清
            RectangleF scrim = new RectangleF(cx - S * 0.34f, cy - S * 0.28f, S * 0.68f, S * 0.56f);
            using (GraphicsPath p = new GraphicsPath())
            {
                p.AddEllipse(scrim);
                using (PathGradientBrush sg = new PathGradientBrush(p))
                {
                    sg.CenterColor = Color.FromArgb(dimmed ? 90 : 115, 4, 6, 9);
                    sg.SurroundColors = new Color[] { Color.FromArgb(0, 4, 6, 9) };
                    g.FillPath(sg, p);
                }
            }
            // 5) 外缘细亮线，收边更利落
            using (Pen edge = new Pen(Color.FromArgb(dimmed ? 45 : 78, th.Accent.R, th.Accent.G, th.Accent.B), 0.9f * s))
                g.DrawEllipse(edge, Gfx.Inset(ball, 0.45f * s));

            // 6) 月度外环
            double pct = (snap != null && snap.Monthly.Percent.HasValue) ? snap.Monthly.Percent.Value : -1;
            float rw = 2.6f * s;
            RectangleF ringRect = Gfx.Inset(ball, rw * 0.5f + 1.0f * s);
            using (Pen track = new Pen(Color.FromArgb(dimmed ? 34 : 62, th.Accent.R, th.Accent.G, th.Accent.B), rw))
                g.DrawArc(track, ringRect, -90f, 359.9f);
            if (pct >= 0)
            {
                Color col = RingColor(pct);
                using (Pen arc = new Pen(Color.FromArgb(dimmed ? 150 : 245, col.R, col.G, col.B), rw))
                {
                    arc.StartCap = LineCap.Round;
                    arc.EndCap = LineCap.Round;
                    float sweep = (float)Math.Min(359.9, pct * 3.6);
                    if (sweep > 0.4f) g.DrawArc(arc, ringRect, -90f, sweep);
                }
            }
            // 7) 5 小时内环
            if (snap != null && snap.Rolling.Percent.HasValue)
            {
                float rw2 = 1.5f * s;
                RectangleF inner = Gfx.Inset(ball, rw2 + 4.2f * s);
                Color c2 = RingColor(snap.Rolling.Percent.Value);
                using (Pen arc = new Pen(Color.FromArgb(dimmed ? 80 : 165, c2.R, c2.G, c2.B), rw2))
                {
                    arc.StartCap = LineCap.Round;
                    arc.EndCap = LineCap.Round;
                    float sw = (float)Math.Min(359.9, snap.Rolling.Percent.Value * 3.6);
                    if (sw > 0.4f) g.DrawArc(arc, inner, -90f, sw);
                }
            }

            // 8) 中心数字：字号沿用原设计（0.30 × 92）
            string main;
            string sub = "";
            if (snap != null && snap.Monthly.Remain.HasValue)
            {
                main = ((int)Math.Round(snap.Monthly.Remain.Value, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture);
                sub = "%";
            }
            else main = "—";

            float fMain = 92f * 0.300f * s;
            using (Font fM = Gfx.FX("Segoe UI", fMain, FontStyle.Bold))
            using (Font fS = Gfx.FX("Segoe UI", fMain * 0.42f, FontStyle.Bold))
            using (SolidBrush brM = new SolidBrush(Color.FromArgb(dimmed ? 190 : 255, th.TextOn)))
            using (SolidBrush brS = new SolidBrush(Color.FromArgb(dimmed ? 170 : 245, th.Accent)))
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Near;      // 左对齐 + 宽矩形：GDI+ 不会把末位数字截掉
                sf.LineAlignment = StringAlignment.Center;
                sf.FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip;

                SizeF szM = g.MeasureString(main, fM, new PointF(0, 0), _measure);
                SizeF szS = sub.Length > 0 ? g.MeasureString(sub, fS, new PointF(0, 0), _measure) : SizeF.Empty;
                float gap = sub.Length > 0 ? 2.4f * s : 0f;
                float totalW = szM.Width + gap + szS.Width;
                float x = cx - totalW / 2f;
                float room = 28f * s;                     // 右侧余量，防裁切

                using (SolidBrush shadow = new SolidBrush(Color.FromArgb(120, 0, 0, 0)))
                    g.DrawString(main, fM, shadow, new RectangleF(x + 0.8f, cy - fMain * 0.62f + 1.0f, szM.Width + room, fMain * 1.24f), sf);
                g.DrawString(main, fM, brM, new RectangleF(x, cy - fMain * 0.62f, szM.Width + room, fMain * 1.24f), sf);
                if (sub.Length > 0)
                    g.DrawString(sub, fS, brS, new RectangleF(x + szM.Width + gap, cy - fMain * 0.22f, szS.Width + room, fMain * 0.80f), sf);
            }

            // 9) 状态点
            if (dimmed)
            {
                Color sc = _app.StateColor(_app.State);
                float r = S * 0.105f;
                float bx = cx + S * 0.255f;
                float by = cy + S * 0.255f;
                using (SolidBrush b = new SolidBrush(Color.FromArgb(235, 10, 12, 16)))
                    g.FillEllipse(b, bx - r * 1.30f, by - r * 1.30f, r * 2.6f, r * 2.6f);
                using (SolidBrush b = new SolidBrush(sc))
                    g.FillEllipse(b, bx - r, by - r, r * 2f, r * 2f);
            }
        }

        void PaintBar(Graphics g)
        {
            Theme th = _app.The;
            Snapshot snap = _app.Last;
            float s = _s;
            float m = BarMargin * s;
            RectangleF body = new RectangleF(m, m, BarW * s, BarH * s);
            bool dimmed = _app.State != NetState.Ok;
            int bgA = (int)Math.Round(Math.Max(0.35, Math.Min(1.0, _app.Cfg.Opacity)) * 255);

            // 投影
            for (int i = 10; i >= 1; i--)
            {
                int a = (int)Math.Round(13.0 * Math.Pow(1.0 - (double)i / 11.0, 2.0));
                if (a <= 0) continue;
                RectangleF r = RectangleF.Inflate(body, i * 0.9f, i * 0.9f);
                r.Offset(0, i * 0.35f);
                using (GraphicsPath sp = Gfx.RoundRect(r, 10 * s + i))
                using (SolidBrush b = new SolidBrush(Color.FromArgb(a, 0, 0, 0)))
                    g.FillPath(b, sp);
            }

            using (GraphicsPath p = Gfx.RoundRect(body, 10 * s))
            {
                using (LinearGradientBrush br = new LinearGradientBrush(body, ColorUtil.A(th.Top, bgA), ColorUtil.A(th.Bottom, bgA), 90f))
                    g.FillPath(br, p);
                using (Pen pen = new Pen(Color.FromArgb(dimmed ? 55 : 85, th.Accent.R, th.Accent.G, th.Accent.B), 1f))
                    g.DrawPath(pen, p);
            }

            float x = body.X + 13 * s;
            float midY = body.Y + body.Height / 2f;

            x = DrawQuota(g, x, midY, "5 小时", snap != null ? snap.Rolling : null, s, dimmed);
            x = DrawDivider(g, x, midY, s);
            x = DrawQuota(g, x, midY, "本周", snap != null ? snap.Weekly : null, s, dimmed);
            x = DrawDivider(g, x, midY, s);
            x = DrawQuota(g, x, midY, "本月", snap != null ? snap.Monthly : null, s, dimmed);
            x = DrawDivider(g, x, midY, s);
            x = DrawBalance(g, x, midY, "DS", snap != null ? snap.DeepSeek : null, s, dimmed);
            x = DrawDivider(g, x, midY, s);
            x = DrawBalance(g, x, midY, "Kimi", snap != null ? snap.Kimi : null, s, dimmed);

            // 右端：状态/更新时间（右对齐，不换行）
            string right = _app.StateText(true);
            Color rc = _app.State != NetState.Ok ? _app.StateColor(_app.State) : Color.FromArgb(225, Theme.TextDim);
            using (Font f = Gfx.FP("Microsoft YaHei UI", 7.6f * s, FontStyle.Regular))
            using (SolidBrush b = new SolidBrush(rc))
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Far;
                sf.LineAlignment = StringAlignment.Center;
                sf.FormatFlags = StringFormatFlags.NoWrap;
                g.DrawString(right, f, b, new RectangleF(x, body.Y, body.Right - 12 * s - x, body.Height), sf);
            }
        }

        float DrawQuota(Graphics g, float x, float midY, string label, QuotaWindow w, float s, bool dimmed)
        {
            Theme th = _app.The;
            double? pv = (w != null && w.Good && w.Percent.HasValue) ? w.Percent : null;

            using (Font f = Gfx.FP("Microsoft YaHei UI", 8.2f * s, FontStyle.Regular))
            using (SolidBrush b = new SolidBrush(Color.FromArgb(dimmed ? 170 : 240, Theme.TextDim)))
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Near;
                sf.LineAlignment = StringAlignment.Center;
                sf.FormatFlags = StringFormatFlags.NoWrap;
                SizeF sz = g.MeasureString(label, f);
                g.DrawString(label, f, b, new RectangleF(x, midY - 11 * s, sz.Width + 20 * s, 22 * s), sf);
                x += sz.Width + 3.5f * s;
            }

            string pctText = pv.HasValue ? ((int)Math.Round(pv.Value)).ToString(CultureInfo.InvariantCulture) + "%" : "—";
            Color pc = pv.HasValue ? (CfgWarn(pv.Value) ? Theme.Warn : th.Accent) : Theme.TextDim;
            using (Font f = Gfx.FP("Microsoft YaHei UI", 10.4f * s, FontStyle.Bold))
            using (SolidBrush b = new SolidBrush(Color.FromArgb(dimmed ? 175 : 250, pc)))
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Near;
                sf.LineAlignment = StringAlignment.Center;
                sf.FormatFlags = StringFormatFlags.NoWrap;
                SizeF sz = g.MeasureString(pctText, f);
                g.DrawString(pctText, f, b, new RectangleF(x, midY - 12 * s, sz.Width + 20 * s, 24 * s), sf);
                x += sz.Width + 2f * s;
            }
            return x;
        }

        float DrawBalance(Graphics g, float x, float midY, string label, Balance bal, float s, bool dimmed)
        {
            using (Font f = Gfx.FP("Microsoft YaHei UI", 7.6f * s, FontStyle.Regular))
            using (SolidBrush b = new SolidBrush(Color.FromArgb(dimmed ? 170 : 235, Theme.TextDim)))
            using (StringFormat sf = new StringFormat())
            {
                sf.LineAlignment = StringAlignment.Center;
                sf.FormatFlags = StringFormatFlags.NoWrap;
                SizeF sz = g.MeasureString(label, f);
                g.DrawString(label, f, b, new RectangleF(x, midY - 10 * s, sz.Width + 20 * s, 20 * s), sf);
                x += sz.Width + 3.5f * s;
            }

            string v = (bal == null) ? "—" : bal.Text;
            bool okv = bal != null && bal.Present && bal.Ok;
            using (Font f = Gfx.FP("Microsoft YaHei UI", 10.4f * s, FontStyle.Bold))
            using (SolidBrush b = new SolidBrush(okv ? Color.FromArgb(dimmed ? 190 : 255, Theme.TextMain)
                                                      : Color.FromArgb(225, Theme.TextDim)))
            using (StringFormat sf = new StringFormat())
            {
                sf.LineAlignment = StringAlignment.Center;
                sf.FormatFlags = StringFormatFlags.NoWrap;
                SizeF sz = g.MeasureString(v, f);
                g.DrawString(v, f, b, new RectangleF(x, midY - 12 * s, sz.Width + 20 * s, 24 * s), sf);
                x += sz.Width;
            }
            return x;
        }

        float DrawDivider(Graphics g, float x, float midY, float s)
        {
            using (Pen p = new Pen(Color.FromArgb(38, 255, 255, 255), 1f))
                g.DrawLine(p, x + 7 * s, midY - 8 * s, x + 7 * s, midY + 8 * s);
            return x + 14 * s;
        }

        bool CfgWarn(double pct) { return _app.Cfg.WarnHigh && pct >= 90; }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { if (_hotkeyOk) UnregisterHotKey(Handle, HotkeyId); } catch { }
                try { _hover.Stop(); _hover.Dispose(); } catch { }
                try { _measure.Dispose(); } catch { }
                try { _layer.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }
    }


    // ============================ 详情面板 ============================
    internal sealed class DetailForm : Form
    {
        struct Row
        {
            public int K;              // 0 标题 1 分隔 2 额度行 3 键值 4 小字 5 页脚
            public string A, B, C, D;
            public Color? Color;
        }

        readonly AppHost _app;
        float _s = 1f;
        readonly StringFormat _right = new StringFormat();
        readonly StringFormat _near = new StringFormat();

        public DetailForm(AppHost app)
        {
            _app = app;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Color.FromArgb(14, 16, 20);
            DoubleBuffered = true;
            _right.Alignment = StringAlignment.Far;
            _right.LineAlignment = StringAlignment.Near;
            _near.Alignment = StringAlignment.Near;
            _near.LineAlignment = StringAlignment.Near;
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x00000080;      // TOOLWINDOW
                cp.ExStyle |= 0x08000000;      // NOACTIVATE
                return cp;
            }
        }

        float RowH(int k)
        {
            switch (k)
            {
                case 0: return 21f;
                case 1: return 9f;
                case 2: return 17f;
                case 3: return 17f;
                case 4: return 13.4f;
                case 6: return 15f;
                default: return 13f;
            }
        }

        public void Relayout()
        {
            if (_s <= 0) _s = 1f;
            List<Row> rows = BuildRows();
            float h = 12f;
            for (int i = 0; i < rows.Count; i++) h += RowH(rows[i].K);
            h += 11f;
            ClientSize = new Size((int)(Consts.BaseDetailW * _s * 0.85f), (int)(h * _s));   // 宽度收到 85%，字号不变
            UpdateRegion();
        }

        void UpdateRegion()
        {
            GraphicsPath p = Gfx.RoundRect(new RectangleF(0, 0, Width, Height), 12 * _s);
            Region old = Region;
            Region = new Region(p);
            p.Dispose();
            if (old != null) old.Dispose();
        }

        public void SetScale(float scale)
        {
            _s = scale;
        }

        static string ResetText(QuotaWindow w)
        {
            if (!w.Reset.HasValue) return "未知";
            DateTime t = w.Reset.Value;
            TimeSpan d = t - Tz.Now;
            if (d.TotalSeconds <= 0) return "已重置";
            if (d.TotalHours < 24) return t.ToString("HH:mm", CultureInfo.InvariantCulture);
            return t.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
        }

        static string PctText(double? p) { return p.HasValue ? p.Value.ToString("0", CultureInfo.InvariantCulture) + "%" : "—"; }

        static string Num(double? v) { return v.HasValue ? v.Value.ToString("0.00", CultureInfo.InvariantCulture) : "—"; }

        List<Row> BuildRows()
        {
            List<Row> rows = new List<Row>();
            Snapshot s = _app.Last;

            Row t = new Row();
            t.K = 0;
            t.A = "上游额度";
            t.B = _app.StateText(false);
            t.Color = _app.State == NetState.Ok ? (Color?)null : _app.StateColor(_app.State);
            rows.Add(t);
            rows.Add(new Row { K = 1 });

            if (s == null)
            {
                Row hint = new Row();
                hint.K = 4;
                hint.A = _app.State == NetState.Init ? "正在获取数据…" : (_app.StateMessage.Length > 0 ? _app.StateMessage : "暂无数据");
                hint.Color = Theme.Stale;
                rows.Add(hint);
            }
            else
            {
                rows.Add(new Row { K = 6 });
                AddWin(rows, "5 小时", s.Rolling);
                AddWin(rows, "本周", s.Weekly);
                AddWin(rows, "本月", s.Monthly);

                rows.Add(new Row { K = 1 });

                rows.Add(new Row { K = 3, A = "DeepSeek 余额", B = s.DeepSeek.Text });
                if (s.DeepSeek.Present && s.DeepSeek.Ok)
                    rows.Add(new Row
                    {
                        K = 4,
                        A = "充值 " + Num(s.DeepSeek.ToppedUp) + " · 赠送 " + Num(s.DeepSeek.Granted)
                    });
                rows.Add(new Row { K = 3, A = "Kimi 余额", B = s.Kimi.Text });
                if (s.Kimi.Present && s.Kimi.Ok)
                    rows.Add(new Row
                    {
                        K = 4,
                        A = "现金 " + Num(s.Kimi.Cash) + " · 代金券 " + Num(s.Kimi.Voucher)
                    });

                if (s.WH5.HasValue || s.WW7.HasValue || s.WD30.HasValue)
                {
                    rows.Add(new Row { K = 1 });
                    rows.Add(new Row { K = 4, A = "网关估算 · 美元 · 仅覆盖本站流量" });
                    rows.Add(new Row
                    {
                        K = 4,
                        A = "5h " + Num(s.WH5) + " · 7d " + Num(s.WW7) + " · 30d " + Num(s.WD30)
                    });
                    if (s.RH5.HasValue || s.RW7.HasValue)
                        rows.Add(new Row
                        {
                            K = 4,
                            A = "请求 5h " + Int(s.RH5) + " · 7d " + Int(s.RW7) + " · 30d " + Int(s.RD30)
                        });
                }

                if (s.Models.Count > 0)
                {
                    int n = Math.Min(2, s.Models.Count);
                    for (int i = 0; i < n; i++)
                    {
                        ModelRow m = s.Models[i];
                        string line = m.Model;
                        if (m.D30.HasValue)
                            line += "   30d " + m.D30.Value.ToString("0.00", CultureInfo.InvariantCulture);
                        if (m.Cap.HasValue)
                            line += " / " + m.Cap.Value.ToString("0", CultureInfo.InvariantCulture) + " 额度";
                        if (!m.Priced) line += "  未定价";
                        rows.Add(new Row { K = 4, A = line });
                    }
                }

                if (_app.State != NetState.Ok && _app.StateMessage.Length > 0)
                {
                    Row warn = new Row();
                    warn.K = 4;
                    warn.A = "状态：" + _app.StateMessage;
                    warn.Color = _app.StateColor(_app.State);
                    rows.Add(warn);
                }
                else if (!s.QuotaOk)
                {
                    Row warn = new Row();
                    warn.K = 4;
                    warn.A = "套餐额度查询异常（quota.ok = false）";
                    warn.Color = Theme.Stale;
                    rows.Add(warn);
                }
            }

            rows.Add(new Row { K = 1 });

            Row foot = new Row();
            foot.K = 5;
            string upd = s != null ? s.FetchedAt.ToString("HH:mm:ss", CultureInfo.InvariantCulture) : "--:--";
            foot.A = "数据 " + upd + " · 每 " + (_app.Cfg.IntervalSec / 60) + " 分钟轮询 · 只读";
            rows.Add(foot);

            return rows;
        }

        static string Int(int? v)
        {
            return v.HasValue ? v.Value.ToString("N0", CultureInfo.InvariantCulture) : "—";
        }

        void AddWin(List<Row> rows, string label, QuotaWindow w)
        {
            Row r = new Row();
            r.K = 2;
            r.A = label;
            if (w == null || !w.Present)
            {
                r.B = "无数据";
                r.C = "";
                r.D = "";
                r.Color = Theme.TextDim;
            }
            else if (!w.Good)
            {
                r.B = "状态 " + (w.Status.Length > 0 ? w.Status : "异常");
                r.C = "";
                r.D = "";
                r.Color = Theme.Stale;
            }
            else
            {
                r.B = PctText(w.Percent);
                r.C = PctText(w.Remain);
                r.D = ResetText(w);
                if (_app.Cfg.WarnHigh && w.Percent.HasValue && w.Percent.Value >= 90) r.Color = Theme.Warn;
            }
            rows.Add(r);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            Theme th = _app.The;
            float s = _s;

            RectangleF body = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
            using (GraphicsPath p = Gfx.RoundRect(body, 12 * s))
            {
                // 不透明底：详情面板优先保证可读性，桌面内容不透过来
                using (LinearGradientBrush br = new LinearGradientBrush(body,
                    ColorUtil.Mix(Color.FromArgb(30, 34, 42), th.Accent, 0.14),
                    Color.FromArgb(15, 17, 22), 90f))
                    g.FillPath(br, p);
                using (Pen pen = new Pen(ColorUtil.A(th.Accent, 120), 1f))
                    g.DrawPath(pen, p);
            }

            List<Row> rows = BuildRows();
            float padX = 12 * s;
            float y = 12 * s;
            float lineRight = Width - padX;

            for (int i = 0; i < rows.Count; i++)
            {
                Row r = rows[i];
                float rh = RowH(r.K) * s;
                float textY = y;

                switch (r.K)
                {
                    case 0:
                        using (Font f = Gfx.FP("Microsoft YaHei UI", 9.6f * s, FontStyle.Bold))
                        using (SolidBrush b = new SolidBrush(th.Accent))
                            g.DrawString(r.A, f, b, padX, textY + 1 * s, _near);
                        using (Font f = Gfx.FP("Microsoft YaHei UI", 7.4f * s, FontStyle.Regular))
                        using (SolidBrush b = new SolidBrush(r.Color.HasValue ? r.Color.Value : ColorUtil.A(Theme.TextDim, 238)))
                        using (StringFormat sf = new StringFormat())
                        {
                            sf.Alignment = StringAlignment.Far;
                            sf.LineAlignment = StringAlignment.Near;
                            g.DrawString(r.B, f, b, new RectangleF(padX, textY + 2.5f * s, lineRight - padX, 14 * s), sf);
                        }
                        break;

                    case 1:
                        using (Pen pen = new Pen(Color.FromArgb(26, 255, 255, 255), 1f))
                            g.DrawLine(pen, padX, y + rh / 2f, lineRight, y + rh / 2f);
                        break;

                    case 2:
                        using (Font f = Gfx.FP("Microsoft YaHei UI", 7.8f * s, FontStyle.Regular))
                        using (SolidBrush b = new SolidBrush(ColorUtil.A(Theme.TextDim, 240)))
                            g.DrawString(r.A, f, b, padX, textY + 1 * s, _near);
                        using (Font fb = Gfx.FP("Microsoft YaHei UI", 8.6f * s, FontStyle.Bold))
                        using (SolidBrush bb = new SolidBrush(r.Color.HasValue ? r.Color.Value : ColorUtil.A(th.Accent, 240)))
                            g.DrawString(r.B, fb, bb, padX + 50 * s, textY + 0.6f * s, _near);
                        if (r.C.Length > 0)
                        {
                            using (Font f = Gfx.FP("Microsoft YaHei UI", 8.6f * s, FontStyle.Bold))
                            using (SolidBrush b = new SolidBrush(ColorUtil.A(Theme.TextMain, 252)))
                                g.DrawString(r.C, f, b, padX + 118 * s, textY + 0.6f * s, _near);
                        }
                        if (r.D.Length > 0)
                        {
                            using (Font f = Gfx.FP("Microsoft YaHei UI", 8.2f * s, FontStyle.Regular))
                            using (SolidBrush b = new SolidBrush(ColorUtil.A(Theme.TextDim, 238)))
                            using (StringFormat sf = new StringFormat())
                            {
                                sf.Alignment = StringAlignment.Far;
                                sf.LineAlignment = StringAlignment.Near;
                                g.DrawString(r.D, f, b, new RectangleF(padX + 176 * s, textY + 0.8f * s, lineRight - padX - 176 * s, 14 * s), sf);
                            }
                        }
                        break;

                    case 6:
                        using (Font f = Gfx.FP("Microsoft YaHei UI", 7.0f * s, FontStyle.Regular))
                        using (SolidBrush b = new SolidBrush(ColorUtil.A(Theme.TextDim, 210)))
                        {
                            g.DrawString("已用", f, b, padX + 50 * s, textY + 2 * s, _near);
                            g.DrawString("剩余", f, b, padX + 118 * s, textY + 2 * s, _near);
                            using (StringFormat sf = new StringFormat())
                            {
                                sf.Alignment = StringAlignment.Far;
                                sf.LineAlignment = StringAlignment.Near;
                                g.DrawString("重置", f, b, new RectangleF(padX + 176 * s, textY + 2 * s, lineRight - padX - 176 * s, 15 * s), sf);
                            }
                        }
                        break;

                    case 3:
                        using (Font f = Gfx.FP("Microsoft YaHei UI", 7.8f * s, FontStyle.Regular))
                        using (SolidBrush b = new SolidBrush(ColorUtil.A(Theme.TextDim, 240)))
                            g.DrawString(r.A, f, b, padX, textY + 1 * s, _near);
                        using (Font f2 = Gfx.FP("Microsoft YaHei UI", 9.4f * s, FontStyle.Bold))
                        using (SolidBrush b2 = new SolidBrush(Theme.TextMain))
                        using (StringFormat sf2 = new StringFormat())
                        {
                            sf2.Alignment = StringAlignment.Far;
                            sf2.LineAlignment = StringAlignment.Near;
                            g.DrawString(r.B, f2, b2, new RectangleF(padX, textY - 0.5f * s, lineRight - padX, 15 * s), sf2);
                        }
                        break;

                    case 4:
                        using (Font f = Gfx.FP("Microsoft YaHei UI", 7.3f * s, FontStyle.Regular))
                        using (SolidBrush b = new SolidBrush(r.Color.HasValue ? r.Color.Value : ColorUtil.A(Theme.TextDim, 210)))
                        {
                            string txt = Gfx.Ellipsis(g, r.A ?? "", f, lineRight - padX);
                            g.DrawString(txt, f, b, padX, textY, _near);
                        }
                        break;

                    default:
                        using (Font f = Gfx.FP("Microsoft YaHei UI", 7.0f * s, FontStyle.Regular))
                        using (SolidBrush b = new SolidBrush(ColorUtil.A(Theme.TextDim, 185)))
                            g.DrawString(r.A, f, b, padX, textY + 1 * s, _near);
                        break;
                }
                y += rh;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _right.Dispose(); _near.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }
    }

    // ============================ 色调微调 ============================
    internal sealed class ToneDialog : Form
    {
        readonly TrackBar _hue = new TrackBar();
        readonly TrackBar _sat = new TrackBar();
        readonly Panel _swatch = new Panel();
        readonly Action<int, int> _live;

        public ToneDialog(int hue, int sat, Action<int, int> live)
        {
            _live = live;
            Text = "色调微调";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(348, 200);
            Font = new Font("Microsoft YaHei UI", 9f);

            Label l1 = new Label();
            l1.Text = "色相";
            l1.SetBounds(16, 16, 40, 20);
            Controls.Add(l1);

            _hue.Minimum = 0;
            _hue.Maximum = 359;
            _hue.TickFrequency = 30;
            _hue.Value = Math.Max(0, Math.Min(359, hue));
            _hue.SetBounds(58, 8, 274, 45);
            _hue.ValueChanged += OnValue;
            Controls.Add(_hue);

            Label l2 = new Label();
            l2.Text = "饱和";
            l2.SetBounds(16, 62, 40, 20);
            Controls.Add(l2);

            _sat.Minimum = 0;
            _sat.Maximum = 100;
            _sat.TickFrequency = 10;
            _sat.Value = Math.Max(0, Math.Min(100, sat));
            _sat.SetBounds(58, 54, 274, 45);
            _sat.ValueChanged += OnValue;
            Controls.Add(_sat);

            _swatch.SetBounds(16, 108, 78, 40);
            _swatch.BorderStyle = BorderStyle.FixedSingle;
            Controls.Add(_swatch);

            Label l3 = new Label();
            l3.Text = "拖动滑块实时预览，确定后保存。";
            l3.SetBounds(104, 118, 230, 20);
            l3.ForeColor = Color.FromArgb(90, 90, 90);
            Controls.Add(l3);

            Button ok = new Button();
            ok.Text = "确定";
            ok.DialogResult = DialogResult.OK;
            ok.SetBounds(182, 158, 76, 28);
            Controls.Add(ok);

            Button cancel = new Button();
            cancel.Text = "取消";
            cancel.DialogResult = DialogResult.Cancel;
            cancel.SetBounds(264, 158, 76, 28);
            Controls.Add(cancel);

            AcceptButton = ok;
            CancelButton = cancel;
            UpdateSwatch();
        }

        void OnValue(object sender, EventArgs e)
        {
            UpdateSwatch();
            if (_live != null) _live(_hue.Value, _sat.Value);
        }

        void UpdateSwatch()
        {
            _swatch.BackColor = ColorUtil.Hsl(_hue.Value, _sat.Value / 100.0, 0.60);
        }
    }

    // ============================ 设置钥匙 ============================
    internal sealed class KeyDialog : Form
    {
        readonly TextBox _box = new TextBox();
        readonly TextBox _url = new TextBox();

        public string Value { get { return _box.Text.Trim(); } }
        public string Endpoint { get { return _url.Text.Trim(); } }

        public KeyDialog(string currentKey, string currentEndpoint, bool firstRun)
        {
            Text = firstRun ? "首次运行 · 连接设置" : "连接设置";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(520, 226);
            Font = new Font("Microsoft YaHei UI", 9f);

            Label tip = new Label();
            tip.Text = "网关地址：填根地址即可，程序会自动补 /api/upstream/quota。\r\n" +
                       "只读钥匙：粘贴网关的只读 key；这把钥匙只能读额度，无法消费。";
            tip.SetBounds(16, 12, 488, 40);
            Controls.Add(tip);

            Label l1 = new Label();
            l1.Text = "网关地址";
            l1.SetBounds(16, 56, 80, 18);
            Controls.Add(l1);
            _url.SetBounds(100, 53, 404, 24);
            _url.Text = currentEndpoint ?? "";
            Controls.Add(_url);

            Label l2 = new Label();
            l2.Text = "只读钥匙";
            l2.SetBounds(16, 90, 80, 18);
            Controls.Add(l2);
            _box.SetBounds(100, 87, 404, 24);
            _box.Text = currentKey ?? "";
            Controls.Add(_box);

            Label where = new Label();
            where.Text = "保存位置：" + AppPaths.Dir;
            where.SetBounds(16, 120, 488, 18);
            where.ForeColor = Color.FromArgb(110, 110, 110);
            Controls.Add(where);

            Button ok = new Button();
            ok.Text = "保存";
            ok.DialogResult = DialogResult.OK;
            ok.SetBounds(338, 152, 76, 28);
            Controls.Add(ok);

            Button cancel = new Button();
            cancel.Text = "取消";
            cancel.DialogResult = DialogResult.Cancel;
            cancel.SetBounds(424, 152, 76, 28);
            Controls.Add(cancel);

            AcceptButton = ok;
            CancelButton = cancel;
        }
    }

    // ============================ 应用主体 ============================
    internal sealed class AppHost : ApplicationContext
    {
        public readonly Config Cfg;
        public readonly Theme The;

        WidgetForm _widget;
        DetailForm _detail;
        NotifyIcon _tray;
        Poller _poller;
        WinTimer _ui;
        WinTimer _top;
        int _menuDepth;
        DateTime _trayRetryAt = DateTime.MinValue;
        WinTimer _boot;
        Icon _trayIcon;
        bool _detailVisible;

        public Snapshot Last;
        public NetState State = NetState.Init;
        public string StateMessage = "正在连接";
        public DateTime? LastOkAt;

        public AppHost(Config cfg)
        {
            Cfg = cfg;
            The = new Theme(cfg.Hue, cfg.Sat);

            _widget = new WidgetForm(this);
            _widget.Show();

            _detail = new DetailForm(this);
            _detail.SetScale(_widget.UiScale * 1.35f);
            _detail.Opacity = 1.0;   // 详情面板保持不透明，优先可读性
            _detail.ContextMenuStrip = BuildMenu();

            _widget.ContextMenuStrip = BuildMenu();

            CreateTray();
            StartPolling();

            _ui = new WinTimer();
            _ui.Interval = 30000;
            _ui.Tick += UiTick;
            _ui.Start();

            _boot = new WinTimer();
            _boot.Interval = 5000;
            _boot.Tick += BootTick;
            _boot.Start();

            // 定时把自己重新插回顶层：TopMost 会被别的顶层窗口挤到下面去
            _top = new WinTimer();
            _top.Interval = 1500;
            _top.Tick += delegate { AssertTopMost(); };
            _top.Start();
            AssertTopMost();

            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
            Cfg.Save();
        }

        void OnProcessExit(object sender, EventArgs e)
        {
            try { Cfg.Save(); } catch { }
        }

        void BootTick(object sender, EventArgs e)
        {
            _boot.Stop();
            _boot.Dispose();
            if (string.IsNullOrEmpty(AppPaths.ReadKey()) || string.IsNullOrEmpty(Cfg.Endpoint))
            {
                if (!PromptForKey(true) && _tray != null)
                {
                    try
                    {
                        _tray.ShowBalloonTip(8000, "尚未完成连接设置",
                            "右键悬浮球或按 Ctrl+Alt+Q → 连接设置，填网关地址与只读钥匙。", ToolTipIcon.Info);
                    }
                    catch { }
                }
            }
            ReRegisterTray();
            if (Cfg.Preview) ShowDetail();
            if (_poller != null && Last == null) _poller.RefreshNow();
        }

        void UiTick(object sender, EventArgs e)
        {
            // 通知区可能被系统静默拒绝；每隔一分钟再登记一次，能恢复就恢复
            if (_tray != null && DateTime.Now > _trayRetryAt)
            {
                _trayRetryAt = DateTime.Now.AddMinutes(1);
                try
                {
                    _tray.Visible = false;
                    _tray.Visible = true;
                }
                catch { }
            }
            if (_widget != null && !_widget.IsDisposed && !_widget.TopMost) _widget.TopMost = true;
            if (_detailVisible && _detail != null && !_detail.IsDisposed)
            {
                _detail.Invalidate();
            }
        }

        // 把两个窗口重新顶到最前，压住其它 TopMost 窗口
        [DllImport("user32.dll")]
        static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint flags);

        static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        const uint SWP_NOSIZE = 0x0001;
        const uint SWP_NOMOVE = 0x0002;
        const uint SWP_NOACTIVATE = 0x0010;

        void AssertTopMost()
        {
            try
            {
                if (_widget != null && !_widget.IsDisposed && _widget.Visible && _widget.IsHandleCreated)
                    SetWindowPos(_widget.Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                if (DetailVisible && _detail.IsHandleCreated)
                    SetWindowPos(_detail.Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            }
            catch { }
        }

        // 打开目录：交给外壳执行，避免在受限令牌下再起 explorer.exe（0xc0000142）
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr ShellExecuteW(IntPtr hwnd, string lpOperation, string lpFile, string lpParameters, string lpDirectory, int nShowCmd);

        public void OpenFolder(string dir)
        {
            try
            {
                IntPtr r = ShellExecuteW(IntPtr.Zero, "open", dir, null, null, 5 /*SW_SHOW*/);
                if (r.ToInt64() > 32) return;
            }
            catch { }
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = dir;
                psi.UseShellExecute = true;
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                MessageBox.Show("打开目录失败：" + ex.Message + "\r\n\r\n路径：" + dir, "GWQuota",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        // ---------- 状态文字 ----------
        public string StateText(bool shortForm)
        {
            switch (State)
            {
                case NetState.Ok:
                    if (!shortForm) return "在线 · " + (Last != null ? Last.FetchedAt.ToString("HH:mm:ss", CultureInfo.InvariantCulture) : "");
                    return Last != null ? Last.FetchedAt.ToString("HH:mm", CultureInfo.InvariantCulture) + " 更新" : "在线";
                case NetState.Init: return "正在连接";
                case NetState.NoKey: return "未设置钥匙";
                case NetState.Network: return "离线";
                case NetState.Unauthorized: return "钥匙失效";
                case NetState.Forbidden: return "接口拒绝";
                case NetState.RateLimited: return "限流退避";
                case NetState.GatewayOff: return "网关已关闭";
                default: return "接口错误";
            }
        }

        public Color StateColor(NetState st)
        {
            switch (st)
            {
                case NetState.Ok: return The.Accent;
                case NetState.Unauthorized:
                case NetState.Forbidden:
                case NetState.NoKey:
                    return Theme.Warn;
                case NetState.Network:
                case NetState.RateLimited:
                case NetState.GatewayOff:
                case NetState.HttpError:
                    return Theme.Stale;
                default: return Theme.TextDim;
            }
        }

        // ---------- 详情面板 ----------
        public bool DetailVisible { get { return _detailVisible && _detail != null && _detail.Visible; } }
        public Rectangle DetailBounds
        {
            get
            {
                if (!DetailVisible) return Rectangle.Empty;
                return _detail.Bounds;
            }
        }

        public void ShowDetail()
        {
            if (_detailVisible) return;
            try
            {
                _detail.SetScale(_widget.UiScale * 1.35f);
                _detail.Relayout();
                PositionDetail();
                _detailVisible = true;
                _detail.Show();
                _detail.TopMost = true;
                _detail.Invalidate();
            }
            catch (Exception ex)
            {
                AppPaths.Log("showdetail: " + ex.Message);
                _detailVisible = false;
            }
        }

        void PositionDetail()
        {
            Rectangle wb = _widget.VisibleRect;
            Screen sc = Screen.FromRectangle(wb);
            Rectangle wa = sc.WorkingArea;
            int gap = (int)(10 * _widget.UiScale);
            int x = wb.Right + gap;
            if (x + _detail.Width > wa.Right) x = wb.Left - gap - _detail.Width;
            if (x < wa.Left) x = Math.Max(wa.Left + 2, wb.Left + (wb.Width - _detail.Width) / 2);
            int y = wb.Top + (wb.Height - _detail.Height) / 2;
            y = Math.Max(wa.Top + 2, Math.Min(y, wa.Bottom - _detail.Height - 2));
            _detail.Location = new Point(x, y);
        }

        public void HideDetail()
        {
            if (Cfg.Preview) return;      // 常显模式下不自动收起
            if (!_detailVisible) return;
            _detailVisible = false;
            try { _detail.Hide(); }
            catch { }
        }

        public void Repaint()
        {
            try { if (_widget != null && !_widget.IsDisposed) _widget.Render(); } catch { }
            try { if (_detailVisible && _detail != null && !_detail.IsDisposed) _detail.Invalidate(); } catch { }
        }

        public void RefreshNow()
        {
            if (_poller != null) _poller.RefreshNow();
        }

        // ---------- 轮询 ----------
        void StartPolling()
        {
            _poller = new Poller(AppPaths.ReadKey, delegate { return Cfg.Endpoint; }, Cfg.IntervalSec);
            _poller.Completed += OnOutcome;
            _poller.Start();
        }

        void OnOutcome(Outcome o)
        {
            if (_widget == null || _widget.IsDisposed) return;
            try { _widget.BeginInvoke((MethodInvoker)(delegate { ApplyOutcome(o); })); }
            catch (Exception ex) { AppPaths.Log("begininvoke: " + ex.Message); }
        }

        void ApplyOutcome(Outcome o)
        {
            try
            {
                if (o.Snap != null)
                {
                    Last = o.Snap;
                    LastOkAt = DateTime.Now;
                }
                State = o.State;
                StateMessage = o.Message ?? "";
                UpdateTray();
                Repaint();
                if (_detailVisible)
                {
                    _detail.Relayout();
                    PositionDetail();
                    _detail.Invalidate();
                }
            }
            catch (Exception ex)
            {
                AppPaths.Log("apply: " + ex.Message);
            }
        }

        // ---------- 托盘 ----------
        void CreateTray()
        {
            _tray = new NotifyIcon();
            _tray.ContextMenuStrip = BuildMenu();
            _tray.DoubleClick += delegate { RefreshNow(); };
            _tray.Text = "GWQuota 上游额度";

            // 顺序：先有图标与提示文字，最后才 Visible —— 否则可能注册一条空条目
            UpdateTray();
            _tray.Visible = true;
            AppPaths.Log("tray: visible=" + _tray.Visible + " icon=" + (_tray.Icon != null)
                + " size=" + (_tray.Icon != null ? _tray.Icon.Width + "x" + _tray.Icon.Height : "-"));
        }

        /// 重新登记一次通知区图标（资源管理器第一次可能来不及，重登记可救回）
        void ReRegisterTray()
        {
            if (_tray == null) return;
            try
            {
                _tray.Visible = false;
                _tray.Visible = true;
                AppPaths.Log("tray: re-registered visible=" + _tray.Visible);
            }
            catch (Exception ex) { AppPaths.Log("tray re-reg: " + ex.Message); }
        }

        void UpdateTray()
        {
            if (_tray == null) return;
            try
            {
                bool alert = State != NetState.Ok && State != NetState.Init;
                Color ring = State == NetState.Ok ? The.Accent : StateColor(State);
                Icon ic = null;
                // 优先用 exe 内嵌图标文件转出来的 HICON（最稳），失败再自绘，最后兜系统图标
                try { ic = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
                catch (Exception ex) { AppPaths.Log("trayicon(extract): " + ex.Message); }
                if (ic == null)
                {
                    try { ic = Gfx.TrayIcon(The.Accent, ring, alert); }
                    catch (Exception ex) { AppPaths.Log("trayicon: " + ex.Message); }
                }
                if (ic == null) ic = SystemIcons.Application;
                AppPaths.Log("trayicon: handle=" + ic.Handle.ToInt64().ToString() + " " + ic.Width + "x" + ic.Height);

                Icon old = _trayIcon;
                _trayIcon = ic;
                _tray.Icon = ic;
                if (old != null && !object.ReferenceEquals(old, ic)) old.Dispose();

                string txt = BuildTrayText();
                if (txt.Length > 63) txt = txt.Substring(0, 62) + "…";
                _tray.Text = txt;
            }
            catch (Exception ex)
            {
                AppPaths.Log("tray: " + ex.Message);
            }
        }

        string BuildTrayText()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("上游额度 · ");
            if (Last != null && Last.Monthly.Remain.HasValue)
            {
                sb.Append("本月剩 ").Append(Last.Monthly.Remain.Value.ToString("0", CultureInfo.InvariantCulture)).Append("%");
                if (Last.Rolling.Remain.HasValue)
                    sb.Append(" · 5h 剩 ").Append(Last.Rolling.Remain.Value.ToString("0", CultureInfo.InvariantCulture)).Append("%");
                if (Last.Weekly.Remain.HasValue)
                    sb.Append(" · 周剩 ").Append(Last.Weekly.Remain.Value.ToString("0", CultureInfo.InvariantCulture)).Append("%");
            }
            else
            {
                sb.Append(StateText(false));
            }
            if (Last != null && Last.DeepSeek.Present)
                sb.Append(" | DS ").Append(Last.DeepSeek.Text);
            if (Last != null && Last.Kimi.Present)
                sb.Append(" | Kimi ").Append(Last.Kimi.Text);
            if (State != NetState.Ok) sb.Append(" | ").Append(StateText(false));
            return sb.ToString();
        }

        // ---------- 菜单 ----------
        ContextMenuStrip BuildMenu()
        {
            ContextMenuStrip m = new ContextMenuStrip();
            m.ShowImageMargin = true;

            ToolStripMenuItem miForm = new ToolStripMenuItem("界面外形");
            ToolStripMenuItem fBall = new ToolStripMenuItem("球形悬浮球");
            fBall.Tag = "form:ball";
            fBall.Click += delegate { SetForm("ball"); };
            ToolStripMenuItem fBar = new ToolStripMenuItem("条形配额条");
            fBar.Tag = "form:bar";
            fBar.Click += delegate { SetForm("bar"); };
            miForm.DropDownItems.Add(fBall);
            miForm.DropDownItems.Add(fBar);

            ToolStripMenuItem miTone = new ToolStripMenuItem("色调");
            for (int i = 0; i < Palettes.All.Count; i++)
            {
                Palette p = Palettes.All[i];
                ToolStripMenuItem it = new ToolStripMenuItem(p.Name + "色", Gfx.Swatch(Palettes.ColorOf(p)));
                it.Tag = "pal:" + p.Id;
                string id = p.Id;
                it.Click += delegate { SetPalette(id); };
                miTone.DropDownItems.Add(it);
            }
            miTone.DropDownItems.Add(new ToolStripSeparator());
            ToolStripMenuItem miToneDlg = new ToolStripMenuItem("色相微调…");
            miToneDlg.Click += delegate { ShowToneDialog(); };
            miTone.DropDownItems.Add(miToneDlg);

            ToolStripMenuItem miOp = new ToolStripMenuItem("透明度");
            int[] ops = new int[] { 60, 75, 88, 95 };
            for (int i = 0; i < ops.Length; i++)
            {
                int v = ops[i];
                ToolStripMenuItem it = new ToolStripMenuItem(v + "%");
                it.Tag = "op:" + v;
                it.Click += delegate { SetOpacity(v); };
                miOp.DropDownItems.Add(it);
            }

            ToolStripMenuItem warn = new ToolStripMenuItem("额度告急变色（≥90%）");
            warn.Tag = "warn";
            warn.Click += delegate
            {
                Cfg.WarnHigh = !Cfg.WarnHigh;
                Cfg.Save();
                Repaint();
            };

            ToolStripMenuItem refresh = new ToolStripMenuItem("立即刷新");
            refresh.Click += delegate { RefreshNow(); };

            ToolStripMenuItem setKey = new ToolStripMenuItem("连接设置（网关地址与钥匙）…");
            setKey.Click += delegate { PromptForKey(false); };

            ToolStripMenuItem reset = new ToolStripMenuItem("回归默认位置（主屏中央）");
            reset.Click += delegate { ResetPosition(); };

            ToolStripMenuItem about = new ToolStripMenuItem("关于 GWQuota");
            about.Click += delegate { ShowAbout(); };

            ToolStripMenuItem exit = new ToolStripMenuItem("退出");
            exit.Click += delegate { ExitApp(); };

            m.Items.Add(miForm);
            m.Items.Add(miTone);
            m.Items.Add(miOp);
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(warn);
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(refresh);
            m.Items.Add(setKey);
            m.Items.Add(reset);
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(about);
            m.Items.Add(exit);

            m.Opening += delegate
            {
                SyncMenu(m);
                _menuDepth++;
                if (_widget != null) _widget.SetMenuOpen(true);
                HideDetailForce();          // 先把详情面板收掉，别挡住菜单
            };
            m.Opened += delegate
            {
                // 让菜单本身也进顶层带：否则会被置顶的悬浮窗/详情面板压住
                try
                {
                    if (m.IsHandleCreated)
                        SetWindowPos(m.Handle, HWND_TOPMOST, 0, 0, 0, 0,
                            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                }
                catch { }
            };
            m.Closed += delegate
            {
                if (_menuDepth > 0) _menuDepth--;
                if (_widget != null) _widget.SetMenuOpen(false);
                // 菜单关掉后把悬浮窗重新顶回去
                AssertTopMost();
            };
            return m;
        }

        /// 强制收起详情面板（忽略 preview 常显设置）
        /// 在鼠标位置弹出完整菜单（热键 / 备用入口用）
        public void ShowMenuAtCursor()
        {
            try
            {
                HideDetailForce();
                ContextMenuStrip m = _widget.ContextMenuStrip;
                if (m == null) return;
                _menuDepth++;
                if (_widget != null) _widget.SetMenuOpen(true);
                m.Show(Cursor.Position);
            }
            catch (Exception ex)
            {
                AppPaths.Log("showmenu: " + ex.Message);
            }
        }
        public void HideDetailForce()
        {
            if (!_detailVisible) return;
            _detailVisible = false;
            try { _detail.Hide(); } catch { }
        }

        void SyncMenu(ToolStripMenuItem owner)
        {
            for (int i = 0; i < owner.DropDownItems.Count; i++)
            {
                ToolStripItem it = owner.DropDownItems[i];
                ToolStripMenuItem mi = it as ToolStripMenuItem;
                if (mi == null) continue;
                if (mi.Tag != null)
                {
                    string tag = mi.Tag as string;
                    if (tag != null)
                    {
                        if (tag.StartsWith("form:", StringComparison.Ordinal))
                            mi.Checked = (tag == "form:" + Cfg.Form);
                        else if (tag.StartsWith("pal:", StringComparison.Ordinal))
                            mi.Checked = (tag == "pal:" + Cfg.PaletteId);
                        else if (tag.StartsWith("op:", StringComparison.Ordinal))
                            mi.Checked = (tag == "op:" + Cfg.OpacityPct);
                        else if (tag == "warn")
                            mi.Checked = Cfg.WarnHigh;
                        else if (tag == "autostart")
                            mi.Checked = AutoStartOn();
                    }
                }
                if (mi.DropDownItems.Count > 0) SyncMenu(mi);
            }
        }

        void SyncMenu(ContextMenuStrip m)
        {
            for (int i = 0; i < m.Items.Count; i++)
            {
                ToolStripMenuItem mi = m.Items[i] as ToolStripMenuItem;
                if (mi == null) continue;
                SyncMenu(mi);
            }
        }

        // ---------- 设置动作 ----------
        void SetForm(string name)
        {
            HideDetail();
            _widget.ApplyForm(name, false);
            _detail.SetScale(_widget.UiScale * 1.35f);
        }

        void SetPalette(string id)
        {
            Palette p = Palettes.ById(id);
            if (p == null) return;
            The.Set(p.Hue, p.Sat);
            Cfg.Hue = p.Hue;
            Cfg.Sat = p.Sat;
            Cfg.PaletteId = p.Id;
            Cfg.Save();
            UpdateTray();
            Repaint();
        }

        void SetOpacity(int pct)
        {
            Cfg.OpacityPct = pct;
            Cfg.Save();
            _detail.Opacity = 1.0;   // 详情面板保持不透明，优先可读性
            Repaint();
        }

        void ShowToneDialog()
        {
            int oh = Cfg.Hue;
            int os = Cfg.Sat;
            HideDetail();
            using (ToneDialog dlg = new ToneDialog(Cfg.Hue, Cfg.Sat, delegate (int h, int s)
            {
                The.Set(h, s);
                Repaint();
            }))
            {
                DialogResult r = dlg.ShowDialog();
                if (r != DialogResult.OK)
                {
                    The.Set(oh, os);
                }
                else
                {
                    Cfg.Hue = The.Hue;
                    Cfg.Sat = The.Sat;
                    Cfg.PaletteId = MatchPalette(The.Hue, The.Sat);
                    Cfg.Save();
                    UpdateTray();
                }
                Repaint();
            }
        }

        static string MatchPalette(int hue, int sat)
        {
            for (int i = 0; i < Palettes.All.Count; i++)
            {
                Palette p = Palettes.All[i];
                if (p.Hue == hue && p.Sat == sat) return p.Id;
            }
            return "";
        }

        void ResetPosition()
        {
            HideDetail();
            Size sz = _widget.Size;
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            Point p = new Point(wa.Left + (wa.Width - sz.Width) / 2, wa.Top + (wa.Height - sz.Height) / 2);
            _widget.Location = p;
            Cfg.X = p.X;
            Cfg.Y = p.Y;
            Cfg.Save();
            _widget.Render();
        }

        // 地址归一化：允许只填根地址（自动补 /api/upstream/quota），去掉尾部斜杠，只接受 http(s)
        static string NormalizeEndpoint(string raw)
        {
            string v = (raw ?? "").Trim();
            if (v.Length == 0) return "";
            if (v.IndexOf("://", StringComparison.Ordinal) < 0) v = "https://" + v;
            Uri u;
            if (!Uri.TryCreate(v, UriKind.Absolute, out u)) return "";
            if (u.Scheme != "http" && u.Scheme != "https") return "";
            string abs = u.GetLeftPart(UriPartial.Path).TrimEnd('/');
            if (!abs.EndsWith(Consts.DefaultPath, StringComparison.OrdinalIgnoreCase)) abs += Consts.DefaultPath;
            return abs;
        }

        bool PromptForKey(bool firstRun)
        {
            HideDetail();
            using (KeyDialog dlg = new KeyDialog(AppPaths.ReadKey(), Cfg.Endpoint, firstRun))
            {
                if (dlg.ShowDialog() == DialogResult.OK)
                {
                    string ep = NormalizeEndpoint(dlg.Endpoint);
                    if (ep.Length == 0)
                    {
                        MessageBox.Show("网关地址无效。请填 https://你的网关域名，例如 https://gateway.example.com。",
                            "GWQuota", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return false;
                    }
                    Cfg.Endpoint = ep;
                    Cfg.Save();
                    AppPaths.WriteKey(dlg.Value);
                    RefreshNow();
                    return true;
                }
            }
            return false;
        }

        // ---------- 开机自启 ----------
        const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

        static bool AutoStartOn()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKeyPath))
                {
                    if (k == null) return false;
                    string v = k.GetValue(Consts.RunValue) as string;
                    if (string.IsNullOrEmpty(v)) return false;
                    return v.IndexOf(Application.ExecutablePath, StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch { return false; }
        }

        static void SetAutoStart(bool on)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKeyPath))
                {
                    if (k == null) return;
                    if (on) k.SetValue(Consts.RunValue, "\"" + Application.ExecutablePath + "\"");
                    else k.DeleteValue(Consts.RunValue, false);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("写入开机自启失败：" + ex.Message, "GWQuota",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        void ToggleAutoStart()
        {
            bool on = !AutoStartOn();
            SetAutoStart(on);
            if (on && !AutoStartOn())
                MessageBox.Show("开机自启设置未生效，可能被安全软件拦截。", "GWQuota",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        void ShowAbout()
        {
            Snapshot s = Last;
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("GWQuota " + Consts.Version + " · 上游额度悬浮窗");
            sb.AppendLine("只读接口：" + (string.IsNullOrEmpty(Cfg.Endpoint) ? "未设置" : Cfg.Endpoint));
            sb.AppendLine("轮询间隔：" + (Cfg.IntervalSec / 60) + " 分钟（服务端缓存 60 秒）");
            sb.AppendLine("钥匙文件：" + AppPaths.KeyFile);
            sb.AppendLine("配置文件：" + AppPaths.IniFile);
            if (s != null)
                sb.AppendLine("最近数据：" + s.FetchedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            MessageBox.Show(sb.ToString(), "关于 GWQuota", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // ---------- 退出 ----------
        public void ExitApp()
        {
            try { if (_ui != null) { _ui.Stop(); _ui.Dispose(); } } catch { }
            try { if (_boot != null) { _boot.Stop(); _boot.Dispose(); } } catch { }
            try { if (_top != null) { _top.Stop(); _top.Dispose(); } } catch { }
            try { if (_poller != null) _poller.Dispose(); } catch { }
            try { Cfg.Save(); } catch { }
            try
            {
                if (_tray != null)
                {
                    _tray.Visible = false;
                    _tray.Dispose();
                    _tray = null;
                }
            }
            catch { }
            try { if (_trayIcon != null) { _trayIcon.Dispose(); _trayIcon = null; } } catch { }
            try { HideDetail(); } catch { }
            try { if (_widget != null) _widget.Hide(); } catch { }
            ExitThread();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { if (_detail != null) _detail.Dispose(); } catch { }
                try { if (_widget != null) _widget.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }
    }

    // ============================ 入口 ============================
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            bool created;
            using (Mutex mutex = new Mutex(true, Consts.MutexName, out created))
            {
                if (!created) return;
                try { Native.SetProcessDPIAware(); }
                catch { }
                AppPaths.Ensure();
                try
                {
                    WindowsIdentity id = WindowsIdentity.GetCurrent();
                    string extra = "";
                    foreach (IdentityReference g in id.Groups)
                    {
                        try
                        {
                            string n = g.Translate(typeof(NTAccount)).Value;
                            if (n.IndexOf("Sandbox", StringComparison.OrdinalIgnoreCase) >= 0) extra += n + " ";
                        }
                        catch { }
                    }
                    AppPaths.Log("identity=" + id.Name + " sandboxGroups=" + (extra.Length > 0 ? extra : "none"));
                }
                catch { }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += delegate (object s, ThreadExceptionEventArgs e)
                {
                    AppPaths.Log("ui ex: " + e.Exception.Message);
                };
                AppDomain.CurrentDomain.UnhandledException += delegate (object s, UnhandledExceptionEventArgs e)
                {
                    Exception ex = e.ExceptionObject as Exception;
                    AppPaths.Log("app ex: " + (ex != null ? ex.ToString() : "unknown"));
                };

                Config cfg = Config.Load();
                using (AppHost host = new AppHost(cfg))
                {
                    Application.Run(host);
                }
            }
        }
    }
}