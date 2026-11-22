// LINE 相簿批次儲存工具
// 以 UI Automation 讀取 LINE 視窗類型與元件位置（GroupHomeWindow＝相簿總覽、AlbumDetailWindow＝單本相簿、
// LcContextMenu＝⋮ 選單），文字（相簿名稱、張數）用 Windows 內建 OCR 辨識，全部在本機執行。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Forms;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace LineAlbumSaver {
    static class N {
        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
        [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint data, UIntPtr extra);
        [DllImport("user32.dll")] public static extern void keybd_event(byte k, byte scan, uint f, UIntPtr extra);
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr h, int id, uint mod, uint key);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr h, int id);
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();

        public static uint Pid(IntPtr h) { uint p; GetWindowThreadProcessId(h, out p); return p; }
        public static string Title(IntPtr h) { var s = new StringBuilder(512); GetWindowText(h, s, s.Capacity); return s.ToString(); }
        public static double Scale(IntPtr h) { try { uint d = GetDpiForWindow(h); return d > 0 ? d / 96.0 : 1.0; } catch { return 1.0; } }
        public static void Foreground(IntPtr h) {
            if (IsIconic(h)) { ShowWindow(h, 9); Thread.Sleep(400); }
            if (GetForegroundWindow() == h) return;
            // 按一下 Alt 解除 Windows 的前景鎖定，再切換視窗。
            keybd_event(0x12, 0, 0, UIntPtr.Zero); keybd_event(0x12, 0, 2, UIntPtr.Zero);
            SetForegroundWindow(h); Thread.Sleep(250);
        }
        public static void Click(Point p, uint linePid, string what) {
            var sp = new POINT { X = p.X, Y = p.Y };
            if (Pid(WindowFromPoint(sp)) != linePid) throw new Exception("要點的「" + what + "」被其他視窗遮住了，請把 LINE 視窗露出來再試。");
            SetCursorPos(p.X, p.Y); Thread.Sleep(60);
            mouse_event(2, 0, 0, 0, UIntPtr.Zero); Thread.Sleep(40); mouse_event(4, 0, 0, 0, UIntPtr.Zero);
        }
        public static void Wheel(Point p, int delta) { SetCursorPos(p.X, p.Y); Thread.Sleep(40); mouse_event(0x800, 0, 0, unchecked((uint)delta), UIntPtr.Zero); }
        public static void Escape() { keybd_event(0x1B, 0, 0, UIntPtr.Zero); keybd_event(0x1B, 0, 2, UIntPtr.Zero); }
        public static Bitmap Capture(Rectangle r) {
            var b = new Bitmap(Math.Max(1, r.Width), Math.Max(1, r.Height));
            using (var g = Graphics.FromImage(b)) g.CopyFromScreen(r.Location, Point.Empty, b.Size, CopyPixelOperation.SourceCopy);
            return b;
        }
    }

    // 檔案總管樣式的「選擇資料夾」視窗（IFileOpenDialog + FOS_PICKFOLDERS），取代舊的樹狀 FolderBrowserDialog。
    static class FolderPicker {
        [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")] class FileOpenDialogCo { }
        [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IFileOpenDialog {
            [PreserveSig] int Show(IntPtr owner);
            void SetFileTypes(uint c, IntPtr specs); void SetFileTypeIndex(uint i); void GetFileTypeIndex(out uint i);
            void Advise(IntPtr sink, out uint cookie); void Unadvise(uint cookie);
            void SetOptions(uint fos); void GetOptions(out uint fos);
            void SetDefaultFolder(IShellItem si); void SetFolder(IShellItem si);
            void GetFolder(out IShellItem si); void GetCurrentSelection(out IShellItem si);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name); void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text); void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
            void GetResult(out IShellItem si);
        }
        [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItem {
            void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv); void GetParent(out IShellItem si);
            void GetDisplayName(uint sigdn, [MarshalAs(UnmanagedType.LPWStr)] out string name);
            void GetAttributes(uint mask, out uint attrs); void Compare(IShellItem si, uint hint, out int order);
        }
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        static extern void SHCreateItemFromParsingName(string path, IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IShellItem si);

        public static string Pick(IWin32Window owner, string title, string initial) {
            var dlg = (IFileOpenDialog)new FileOpenDialogCo();
            try {
                dlg.SetOptions(0x20 | 0x40 | 0x800);   // FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST
                dlg.SetTitle(title);
                if (!string.IsNullOrEmpty(initial) && Directory.Exists(initial)) {
                    try { IShellItem si; SHCreateItemFromParsingName(initial, IntPtr.Zero, typeof(IShellItem).GUID, out si); dlg.SetFolder(si); } catch { }
                }
                if (dlg.Show(owner == null ? IntPtr.Zero : owner.Handle) != 0) return null;   // 取消
                IShellItem res; dlg.GetResult(out res);
                string path; res.GetDisplayName(0x80058000, out path);   // SIGDN_FILESYSPATH
                return path;
            } finally { Marshal.ReleaseComObject(dlg); }
        }
    }

    sealed class OcrLine { public string Text; public RectangleF Box; }

    sealed class Ocr {
        readonly OcrEngine engine;
        public Ocr() {
            engine = OcrEngine.TryCreateFromLanguage(new Language("zh-Hant-TW")) ?? OcrEngine.TryCreateFromUserProfileLanguages();
            if (engine == null) throw new Exception("Windows 沒有可用的 OCR 語言，請到「設定 > 時間與語言 > 語言」安裝繁體中文（含光學字元辨識）。");
        }
        static T Wait<T>(Windows.Foundation.IAsyncOperation<T> op) {
            var info = (Windows.Foundation.IAsyncInfo)op;
            while (info.Status == Windows.Foundation.AsyncStatus.Started) Thread.Sleep(10);
            if (info.Status != Windows.Foundation.AsyncStatus.Completed) throw new Exception("OCR 失敗");
            return op.GetResults();
        }
        // 回傳的座標是螢幕座標。
        public List<OcrLine> Read(Rectangle screen) {
            if (screen.Width < 4 || screen.Height < 4) return new List<OcrLine>();
            using (var src = N.Capture(screen)) {
                double scale = Math.Min(3.0, (double)OcrEngine.MaxImageDimension / Math.Max(src.Width, src.Height));
                if (scale < 1) scale = 1;
                using (var big = new Bitmap((int)(src.Width * scale), (int)(src.Height * scale)))
                using (var ms = new MemoryStream()) {
                    using (var g = Graphics.FromImage(big)) { g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic; g.DrawImage(src, 0, 0, big.Width, big.Height); }
                    big.Save(ms, ImageFormat.Png);
                    using (var stream = new InMemoryRandomAccessStream()) {
                        using (var w = new DataWriter(stream.GetOutputStreamAt(0))) { w.WriteBytes(ms.ToArray()); Wait(w.StoreAsync()); Wait(w.FlushAsync()); w.DetachStream(); }
                        stream.Seek(0);
                        var dec = Wait(BitmapDecoder.CreateAsync(stream));
                        using (var sb = Wait(dec.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied))) {
                            var res = Wait(engine.RecognizeAsync(sb));
                            return res.Lines.Where(l => l.Words.Count > 0).Select(l => {
                                double x0 = l.Words.Min(x => x.BoundingRect.X), y0 = l.Words.Min(x => x.BoundingRect.Y);
                                double x1 = l.Words.Max(x => x.BoundingRect.X + x.BoundingRect.Width), y1 = l.Words.Max(x => x.BoundingRect.Y + x.BoundingRect.Height);
                                return new OcrLine { Text = l.Text, Box = new RectangleF((float)(screen.X + x0 / scale), (float)(screen.Y + y0 / scale), (float)((x1 - x0) / scale), (float)((y1 - y0) / scale)) };
                            }).OrderBy(l => l.Box.Top).ToList();
                        }
                    }
                }
            }
        }
        public string Text(Rectangle screen) { return Txt.Join(Read(screen).Select(l => l.Text)); }
    }

    static class Txt {
        // Windows OCR 會在中文字之間插空白，把它們拿掉；英文單字間的空白保留。
        public static string Fix(string s) {
            s = (s ?? "").Trim();
            s = Regex.Replace(s, @"(?<=[\p{IsCJKUnifiedIdeographs}\p{IsCJKSymbolsandPunctuation}\p{IsHalfwidthandFullwidthForms}])\s+(?=[\p{IsCJKUnifiedIdeographs}\p{IsCJKSymbolsandPunctuation}\p{IsHalfwidthandFullwidthForms}])", "");
            s = Regex.Replace(s, @"(?<=\d)\s*([/:\-._])\s*(?=\d)", "$1");
            s = Regex.Replace(s, @"(?<=\d)\s+(?=[\p{IsCJKUnifiedIdeographs}])", "");
            s = Regex.Replace(s, @"(?<=[\p{IsCJKUnifiedIdeographs}])\s+(?=\d)", "");
            return s;
        }
        public static string Join(IEnumerable<string> lines) { return Fix(string.Join(" ", lines)); }
        public static string Compact(string s) { return Regex.Replace((s ?? "").Normalize(NormalizationForm.FormKC), @"\s+", ""); }
        // 「16張照片」→16；「照片 12 張・影片 3 部」→15。讀不到回傳 -1。
        public static int Count(string s) {
            s = Compact(s).Replace('O', '0').Replace('o', '0').Replace('l', '1').Replace('I', '1');
            var ms = Regex.Matches(s, @"\d+");
            if (ms.Count == 0) return -1;
            int total = 0; foreach (Match m in ms) { int v; if (int.TryParse(m.Value, out v)) total += v; }
            return total;
        }
        // 兩次 OCR 讀同一張卡片可能差一兩個字：一樣、互相包含、或編輯距離在 20% 以內就算同一本。
        public static bool Similar(string a, string b) {
            a = Compact(a).TrimEnd('…', '.'); b = Compact(b).TrimEnd('…', '.');
            if (a.Length == 0 || b.Length == 0) return a == b;
            if (a == b || a.Contains(b) || b.Contains(a)) return true;
            var d = new int[a.Length + 1, b.Length + 1];
            for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
            for (int j = 0; j <= b.Length; j++) d[0, j] = j;
            for (int i = 1; i <= a.Length; i++)
                for (int j = 1; j <= b.Length; j++)
                    d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            return d[a.Length, b.Length] <= Math.Max(1, Math.Max(a.Length, b.Length) / 5);
        }
        // 選單的「刪除相簿」：一定要同時讀到「刪除」和「相簿」才算。
        public static bool IsDeleteAlbum(string s) {
            s = Compact(s);
            return (s.Contains("刪除") && s.Contains("相簿")) || s.ToLowerInvariant().Contains("deletealbum");
        }
        // 確認視窗的按鈕：文字「剛好」是刪除／確定（訊息文字「確定要刪除…嗎」不算）。
        public static bool IsConfirmWord(string s) {
            s = Compact(s).ToLowerInvariant();
            return s == "刪除" || s == "確定" || s == "确定" || s == "删除" || s == "ok" || s == "delete";
        }
        public static bool IsCancelWord(string s) {
            s = Compact(s).ToLowerInvariant();
            return s.Contains("取消") || s.Contains("cancel");
        }
        public static bool IsSaveAll(string s) {
            s = Compact(s);
            return (s.Contains("全部") && (s.Contains("儲") || s.Contains("存") || s.Contains("保"))) || s.Contains("儲存全") || s.ToLowerInvariant().Contains("saveall");
        }
        public static string SafeFolder(string name) {
            string r = (name ?? "").Replace("/", "").Replace("\\", "");
            r = Regex.Replace(r, "[<>:\"|?*\\x00-\\x1f]", "_").Trim().TrimEnd('.', ' ');
            if (r.Length == 0) r = "未命名相簿";
            if (Regex.IsMatch(r, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase)) r = "_" + r;
            if (r.Length > 80) r = r.Substring(0, 80);
            return r;
        }
        public static string Csv(object o) { return "\"" + (o == null ? "" : o.ToString()).Replace("\"", "\"\"") + "\""; }
    }

    static class Ui {
        public static readonly string[] TempExt = { ".tmp", ".part", ".crdownload", ".download", ".partial" };
        public static Rectangle R(AutomationElement e) {
            var b = e.Current.BoundingRectangle;
            if (b.IsEmpty || double.IsInfinity(b.Width)) return Rectangle.Empty;
            return new Rectangle((int)Math.Round(b.X), (int)Math.Round(b.Y), (int)Math.Round(b.Width), (int)Math.Round(b.Height));
        }
        public static List<uint> LinePids() { return Process.GetProcessesByName("LINE").Select(p => (uint)p.Id).ToList(); }
        public static List<AutomationElement> TopWindows() {
            var list = new List<AutomationElement>();
            foreach (var pid in LinePids())
                foreach (AutomationElement e in AutomationElement.RootElement.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ProcessIdProperty, (int)pid)))
                    list.Add(e);
            return list;
        }
        public static List<AutomationElement> TopWindows(string cls) {
            return TopWindows().Where(e => { try { return e.Current.ClassName == cls; } catch { return false; } }).ToList();
        }
        public static List<AutomationElement> Find(AutomationElement root, string cls, TreeScope scope = TreeScope.Descendants) {
            var list = new List<AutomationElement>();
            foreach (AutomationElement e in root.FindAll(scope, new PropertyCondition(AutomationElement.ClassNameProperty, cls))) list.Add(e);
            return list;
        }
        // 清單的每一列。LINE 26.6 起相簿卡片的類型從 ListItem 變成 Button，兩種都收。
        public static List<AutomationElement> Rows(AutomationElement list) {
            var rows = new List<AutomationElement>();
            var cond = new OrCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem), new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
            foreach (AutomationElement e in list.FindAll(TreeScope.Children, cond)) rows.Add(e);
            return rows;
        }
        public static AutomationElement First(AutomationElement root, string cls) {
            return root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ClassNameProperty, cls));
        }
        public static IntPtr Hwnd(AutomationElement e) { return new IntPtr(e.Current.NativeWindowHandle); }
        public static int CountFiles(string folder) {
            if (!Directory.Exists(folder)) return 0;
            int n = 0;
            foreach (var f in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)) {
                string ext = Path.GetExtension(f).ToLowerInvariant();
                if (TempExt.Contains(ext) || Path.GetFileName(f).StartsWith("~")) continue;
                try { if (new FileInfo(f).Length == 0) continue; } catch { continue; }
                n++;
            }
            return n;
        }
    }

    sealed class LineWindow {
        public IntPtr Hwnd; public string Kind; public string Label; public bool IsOverview; public bool OnAlbumTab = true;
        public override string ToString() { return Label; }
    }

    static class Detect {
        // 相簿總覽的群組名稱：標題列下方、頁籤上方的那段文字。
        public static Rectangle GroupNameRect(AutomationElement win) {
            var tab = Ui.First(win, "GroupHomeTabPanel");
            int limit = tab != null ? Ui.R(tab).Top : int.MaxValue;
            var t = Ui.Find(win, "LcText").Select(Ui.R).Where(r => r.Height > 0 && r.Bottom <= limit + 2).OrderByDescending(r => r.Width * r.Height).FirstOrDefault();
            return t;
        }
        // 頁籤底下那條短線在左半邊＝目前在「相簿」頁籤。
        public static bool OnAlbumTab(AutomationElement win) {
            var tab = Ui.First(win, "GroupHomeTabPanel");
            if (tab == null) return true;
            var tr = Ui.R(tab);
            var under = Ui.Find(tab, "LcLine").Select(Ui.R).Where(r => r.Height <= 6 && r.Width < tr.Width / 2).ToList();
            if (under.Count == 0) return true;
            return under[0].Left + under[0].Width / 2 < tr.Left + tr.Width / 2;
        }
        public static List<LineWindow> Windows(Ocr ocr) {
            var result = new List<LineWindow>();
            foreach (var e in Ui.TopWindows()) {
                string cls; IntPtr h;
                try { cls = e.Current.ClassName; h = Ui.Hwnd(e); } catch { continue; }
                var w = new LineWindow { Hwnd = h, Kind = cls };
                bool min = N.IsIconic(h);
                if (cls == "GroupHomeWindow") {
                    w.IsOverview = true;
                    string name = "";
                    if (!min) { try { name = ocr.Text(GroupNameRect(e)); } catch { } w.OnAlbumTab = OnAlbumTab(e); }
                    if (name.Length == 0) name = "（名稱讀不到）";
                    w.Label = name + " 相簿" + (min ? "　（已最小化）" : "") + (w.OnAlbumTab ? "" : "　（目前在記事本頁籤）");
                } else if (cls == "ChatWindow") w.Label = N.Title(h) + "　（一般對話視窗）";
                else if (cls == "AllInOneWindow") w.Label = "LINE 主視窗　（聊天列表）";
                else if (cls == "AlbumDetailWindow") w.Label = "單本相簿視窗　（不是總覽）";
                else continue;
                result.Add(w);
            }
            return result.OrderByDescending(x => x.IsOverview).ToList();
        }
    }

    sealed class AlbumRow {
        public int No; public string Name = ""; public int Expected = -1; public int Actual; public string Status = ""; public string Folder = "";
        // 快速掃描記下的：總覽上的絕對位置（捲軸座標）、卡片上讀到的名稱、是否勾選下載
        public double Abs = -1; public string ScanName = ""; public bool Selected;
        public bool Delete;   // 勾選刪除（只有張數核對完全相符的才勾得到）
        public bool Deletable { get {
            return Abs >= 0 && Expected > 0 && Actual == Expected && !string.IsNullOrEmpty(Folder) && Directory.Exists(Folder)
                && (Status.StartsWith("完成 ✔") || Status.StartsWith("已下載（張數相符）") || Status.StartsWith("略過（已下載過，張數相符）"));
        } }
    }

    sealed class Card { public double Abs; public Rectangle Band, Item; public bool Visible; }

    sealed class Runner {
        readonly Ocr ocr; readonly Action<string> log; readonly Action<AlbumRow> report; readonly CancellationToken token;
        public int TimeoutSeconds = 600, StallSeconds = 90;
        uint pid; double scale;
        public Runner(Ocr o, Action<string> l, Action<AlbumRow> r, CancellationToken t) { ocr = o; log = l; report = r; token = t; }
        void Check() { token.ThrowIfCancellationRequested(); }
        void Sleep(int ms) { if (token.WaitHandle.WaitOne(ms)) token.ThrowIfCancellationRequested(); }

        AutomationElement Prepare(IntPtr overview, out AutomationElement list, out AutomationElement bar) {
            if (!N.IsWindow(overview)) throw new Exception("選的 LINE 視窗已經關掉了，請按「重新整理」再選一次。");
            pid = N.Pid(overview); scale = N.Scale(overview);
            N.Foreground(overview); Sleep(300);
            var win = AutomationElement.FromHandle(overview);
            if (win.Current.ClassName != "GroupHomeWindow") throw new Exception("選的不是相簿總覽視窗。");
            if (!Detect.OnAlbumTab(win)) throw new Exception("相簿總覽目前在「記事本」頁籤，請先切到「相簿」。");
            list = Ui.First(win, "LcListView");
            if (list == null) throw new Exception("相簿總覽裡找不到相簿清單。");
            bar = Ui.First(win, "LcScrollBar");
            return win;
        }

        // 一般相簿卡片的高度。最後一本下方多一段空白，卡片會比較高，名稱位置要從頂端算。
        int InitCardH(AutomationElement bar) {
            double small = SmallChange(bar);
            return small * scale >= 150 * scale ? (int)Math.Round(small * scale) : int.MaxValue;
        }

        // 目前畫面上的相簿卡片（由上往下）；Visible＝名稱區完整露出、可以讀字或點擊。
        List<Card> Cards(AutomationElement list, AutomationElement bar, ref int cardH, out double v, out double max) {
            var lr = Ui.R(list); v = ScrollValue(bar, out max);
            var items = Ui.Rows(list);
            foreach (var it in items) { int h = Ui.R(it).Height; if (h >= 150 * scale && h < cardH) cardH = h; }
            var cards = new List<Card>();
            foreach (var it in items.OrderBy(i => Ui.R(i).Top)) {
                var r = Ui.R(it); if (r.Height < 100 * scale) continue;
                var item = new Rectangle(r.X, r.Y, r.Width, Math.Min(r.Height, cardH));
                var band = NameBand(item);
                cards.Add(new Card { Abs = v + (r.Top - lr.Top) / scale, Item = item, Band = band, Visible = band.Top >= lr.Top && band.Bottom <= lr.Bottom });
            }
            return cards;
        }

        // 讀卡片名稱區：第一行是名稱，「N張」那行是張數（名稱常帶日期數字，不能直接抓數字）。
        void ReadCard(Rectangle band, out string name, out int count) {
            var lines = ocr.Read(band);
            name = lines.Count > 0 ? Txt.Fix(lines[0].Text) : ""; count = -1;
            foreach (var l in lines.Skip(1)) {
                if (Regex.IsMatch(Txt.Compact(l.Text), @"\d+(張|部|個|項|photo|video|item)", RegexOptions.IgnoreCase)) { count = Txt.Count(l.Text); break; }
            }
        }

        // ① 快速掃描：從最上面捲到最底，只讀每本的名稱與張數，不點開任何相簿。
        public List<AlbumRow> Scan(IntPtr overview) {
            AutomationElement list, bar; Prepare(overview, out list, out bar);
            log("快速掃描：從最上面往下讀相簿名稱與張數，不會點開相簿。螢幕縮放 " + (int)(scale * 100) + "%。");
            if (bar != null && SetScroll(bar, 0)) Sleep(700);
            var found = new List<AlbumRow>(); int noMove = 0, cardH = InitCardH(bar);
            while (true) {
                Check();
                if (!N.IsWindow(overview)) throw new Exception("相簿總覽視窗被關掉了。");
                double v, max; var cards = Cards(list, bar, ref cardH, out v, out max);
                foreach (var c in cards.Where(c => c.Visible)) {
                    if (found.Any(f => Math.Abs(f.Abs - c.Abs) < 30)) continue;
                    string name; int count; ReadCard(c.Band, out name, out count);
                    var row = new AlbumRow { No = found.Count + 1, Abs = c.Abs, Name = name, ScanName = name, Expected = count, Status = "掃描到" };
                    found.Add(row); report(row);
                }
                if (bar == null || v >= max - 0.5) break;
                var lr = Ui.R(list);
                double step = Math.Max(60, lr.Height / scale * 0.5);
                if (!SetScroll(bar, Math.Min(max, v + step))) N.Wheel(new Point(lr.Left + lr.Width / 2, lr.Top + lr.Height / 2), -120);
                Sleep(450);
                double v2 = ScrollValue(bar, out max);
                if (Math.Abs(v2 - v) < 0.5) { if (++noMove >= 4) { log("捲軸連續 4 次沒有移動，停止。"); break; } }
                else noMove = 0;
            }
            log("掃描完成：共 " + found.Count + " 本。");
            return found;
        }

        // ③ 只下載勾選的相簿：捲到掃描時記下的位置，確認名稱沒變才點開。
        public void Download(IntPtr overview, string dest, List<AlbumRow> picks) {
            AutomationElement list, bar; var win = Prepare(overview, out list, out bar);
            log("開始下載勾選的 " + picks.Count + " 本。");
            int cardH = InitCardH(bar);
            foreach (var row in picks) {
                Check();
                if (!N.IsWindow(overview)) throw new Exception("相簿總覽視窗被關掉了。");
                try {
                    var c = Locate(list, bar, row.Abs, ref cardH);
                    if (c == null) throw new Exception("總覽上找不到這本相簿的位置，可能有人新增或刪除了相簿，請重新掃描。");
                    string now; int cnt; ReadCard(c.Band, out now, out cnt);
                    if (!Txt.Similar(now, row.ScanName)) throw new Exception("這個位置現在是「" + now + "」，跟掃描時的「" + row.ScanName + "」不同，請重新掃描。");
                    DoAlbum(win, overview, c.Band, c.Item, dest, row);
                }
                catch (OperationCanceledException) { row.Status = "已停止"; report(row); throw; }
                catch (Exception ex) { row.Status = "失敗：" + ex.Message; log("第 " + row.No + " 本失敗：" + ex.Message); CloseStray(); }
                report(row);
                N.Foreground(overview); Sleep(400);
            }
        }

        Card Locate(AutomationElement list, AutomationElement bar, double abs, ref int cardH) {
            var lr = Ui.R(list);
            for (int attempt = 0; attempt < 40; attempt++) {
                Check();
                double v, max; var cards = Cards(list, bar, ref cardH, out v, out max);
                var c = cards.FirstOrDefault(x => Math.Abs(x.Abs - abs) < 30);
                if (c != null && c.Visible) return c;
                // 讓卡片頂端靠近清單上緣；直接設捲軸不成功時才用滾輪慢慢靠過去
                double target = Math.Max(0, Math.Min(max, abs - 20));
                if (Math.Abs(target - v) < 0.5 && attempt > 0) return null;
                if (bar == null || !SetScroll(bar, target)) N.Wheel(new Point(lr.Left + lr.Width / 2, lr.Top + lr.Height / 2), target > v ? -120 : 120);
                Sleep(500);
            }
            return null;
        }

        // 相簿卡片下緣的名稱＋張數區（避開右邊的 ⋮）。item 要傳「一般卡片高度」裁過的矩形。
        Rectangle NameBand(Rectangle item) {
            int h = (int)(56 * scale);
            return new Rectangle(item.Left + (int)(8 * scale), item.Bottom - h, item.Width - (int)(60 * scale), h - (int)(2 * scale));
        }

        static double SmallChange(AutomationElement bar) {
            try { object p; if (bar == null || !bar.TryGetCurrentPattern(RangeValuePattern.Pattern, out p)) return 0; return ((RangeValuePattern)p).Current.SmallChange; } catch { return 0; }
        }
        static double ScrollValue(AutomationElement bar, out double max) {
            max = 0; if (bar == null) return 0;
            object p; if (!bar.TryGetCurrentPattern(RangeValuePattern.Pattern, out p)) return 0;
            var rv = (RangeValuePattern)p; max = rv.Current.Maximum; return rv.Current.Value;
        }
        static bool SetScroll(AutomationElement bar, double value) {
            try { object p; if (!bar.TryGetCurrentPattern(RangeValuePattern.Pattern, out p)) return false; ((RangeValuePattern)p).SetValue(value); return true; } catch { return false; }
        }

        void DoAlbum(AutomationElement overviewEl, IntPtr overview, Rectangle band, Rectangle item, string dest, AlbumRow row) {
            // 1. 點總覽上的相簿名稱
            string listName; var detail = OpenDetail(overview, band, item, row, out listName);
            IntPtr dh = Ui.Hwnd(detail);
            try {
                Sleep(1200);
                // 2. 讀單本相簿的標題與張數
                string title; int expected;
                if (!AlbumInfo(detail, ocr, out title, out expected)) throw new Exception("單本相簿視窗讀不到標題與張數的位置。");
                if (title.Length == 0) title = listName;
                row.Name = title; row.Expected = expected;
                log("　相簿名稱：" + (title.Length > 0 ? title : "（讀不到）") + "，" + (expected >= 0 ? expected + " 張" : "張數讀不到"));

                // 3. 決定資料夾；同名且張數相符就略過
                string folder = Path.Combine(dest, Txt.SafeFolder(title.Length > 0 ? title : "相簿_" + row.No));
                if (Directory.Exists(folder)) {
                    int have = Ui.CountFiles(folder);
                    if (expected > 0 && have == expected) {
                        row.Folder = folder; row.Actual = have; row.Status = "略過（已下載過，張數相符）";
                        log("　" + row.Status); return;
                    }
                    string stem = folder; int k = 2;
                    while (Directory.Exists(folder) && Ui.CountFiles(folder) > 0) folder = stem + " (" + k++ + ")";
                }
                Directory.CreateDirectory(folder); row.Folder = folder;
                report(row);

                // 4. 右上角 ⋮ → 儲存全部
                ClickSaveAll(detail);
                // 5. 資料夾對話框填入路徑
                FillFolderDialog(folder);
                // 6. 等檔案存完並核對張數
                row.Status = "下載中…"; report(row);
                row.Actual = WaitFiles(folder, expected, row);
                if (expected > 0 && row.Actual >= expected) row.Status = row.Actual == expected ? "完成 ✔" : "完成（多 " + (row.Actual - expected) + " 個檔）";
                else if (expected > 0) row.Status = "不完整 " + row.Actual + "/" + expected;
                else row.Status = row.Actual > 0 ? "完成（張數無法核對）" : "失敗：資料夾是空的";
                log("　" + row.Status + "　→ " + folder);
            } finally {
                CloseDetail(dh);
            }
        }

        static Rectangle Pad(Rectangle r, int p) { return new Rectangle(r.X - p, r.Y - p, r.Width + p * 2, r.Height + p * 2); }

        // 單本相簿的標題與張數。LINE 26.6 起 LcText 讀得到原文（舊版才要 OCR），而且標題重複兩行，
        // 所以張數要認「N張」的格式，不能看排第幾行。
        public static bool AlbumInfo(AutomationElement detail, Ocr ocr, out string title, out int count) {
            title = ""; count = -1;
            var bar = Ui.First(detail, "LcTitleBar"); int barBottom = bar != null ? Ui.R(bar).Bottom : Ui.R(detail).Top;
            var texts = Ui.Find(detail, "LcText").Where(e => { var r = Ui.R(e); return r.Height > 0 && r.Top >= barBottom; }).OrderBy(e => Ui.R(e).Top).ThenBy(e => Ui.R(e).Left).ToList();
            if (texts.Count < 2) return false;
            var read = texts.Select(e => { string s = (e.Current.Name ?? "").Trim(); return s.Length > 0 ? s : ocr.Text(Pad(Ui.R(e), 4)); }).ToList();
            title = read[0];
            string name = Txt.Compact(title);
            foreach (var s in read.Skip(1)) {
                string c = Txt.Compact(s);
                if (c != name && Regex.IsMatch(c, @"\d+(張|部|個|項|photo|video|item)", RegexOptions.IgnoreCase)) { count = Txt.Count(s); break; }
            }
            return true;
        }

        AutomationElement WaitWindow(string cls, HashSet<IntPtr> before, int ms) {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms) {
                Check();
                var w = Ui.TopWindows(cls).FirstOrDefault(e => !before.Contains(Ui.Hwnd(e)));
                if (w != null) return w;
                Sleep(250);
            }
            return null;
        }

        void ClickSaveAll(AutomationElement detail) { ClickMenu(detail, Txt.IsSaveAll, "儲存全部"); }

        // 右上 ⋮ → 依文字點選單項目。「儲存全部」和「刪除相簿」在同一個選單，所以只認字、絕不靠位置猜。
        void ClickMenu(AutomationElement detail, Func<string, bool> match, string label) {
            var dr = Ui.R(detail);
            var bar = Ui.First(detail, "LcTitleBar"); int top = bar != null ? Ui.R(bar).Bottom - 2 : dr.Top;
            var scroll = Ui.First(detail, "LcScrollArea"); int bottom = scroll != null ? Ui.R(scroll).Top + 2 : dr.Top + (int)(120 * scale);
            var dots = Ui.Find(detail, "LcButton").Select(Ui.R).Where(r => r.Top >= top && r.Bottom <= bottom && r.Width > 0).OrderByDescending(r => r.Right).FirstOrDefault();
            if (dots.IsEmpty) throw new Exception("找不到單本相簿右上角的 ⋮ 按鈕。");
            for (int attempt = 1; attempt <= 3; attempt++) {
                Check();
                var before = new HashSet<IntPtr>(Ui.TopWindows("LcContextMenu").Select(Ui.Hwnd));
                N.Foreground(Ui.Hwnd(detail));
                N.Click(new Point(dots.Left + dots.Width / 2, dots.Top + dots.Height / 2), pid, "⋮");
                var menu = WaitWindow("LcContextMenu", before, 4000);
                if (menu == null) { log("　⋮ 選單沒有出現，重試（" + attempt + "/3）"); N.Escape(); Sleep(500); continue; }
                Sleep(300);
                var menuEls = Ui.Find(menu, "LcContextMenuItem");
                var items = menuEls.Select(Ui.R).Where(r => r.Height > 0).OrderBy(r => r.Top).ToList();
                var lines = ocr.Read(Ui.R(menu));
                Rectangle target = Rectangle.Empty;
                // 有原文就直接認字；沒有原文才用 OCR 對每一項認字。讀不到字就不點。
                var named = menuEls.FirstOrDefault(e => match(e.Current.Name));
                if (named != null) target = Ui.R(named);
                else foreach (var it in items) {
                    var hit = lines.FirstOrDefault(l => { float cy = l.Box.Top + l.Box.Height / 2; return cy >= it.Top && cy <= it.Bottom; });
                    if (hit != null && match(hit.Text)) { target = it; break; }
                }
                if (target.IsEmpty) { log("　選單裡找不到「" + label + "」（讀到：" + string.Join("、", lines.Select(l => Txt.Fix(l.Text))) + "），重試"); N.Escape(); Sleep(500); continue; }
                N.Click(new Point(target.Left + target.Width / 3, target.Top + target.Height / 2), pid, label);
                log("　已按「" + label + "」");
                return;
            }
            throw new Exception("按 ⋮ 三次都沒能點到「" + label + "」。");
        }

        // 點總覽上的相簿名稱，等單本相簿視窗開出來。
        AutomationElement OpenDetail(IntPtr overview, Rectangle band, Rectangle item, AlbumRow row, out string listName) {
            var lines = ocr.Read(band);
            listName = lines.Count > 0 ? Txt.Fix(lines[0].Text) : "";
            Point clickAt = lines.Count > 0
                ? new Point((int)(lines[0].Box.Left + Math.Min(lines[0].Box.Width / 2, 60 * scale)), (int)(lines[0].Box.Top + lines[0].Box.Height / 2))
                : new Point(item.Left + (int)(40 * scale), item.Bottom - (int)(31 * scale));
            log("第 " + row.No + " 本：點「" + (listName.Length > 0 ? listName : "（名稱）") + "」");
            var before = new HashSet<IntPtr>(Ui.TopWindows("AlbumDetailWindow").Select(Ui.Hwnd));
            N.Foreground(overview);
            N.Click(clickAt, pid, "相簿名稱");
            AutomationElement detail = WaitWindow("AlbumDetailWindow", before, 15000);
            if (detail == null) throw new Exception("點了相簿名稱，但 15 秒內沒有開出單本相簿視窗。");
            return detail;
        }

        // 刪除勾選的相簿：從清單最下面往上刪，前面相簿的位置才不會跑掉。
        // 每本都重新打開核對（標題、張數＝資料夾檔案數），不符就不刪。
        public void Delete(IntPtr overview, List<AlbumRow> picks) {
            AutomationElement list, bar; var win = Prepare(overview, out list, out bar);
            log("開始刪除勾選的 " + picks.Count + " 本（由下往上）。");
            int cardH = InitCardH(bar);
            foreach (var row in picks.OrderByDescending(r => r.Abs)) {
                Check();
                if (!N.IsWindow(overview)) throw new Exception("相簿總覽視窗被關掉了。");
                try {
                    var c = Locate(list, bar, row.Abs, ref cardH);
                    if (c == null) throw new Exception("總覽上找不到這本相簿的位置，請重新掃描。未刪除。");
                    string now; int cnt; ReadCard(c.Band, out now, out cnt);
                    if (!Txt.Similar(now, row.ScanName)) throw new Exception("這個位置現在是「" + now + "」，跟掃描時的「" + row.ScanName + "」不同，請重新掃描。未刪除。");
                    string listName; var detail = OpenDetail(overview, c.Band, c.Item, row, out listName);
                    IntPtr dh = Ui.Hwnd(detail);
                    bool deleted = false;
                    try {
                        Sleep(1200);
                        string title; int expected;
                        if (!AlbumInfo(detail, ocr, out title, out expected)) throw new Exception("讀不到相簿標題與張數。未刪除。");
                        int have = Ui.CountFiles(row.Folder);
                        log("　核對：LINE「" + title + "」" + expected + " 張，資料夾 " + have + " 個檔");
                        if (!Txt.Similar(title, row.Name) && !Txt.Similar(title, row.ScanName)) throw new Exception("打開的相簿是「" + title + "」，跟清單上的不同。未刪除。");
                        if (expected <= 0 || have != expected || expected != row.Expected) throw new Exception("張數不符（LINE " + expected + "、資料夾 " + have + "）。未刪除。");
                        // 先記下「按刪除相簿之前」的視窗與按鈕，確認視窗的按鈕才分得出是新出現的
                        var beforeWins = new HashSet<IntPtr>(Ui.TopWindows().Select(Ui.Hwnd));
                        var beforeBtns = new HashSet<string>(AllButtons(detail).Select(b => Ui.R(b).ToString()));
                        ClickMenu(detail, Txt.IsDeleteAlbum, "刪除相簿");
                        ConfirmDelete(beforeWins, beforeBtns, detail);
                        // 刪掉後單本相簿視窗會自己關掉
                        for (int i = 0; i < 40 && N.IsWindow(dh); i++) Sleep(250);
                        deleted = !N.IsWindow(dh);
                        row.Status = deleted ? "已刪除（LINE 上）" : "刪除結果不確定，請到 LINE 確認";
                        log("　" + row.Status);
                    } finally { if (!deleted) { CloseStray(); CloseDetail(dh); } }
                    if (deleted) row.Delete = false;
                }
                catch (OperationCanceledException) { row.Status = "已停止"; report(row); throw; }
                catch (Exception ex) { row.Status = "未刪除：" + ex.Message; log("第 " + row.No + " 本未刪除：" + ex.Message); CloseStray(); }
                report(row);
                N.Foreground(overview); Sleep(600);
            }
        }

        // LINE 的確認視窗：「取消」＋紅色「刪除」。可能是新的頂層視窗，也可能疊在單本視窗裡。
        // LINE 的按鈕常常沒有原文，所以每顆新按鈕都用 OCR 讀字、並看底色：
        // 字剛好是「刪除／確定」才按；讀不到字時，只有「唯一一顆紅色、而且不是取消」的按鈕才按。
        void ConfirmDelete(HashSet<IntPtr> beforeWins, HashSet<string> beforeBtns, AutomationElement detail) {
            var sw = Stopwatch.StartNew(); bool logged = false;
            while (sw.ElapsedMilliseconds < 8000) {
                Check();
                var cands = new List<AutomationElement>();
                var fresh = Ui.TopWindows().Where(e => !beforeWins.Contains(Ui.Hwnd(e))).Where(e => { try { return e.Current.ClassName != "LcContextMenu"; } catch { return false; } }).ToList();
                foreach (var w in fresh) cands.AddRange(AllButtons(w));
                cands.AddRange(AllButtons(detail).Where(b => !beforeBtns.Contains(Ui.R(b).ToString())));
                var info = cands.Select(b => { var r = Ui.R(b); string name = ""; try { name = (b.Current.Name ?? "").Trim(); } catch { }
                    string text = name.Length > 0 ? name : (r.Width > 4 && r.Height > 4 ? Txt.Fix(ocr.Text(r)) : "");
                    return new { R = r, Text = text, Red = IsRed(r) }; }).Where(x => !x.R.IsEmpty).ToList();
                if (info.Count > 0) {
                    if (!logged) { log("　確認視窗按鈕：" + string.Join("、", info.Select(x => "「" + x.Text + "」" + (x.Red ? "(紅)" : "")))); logged = true; }
                    var pick = info.FirstOrDefault(x => Txt.IsConfirmWord(x.Text));
                    if (pick == null) { var reds = info.Where(x => x.Red && !Txt.IsCancelWord(x.Text)).ToList(); if (reds.Count == 1) pick = reds[0]; }
                    if (pick != null) {
                        N.Click(new Point(pick.R.Left + pick.R.Width / 2, pick.R.Top + pick.R.Height / 2), pid, "確認刪除");
                        log("　已按確認視窗的「" + (pick.Text.Length > 0 ? pick.Text : "紅色按鈕") + "」");
                        return;
                    }
                }
                Sleep(300);
            }
            N.Escape();
            throw new Exception(logged ? "確認視窗裡分不出哪一顆是「刪除」，已取消。未刪除。" : "按了「刪除相簿」但沒有找到確認視窗，已取消。未刪除。");
        }
        // 按鈕底色偏紅（LINE 的刪除鈕是紅底白字）：取整顆按鈕的平均色。
        static bool IsRed(Rectangle r) {
            if (r.Width < 4 || r.Height < 4) return false;
            try {
                using (var b = N.Capture(r)) {
                    long R = 0, G = 0, B = 0, n = 0;
                    for (int y = 1; y < b.Height - 1; y += 2) for (int x = 1; x < b.Width - 1; x += 2) { var c = b.GetPixel(x, y); R += c.R; G += c.G; B += c.B; n++; }
                    if (n == 0) return false; R /= n; G /= n; B /= n;
                    return R > 140 && R > G + 50 && R > B + 50;
                }
            } catch { return false; }
        }
        // LINE 的按鈕不一定是 Button 類型，LcButton 也收。
        static List<AutomationElement> AllButtons(AutomationElement root) {
            var list = new List<AutomationElement>();
            try {
                var cond = new OrCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button), new PropertyCondition(AutomationElement.ClassNameProperty, "LcButton"));
                foreach (AutomationElement e in root.FindAll(TreeScope.Descendants, cond)) list.Add(e);
            } catch { }
            return list;
        }

        // LINE 按「儲存全部」後會跳出 Windows 的「選擇資料夾」視窗：路徑框 ID 1152、確認鈕 ID 1。
        void FillFolderDialog(string folder) {
            AutomationElement edit = null; var sw = Stopwatch.StartNew();
            var cond = new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit), new PropertyCondition(AutomationElement.AutomationIdProperty, "1152"));
            while (edit == null && sw.ElapsedMilliseconds < 12000) {
                Check();
                foreach (var w in Ui.TopWindows()) { try { edit = w.FindFirst(TreeScope.Descendants, cond); } catch { } if (edit != null) break; }
                if (edit == null) Sleep(300);
            }
            if (edit == null) throw new Exception("按了「儲存全部」，但 12 秒內沒有出現選擇資料夾的視窗。");
            Sleep(300);
            object vp; if (!edit.TryGetCurrentPattern(ValuePattern.Pattern, out vp)) throw new Exception("資料夾視窗的路徑欄無法自動填入。");
            ((ValuePattern)vp).SetValue(folder); Sleep(200);
            AutomationElement node = edit, ok = null;
            var okCond = new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button), new PropertyCondition(AutomationElement.AutomationIdProperty, "1"));
            for (int up = 0; up < 10 && ok == null && node != null; up++) { ok = node.FindFirst(TreeScope.Descendants, okCond); if (ok == null) node = TreeWalker.ControlViewWalker.GetParent(node); }
            if (ok == null) throw new Exception("資料夾視窗找不到「選擇資料夾」按鈕。");
            object ip; if (!ok.TryGetCurrentPattern(InvokePattern.Pattern, out ip)) throw new Exception("「選擇資料夾」按鈕無法操作。");
            ((InvokePattern)ip).Invoke();
            log("　已選定資料夾，LINE 開始儲存");
            Sleep(800);
        }

        int WaitFiles(string folder, int expected, AlbumRow row) {
            var sw = Stopwatch.StartNew(); int last = -1; long lastChange = 0, lastReport = 0;
            while (true) {
                Check();
                int n = Ui.CountFiles(folder); long t = sw.ElapsedMilliseconds;
                if (n != last) { last = n; lastChange = t; }
                if (t - lastReport > 1500) { row.Actual = n; row.Status = "下載中… " + n + (expected > 0 ? "/" + expected : ""); report(row); lastReport = t; }
                long quiet = t - lastChange;
                if (expected > 0 && n >= expected && quiet >= 3000) return n;
                if (expected <= 0 && n > 0 && quiet >= 10000) return n;
                if (quiet >= StallSeconds * 1000L) { log("　檔案數 " + StallSeconds + " 秒沒有增加，不再等待。"); return n; }
                if (t >= TimeoutSeconds * 1000L) { log("　超過每本上限 " + TimeoutSeconds + " 秒，不再等待。"); return n; }
                Sleep(1000);
            }
        }

        void CloseDetail(IntPtr h) {
            if (!N.IsWindow(h)) return;
            N.PostMessage(h, 0x0010, IntPtr.Zero, IntPtr.Zero);
            for (int i = 0; i < 20 && N.IsWindow(h); i++) Thread.Sleep(150);
            if (N.IsWindow(h)) { N.Foreground(h); N.Escape(); Thread.Sleep(300); }
        }
        void CloseStray() {
            try { foreach (var m in Ui.TopWindows("LcContextMenu")) N.Escape(); } catch { }
        }
    }

    static class Theme {
        public static readonly Color Page = Color.FromArgb(0, 16, 30), Card = Color.FromArgb(0, 30, 54), Field = Color.FromArgb(0, 20, 38),
            Border = Color.FromArgb(22, 62, 96), BorderHi = Color.FromArgb(0, 176, 80), Text = Color.FromArgb(226, 236, 244), Muted = Color.FromArgb(124, 152, 176),
            Green = Color.FromArgb(0, 176, 80), GreenHi = Color.FromArgb(28, 214, 110), Cyan = Color.FromArgb(70, 160, 210), Amber = Color.FromArgb(255, 184, 64), Red = Color.FromArgb(255, 96, 88),
            OkBg = Color.FromArgb(0, 50, 44), BadBg = Color.FromArgb(58, 22, 34), SkipBg = Color.FromArgb(10, 36, 60), RunBg = Color.FromArgb(50, 42, 20);
        public static System.Drawing.Drawing2D.GraphicsPath Round(RectangleF r, float rad) {
            var p = new System.Drawing.Drawing2D.GraphicsPath(); float d = rad * 2;
            if (d <= 0) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90); p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90); p.CloseFigure();
            return p;
        }
        public static Bitmap Resource(string name) {
            using (var s = typeof(Theme).Assembly.GetManifestResourceStream(name)) return s == null ? null : new Bitmap(s);
        }
        // NMDC 標誌原圖是白底深藍字：白底轉透明、深藍轉白色、綠色保留，放在深色標題列上。
        public static Bitmap LogoForDark(Bitmap src) {
            if (src == null) return null;
            var dst = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
            for (int y = 0; y < src.Height; y++) for (int x = 0; x < src.Width; x++) {
                var c = src.GetPixel(x, y);
                int min = Math.Min(c.R, Math.Min(c.G, c.B));
                int a = 255 - min; if (a < 24) { dst.SetPixel(x, y, Color.Transparent); continue; }
                bool green = c.G > c.R + 40 && c.G > c.B + 20;
                dst.SetPixel(x, y, green ? Color.FromArgb(Math.Min(255, a * 2), 30, 210, 90) : Color.FromArgb(Math.Min(255, (int)(a * 1.15)), 255, 255, 255));
            }
            return dst;
        }
    }

    sealed class Btn : Button {
        public bool Primary, Danger; bool hover, down;
        public Btn() {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; Cursor = Cursors.Hand; BackColor = Color.Transparent;
        }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnPaint(PaintEventArgs e) {
            var g = e.Graphics; g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Card);
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f); float rad = Height * 0.18f;
            Color fill, border, fore;
            if (!Enabled) { fill = Color.FromArgb(18, 34, 48); border = Color.FromArgb(30, 50, 66); fore = Color.FromArgb(70, 92, 108); }
            else if (Primary) { fill = down ? Color.FromArgb(4, 160, 68) : hover ? Theme.GreenHi : Theme.Green; border = fill; fore = Color.FromArgb(4, 22, 12); }
            else if (Danger) { fill = hover ? Color.FromArgb(70, 26, 28) : Color.FromArgb(40, 20, 24); border = Theme.Red; fore = Theme.Red; }
            else { fill = hover ? Color.FromArgb(20, 48, 68) : Theme.Field; border = hover ? Theme.Cyan : Theme.Border; fore = Theme.Text; }
            using (var path = Theme.Round(r, rad)) {
                if (Primary && Enabled) using (var glow = new Pen(Color.FromArgb(hover ? 90 : 50, Theme.GreenHi), 3f)) g.DrawPath(glow, path);
                using (var b = new SolidBrush(fill)) g.FillPath(b, path);
                using (var p = new Pen(border)) g.DrawPath(p, path);
            }
            TextRenderer.DrawText(g, Text, Font, Rectangle.Round(r), fore, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }
    }

    sealed class MainForm : Form {
        static readonly string Settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LineAlbumSaver.txt");
        public static bool PreviewMode;

        readonly float S;
        readonly Bitmap nmdcLogo, lineLogo;
        readonly ComboBox windows = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat, DrawMode = DrawMode.OwnerDrawFixed };
        readonly TextBox dest = new TextBox { Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, TabStop = false };
        readonly Btn refresh, browse, scan, download, stop, recheck, openDir, selAll, selNone, del;
        readonly NumericUpDown timeout = new NumericUpDown { Minimum = 60, Maximum = 7200, Value = 600, Increment = 60, TextAlign = HorizontalAlignment.Center, BorderStyle = BorderStyle.None };
        readonly Label hint = new Label { AutoSize = true };
        readonly Label chipAll, chipOk, chipSkip, chipBad;
        readonly DataGridView grid = new DataGridView();
        readonly TextBox logBox = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.None, BorderStyle = BorderStyle.None, TabStop = false };
        readonly Panel header = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0) };
        readonly ToolTip tips = new ToolTip();
        readonly List<AlbumRow> rows = new List<AlbumRow>();
        Ocr ocr; CancellationTokenSource cts; bool busy;

        int P(float v) { return (int)Math.Round(v * S); }

        public MainForm() {
            AutoScaleMode = AutoScaleMode.None; DoubleBuffered = true;
            using (var g = CreateGraphics()) S = g.DpiX / 96f;
            Font = new Font("Microsoft JhengHei UI", 8.25f); BackColor = Theme.Page; ForeColor = Theme.Text;
            nmdcLogo = Theme.LogoForDark(Theme.Resource("nmdc.jpg")); lineLogo = Theme.Resource("line.png");
            Text = nmdcLogo != null ? "LINE 相簿批次儲存 · NMDC" : "LINE 相簿批次儲存";
            if (lineLogo != null) try { Icon = Icon.FromHandle(new Bitmap(lineLogo, 32, 32).GetHicon()); } catch { }
            else try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            ClientSize = new Size(P(680), P(610)); MinimumSize = new Size(P(600), P(540));
            StartPosition = FormStartPosition.Manual;
            var wa = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(Math.Max(wa.Left, wa.Right - Width - P(12)), Math.Max(wa.Top, wa.Top + (wa.Height - Height) / 2));

            refresh = MakeButton("⟳  重新整理"); browse = MakeButton("選擇資料夾");
            scan = MakeButton("①  快速掃描相簿"); scan.Primary = true; scan.Font = new Font(Font.FontFamily, 8.5f, FontStyle.Bold); scan.Padding = new Padding(P(10), P(3), P(10), P(3));
            download = MakeButton("②  下載勾選的相簿"); download.Primary = true; download.Font = scan.Font; download.Padding = scan.Padding;
            stop = MakeButton("■  停止  F8"); stop.Danger = true; stop.Enabled = false;
            recheck = MakeButton("✓  重新核對"); openDir = MakeButton("開啟資料夾"); selAll = MakeButton("全選"); selNone = MakeButton("全不選"); del = MakeButton("🗑  刪除勾選的相簿"); del.Danger = true;
            chipAll = Chip(Theme.Text, Color.FromArgb(22, 42, 58)); chipOk = Chip(Theme.GreenHi, Theme.OkBg); chipSkip = Chip(Theme.Muted, Theme.SkipBg); chipBad = Chip(Theme.Red, Theme.BadBg);

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Margin = new Padding(0), BackColor = Theme.Page };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, P(52)));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, P(96)));
            Controls.Add(root);

            header.Paint += PaintHeader; header.Resize += (s, e) => header.Invalidate();
            typeof(Panel).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).SetValue(header, true, null);
            root.Controls.Add(header, 0, 0);

            // 設定
            var form = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, Margin = new Padding(0), BackColor = Theme.Card };
            form.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); form.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            windows.ItemHeight = P(17); windows.BackColor = Theme.Field; windows.ForeColor = Theme.Text; windows.DrawItem += DrawWindowItem;
            dest.BackColor = Theme.Field; dest.ForeColor = Theme.Text;
            form.Controls.Add(FieldLabel("LINE 視窗"), 0, 0); form.Controls.Add(Field(windows, windows.ItemHeight + P(6)), 1, 0); form.Controls.Add(refresh, 2, 0);
            form.Controls.Add(FieldLabel("儲存位置"), 0, 1); form.Controls.Add(Field(dest, dest.PreferredHeight), 1, 1); form.Controls.Add(browse, 2, 1);

            var actions = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 6, Margin = new Padding(0, P(6), 0, 0), BackColor = Theme.Card };
            for (int i = 0; i < 6; i++) actions.ColumnStyles.Add(i == 4 ? new ColumnStyle(SizeType.Percent, 100) : new ColumnStyle(SizeType.AutoSize));
            actions.Controls.Add(scan, 0, 0); actions.Controls.Add(download, 1, 0); actions.Controls.Add(stop, 2, 0); actions.Controls.Add(recheck, 3, 0);
            var wait = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Right, Margin = new Padding(0), BackColor = Theme.Card };
            wait.Controls.Add(new Label { Text = "每本上限", AutoSize = true, ForeColor = Theme.Muted, Margin = new Padding(0, P(7), P(6), 0) });
            timeout.BackColor = Theme.Field; timeout.ForeColor = Theme.Text; timeout.Width = P(52);
            var tf = Field(timeout, timeout.PreferredHeight); tf.Dock = DockStyle.None; tf.Size = new Size(P(66), timeout.PreferredHeight + P(6)); tf.Padding = new Padding(P(6), P(4), P(2), P(4)); tf.Margin = new Padding(0, P(3), P(5), 0); timeout.Dock = DockStyle.Fill; wait.Controls.Add(tf);
            wait.Controls.Add(new Label { Text = "秒", AutoSize = true, ForeColor = Theme.Muted, Margin = new Padding(0, P(7), 0, 0) });
            actions.Controls.Add(wait, 5, 0);
            hint.Margin = new Padding(P(1), P(5), 0, 0); hint.ForeColor = Theme.Muted;

            var setup = CardTable("設定", "SETUP");
            setup.Controls.Add(form); setup.Controls.Add(actions); setup.Controls.Add(hint);
            setup.Margin = new Padding(P(12), P(10), P(12), P(5));
            root.Controls.Add(setup, 0, 1);

            // 下載結果
            var result = CardTable("相簿清單（勾選要下載的）", "ALBUMS"); result.Dock = DockStyle.Fill; result.AutoSize = false; result.Margin = new Padding(P(12), P(5), P(12), P(5));
            var chips = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Anchor = AnchorStyles.Left, Margin = new Padding(0), BackColor = Theme.Card };
            chips.Controls.Add(chipAll); chips.Controls.Add(chipOk); chips.Controls.Add(chipSkip); chips.Controls.Add(chipBad);
            var bar = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 5, Margin = new Padding(0, 0, 0, P(6)), BackColor = Theme.Card };
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); for (int i = 0; i < 3; i++) bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            del.Anchor = selAll.Anchor = selNone.Anchor = openDir.Anchor = AnchorStyles.Right; openDir.Margin = new Padding(0);
            // 第一列：統計；第二列：刪除（左）、全選/全不選/開啟資料夾（右）
            bar.Controls.Add(chips, 0, 0); bar.SetColumnSpan(chips, 5);
            del.Anchor = AnchorStyles.Left; chips.Margin = new Padding(0, 0, 0, P(4));
            bar.Controls.Add(del, 0, 1); bar.Controls.Add(selAll, 2, 1); bar.Controls.Add(selNone, 3, 1); bar.Controls.Add(openDir, 4, 1);
            result.Controls.Add(bar);
            SetupGrid();
            var gridHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(1), BackColor = Theme.Border, Margin = new Padding(0) };
            gridHost.Controls.Add(grid);
            result.RowStyles.Add(new RowStyle(SizeType.AutoSize)); result.RowStyles.Add(new RowStyle(SizeType.AutoSize)); result.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            result.Controls.Add(gridHost);
            root.Controls.Add(result, 0, 2);

            // 執行紀錄
            var logCard = CardTable("執行紀錄", "CONSOLE"); logCard.Dock = DockStyle.Fill; logCard.AutoSize = false; logCard.Margin = new Padding(P(12), P(5), P(12), P(12));
            logBox.BackColor = Theme.Field; logBox.ForeColor = Color.FromArgb(140, 220, 175); logBox.Font = new Font("Consolas", 8f);
            var logHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(P(8), P(6), P(4), P(6)), BackColor = Theme.Field, Margin = new Padding(0) };
            logHost.Paint += (s, e) => { using (var p = new Pen(Theme.Border)) e.Graphics.DrawRectangle(p, 0, 0, logHost.Width - 1, logHost.Height - 1); };
            logHost.Controls.Add(logBox);
            logCard.RowStyles.Add(new RowStyle(SizeType.AutoSize)); logCard.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            logCard.Controls.Add(logHost);
            root.Controls.Add(logCard, 0, 3);

            Resize += (s, e) => FitHint();
            refresh.Click += (s, e) => RefreshWindows();
            browse.Click += (s, e) => {
                var picked = FolderPicker.Pick(this, "選擇相簿要存放的資料夾（每本相簿會建一個子資料夾）", dest.Text);
                if (picked != null) { SetDest(picked); try { File.WriteAllText(Settings, picked, Encoding.UTF8); } catch { } }
            };
            openDir.Click += (s, e) => { if (Directory.Exists(dest.Text)) Process.Start("explorer.exe", "\"" + dest.Text + "\""); };
            windows.SelectedIndexChanged += (s, e) => UpdateButtons();
            scan.Click += (s, e) => Scan();
            download.Click += (s, e) => DownloadPicked();
            selAll.Click += (s, e) => SetAll(true); selNone.Click += (s, e) => SetAll(false);
            del.Click += (s, e) => DeletePicked();
            stop.Click += (s, e) => { if (cts != null) { cts.Cancel(); Log("收到停止指令，處理完目前動作後停止。"); } };
            recheck.Click += (s, e) => Recheck();
            try { if (File.Exists(Settings)) SetDest(File.ReadAllText(Settings, Encoding.UTF8).Trim()); } catch { }
            Shown += (s, e) => { FitHint(); if (PreviewMode) return; N.RegisterHotKey(Handle, 1, 0, (uint)Keys.F8); RefreshWindows(); };
            FormClosed += (s, e) => N.UnregisterHotKey(Handle, 1);
            UpdateSummary(); UpdateButtons();
        }

        // NMDC 標誌裡 M 的那道斜槓：往右上傾斜的平行四邊形。
        static System.Drawing.Drawing2D.GraphicsPath Slash(float x, float y, float w, float h, float skew) {
            var p = new System.Drawing.Drawing2D.GraphicsPath();
            p.AddPolygon(new[] { new PointF(x + skew, y), new PointF(x + skew + w, y), new PointF(x + w, y + h), new PointF(x, y + h) });
            return p;
        }

        void PaintHeader(object sender, PaintEventArgs e) {
            var g = e.Graphics; var r = header.ClientRectangle; if (r.Width < 4) return;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias; g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            using (var b = new System.Drawing.Drawing2D.LinearGradientBrush(r, Color.FromArgb(0, 18, 34), Color.FromArgb(0, 36, 64), 0f)) g.FillRectangle(b, r);
            // 細點陣
            using (var b = new SolidBrush(Color.FromArgb(22, 120, 180, 220))) { int step = P(12); for (int y = step / 2; y < r.Height; y += step) for (int x = r.Width / 2; x < r.Width; x += step) g.FillRectangle(b, x, y, Math.Max(1, P(1)), Math.Max(1, P(1))); }
            // 右側斜槓群（呼應標誌）
            float sh = r.Height, sk = sh * 0.55f, sw = P(22), sx = r.Width - P(300);
            for (int i = 0; i < 4; i++) using (var path = Slash(sx + i * (sw + P(10)), 0, sw, sh, sk)) using (var b = new SolidBrush(Color.FromArgb(22 + i * 12, 60, 130, 200))) g.FillPath(b, path);
            using (var path = Slash(sx + 4 * (sw + P(10)), 0, sw * 0.6f, sh, sk)) using (var b = new SolidBrush(Color.FromArgb(150, Theme.Green))) g.FillPath(b, path);
            using (var lb = new System.Drawing.Drawing2D.LinearGradientBrush(new Rectangle(0, r.Height - P(2), r.Width, P(2)), Theme.Green, Color.FromArgb(0, 0, 176, 80), 0f)) g.FillRectangle(lb, 0, r.Height - P(2), r.Width, P(2));

            int x0 = P(16), cy = r.Height / 2;
            if (nmdcLogo != null) {
                int h = P(24), w = h * nmdcLogo.Width / nmdcLogo.Height; g.DrawImage(nmdcLogo, x0, cy - h / 2 - P(1), w, h); x0 += w + P(13);
                using (var p = new Pen(Color.FromArgb(70, 128, 156, 174))) g.DrawLine(p, x0, cy - P(14), x0, cy + P(14));
                x0 += P(13);
            }
            if (lineLogo != null) { int h = P(26); g.DrawImage(lineLogo, x0, cy - h / 2, h, h); x0 += h + P(10); }
            using (var f1 = new Font(Font.FontFamily, 10.5f, FontStyle.Bold)) using (var f2 = new Font("Consolas", 6.75f))
            using (var w1 = new SolidBrush(Color.White)) using (var w2 = new SolidBrush(Color.FromArgb(110, 170, 200))) {
                g.DrawString("LINE 相簿批次儲存", f1, w1, x0 - P(3), cy - P(17));
                g.DrawString("ALBUM BATCH SAVER // LOCAL OCR // OFFLINE", f2, w2, x0, cy + P(3));
            }
            // 狀態燈
            string st = busy ? "RUNNING" : "READY"; Color sc = busy ? Theme.Amber : Theme.GreenHi;
            using (var f = new Font("Consolas", 7f, FontStyle.Bold)) {
                var sz = g.MeasureString(st, f); float pw = sz.Width + P(24), ph = P(19), px = r.Width - pw - P(14), py = cy - ph / 2;
                using (var path = Theme.Round(new RectangleF(px, py, pw, ph), ph / 2)) {
                    using (var b = new SolidBrush(Color.FromArgb(200, 0, 20, 36))) g.FillPath(b, path);
                    using (var p = new Pen(Color.FromArgb(160, sc))) g.DrawPath(p, path);
                }
                using (var b = new SolidBrush(Color.FromArgb(70, sc))) g.FillEllipse(b, px + P(6), cy - P(5), P(10), P(10));
                using (var b = new SolidBrush(sc)) { g.FillEllipse(b, px + P(8.5f), cy - P(2.5f), P(5), P(5)); g.DrawString(st, f, b, px + P(18), cy - sz.Height / 2); }
            }
        }

        Btn MakeButton(string text) {
            return new Btn { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(P(8), P(2), P(8), P(2)), Margin = new Padding(0, P(2), P(6), P(2)), ForeColor = Theme.Text };
        }
        // 卡片：圓角深色底＋標題列（中文＋英文小字）。內容依序往下排。
        TableLayoutPanel CardTable(string title, string en) {
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, BackColor = Theme.Card, Padding = new Padding(P(12), P(7), P(12), P(9)) };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.Paint += (s, e) => {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (var p = new Pen(Theme.Border)) e.Graphics.DrawRectangle(p, 0, 0, t.Width - 1, t.Height - 1);
                using (var path = Slash(0, P(8), P(6), P(14), P(3))) using (var b = new SolidBrush(Theme.Green)) e.Graphics.FillPath(b, path);
            };
            t.Resize += (s, e) => t.Invalidate();
            var head = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, P(4)), BackColor = Theme.Card };
            head.Controls.Add(new Label { Text = title, AutoSize = true, ForeColor = Theme.Text, Font = new Font(Font.FontFamily, 8.75f, FontStyle.Bold), Margin = new Padding(0) });
            head.Controls.Add(new Label { Text = en, AutoSize = true, ForeColor = Color.FromArgb(70, 130, 160), Font = new Font("Consolas", 6.75f, FontStyle.Bold), Margin = new Padding(P(5), P(3), 0, 0) });
            t.Controls.Add(head);
            return t;
        }
        Panel Field(Control c, int innerHeight) {
            var p = new Panel { Height = innerHeight + P(8), Dock = DockStyle.Fill, BackColor = Theme.Field, Padding = new Padding(P(7), P(4), P(5), P(4)), Margin = new Padding(0, P(2), P(6), P(2)) };
            bool focus = false;
            p.Paint += (s, e) => { using (var pen = new Pen(focus ? Theme.Cyan : Theme.Border)) e.Graphics.DrawRectangle(pen, 0, 0, p.Width - 1, p.Height - 1); };
            c.GotFocus += (s, e) => { focus = true; p.Invalidate(); }; c.LostFocus += (s, e) => { focus = false; p.Invalidate(); };
            p.Resize += (s, e) => p.Invalidate();
            p.Controls.Add(c); return p;
        }
        Label FieldLabel(string text) { return new Label { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = Theme.Muted, Margin = new Padding(0, 0, P(12), 0) }; }
        Label Chip(Color fg, Color bg) { return new Label { AutoSize = true, BackColor = bg, ForeColor = fg, Padding = new Padding(P(7), P(2), P(7), P(2)), Margin = new Padding(0, P(2), P(5), P(2)), Font = new Font(Font, FontStyle.Bold) }; }

        void DrawWindowItem(object sender, DrawItemEventArgs e) {
            bool edit = (e.State & DrawItemState.ComboBoxEdit) != 0, sel = (e.State & DrawItemState.Selected) != 0 && !edit;
            using (var b = new SolidBrush(sel ? Color.FromArgb(18, 60, 80) : Theme.Field)) e.Graphics.FillRectangle(b, e.Bounds);
            if (e.Index < 0) return;
            var w = windows.Items[e.Index] as LineWindow;
            Color fc = w != null && w.IsOverview ? Theme.Text : Theme.Muted;
            var rect = new Rectangle(e.Bounds.X + P(4), e.Bounds.Y, e.Bounds.Width - P(4), e.Bounds.Height);
            if (w != null && w.IsOverview) { using (var b = new SolidBrush(Theme.GreenHi)) e.Graphics.FillEllipse(b, rect.X, rect.Y + rect.Height / 2 - P(3), P(6), P(6)); rect.X += P(12); rect.Width -= P(12); }
            TextRenderer.DrawText(e.Graphics, windows.Items[e.Index].ToString(), windows.Font, rect, fc, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        }

        void SetupGrid() {
            grid.Dock = DockStyle.Fill; grid.ScrollBars = ScrollBars.Vertical; grid.ReadOnly = false; grid.AllowUserToAddRows = false; grid.AllowUserToDeleteRows = false; grid.AllowUserToResizeRows = false;
            grid.RowHeadersVisible = false; grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect; grid.MultiSelect = false;
            grid.BackgroundColor = Theme.Field; grid.BorderStyle = BorderStyle.None; grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal; grid.GridColor = Color.FromArgb(14, 46, 74);
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill; grid.EnableHeadersVisualStyles = false;
            grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None; grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing; grid.ColumnHeadersHeight = P(23);
            var hs = grid.ColumnHeadersDefaultCellStyle; hs.BackColor = Color.FromArgb(0, 36, 64); hs.ForeColor = Color.FromArgb(110, 165, 200); hs.Font = new Font("Microsoft JhengHei UI", 7.75f, FontStyle.Bold); hs.Padding = new Padding(P(4), 0, 0, 0); hs.SelectionBackColor = hs.BackColor;
            grid.RowTemplate.Height = P(22);
            var cs = grid.DefaultCellStyle; cs.BackColor = Theme.Field; cs.ForeColor = Theme.Text; cs.Padding = new Padding(P(4), 0, P(2), 0); cs.WrapMode = DataGridViewTriState.False;
            cs.SelectionBackColor = Color.FromArgb(0, 70, 110); cs.SelectionForeColor = Color.White;
            grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "下載", FillWeight = 7, MinimumWidth = P(36), SortMode = DataGridViewColumnSortMode.NotSortable });
            grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "刪除", FillWeight = 7, MinimumWidth = P(36), SortMode = DataGridViewColumnSortMode.NotSortable, ToolTipText = "只有下載完、張數核對完全相符的相簿才能勾" });
            string[] heads = { "#", "相簿名稱", "應有", "實際", "狀態", "資料夾" }; float[] weights = { 5, 32, 7, 7, 24, 20 };
            for (int i = 0; i < heads.Length; i++) {
                var c = new DataGridViewTextBoxColumn { HeaderText = heads[i], FillWeight = weights[i], SortMode = DataGridViewColumnSortMode.NotSortable, MinimumWidth = P(i == 1 ? 110 : 36), ReadOnly = true };
                if (i == 0 || i == 2 || i == 3) { c.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter; c.HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleCenter; c.DefaultCellStyle.Font = new Font("Consolas", 8.25f); }
                grid.Columns.Add(c);
            }
            grid.ShowCellToolTips = true;
            // 勾選框：點了馬上生效（DataGridView 預設要離開格子才提交）
            grid.CurrentCellDirtyStateChanged += (s, e) => { if (grid.IsCurrentCellDirty && grid.CurrentCell.ColumnIndex <= 1) grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            // 刪除欄：張數沒核對相符的不讓勾；執行中都不能改
            grid.CellBeginEdit += (s, e) => { if (busy || (e.ColumnIndex == 1 && (e.RowIndex >= rows.Count || !rows[e.RowIndex].Deletable))) e.Cancel = true; };
            grid.CellValueChanged += (s, e) => {
                if (e.ColumnIndex > 1 || e.RowIndex < 0 || e.RowIndex >= rows.Count) return;
                var v = grid.Rows[e.RowIndex].Cells[e.ColumnIndex].Value; bool on = v is bool && (bool)v;
                if (e.ColumnIndex == 0) rows[e.RowIndex].Selected = on; else rows[e.RowIndex].Delete = on && rows[e.RowIndex].Deletable;
                UpdateSummary(); UpdateButtons();
            };
            grid.CellDoubleClick += (s, e) => { if (e.ColumnIndex > 1 && e.RowIndex >= 0 && e.RowIndex < rows.Count && Directory.Exists(rows[e.RowIndex].Folder)) Process.Start("explorer.exe", "\"" + rows[e.RowIndex].Folder + "\""); };
        }

        void SetDest(string path) { dest.Text = path; tips.SetToolTip(dest, path); }
        void FitHint() { if (hint.Parent != null) hint.MaximumSize = new Size(Math.Max(P(200), hint.Parent.ClientSize.Width - P(30)), 0); }

        protected override void WndProc(ref Message m) {
            if (m.Msg == 0x0312 && m.WParam.ToInt32() == 1 && cts != null) { cts.Cancel(); Log("F8：停止中…"); }
            base.WndProc(ref m);
        }

        void Log(string s) {
            if (InvokeRequired) { BeginInvoke(new Action<string>(Log), s); return; }
            logBox.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  › " + s.Replace("　", "  ") + Environment.NewLine);
        }

        public void Report(AlbumRow r) {
            if (InvokeRequired) { BeginInvoke(new Action<AlbumRow>(Report), r); return; }
            int i = rows.IndexOf(r);
            if (i < 0) { rows.Add(r); i = grid.Rows.Add(); }
            var g = grid.Rows[i];
            string folderName = string.IsNullOrEmpty(r.Folder) ? "" : Path.GetFileName(r.Folder);
            if (!r.Deletable) r.Delete = false;
            g.SetValues(r.Selected, r.Delete, r.No, r.Name, r.Expected >= 0 ? r.Expected.ToString() : "?", r.Actual, r.Status, folderName);
            g.Cells[7].ToolTipText = r.Folder; g.Cells[3].ToolTipText = r.Name; g.Cells[6].ToolTipText = r.Status;
            g.Cells[1].ToolTipText = r.Deletable ? "勾選後按「刪除勾選的相簿」" : "張數核對完全相符才能刪除";
            bool ok = r.Status.StartsWith("完成"), skip = r.Status.StartsWith("略過") || r.Status.StartsWith("已下載") || r.Status.StartsWith("已刪除"), bad = r.Status.StartsWith("失敗") || r.Status.StartsWith("不完整") || r.Status.StartsWith("已停止") || r.Status.StartsWith("未刪除") || r.Status.StartsWith("刪除結果");
            bool idle = r.Status == "未下載" || r.Status == "掃描到" || r.Status.StartsWith("資料夾已有");
            g.DefaultCellStyle.BackColor = ok ? Theme.OkBg : skip ? Theme.SkipBg : bad ? Theme.BadBg : idle ? Theme.Field : Theme.RunBg;
            g.Cells[6].Style.ForeColor = ok ? Theme.GreenHi : bad ? Theme.Red : skip || idle ? Theme.Muted : Theme.Amber;
            g.Cells[6].Style.Font = new Font(Font, FontStyle.Bold);
            try { int shown = grid.DisplayedRowCount(false); if (shown > 0 && i >= shown) grid.FirstDisplayedScrollingRowIndex = i - shown + 1; } catch { }
            UpdateSummary();
        }

        void UpdateSummary() {
            int ok = rows.Count(r => r.Status.StartsWith("完成")), skip = rows.Count(r => r.Status.StartsWith("略過")), bad = rows.Count(r => r.Status.StartsWith("失敗") || r.Status.StartsWith("不完整") || r.Status.StartsWith("已停止"));
            int pick = rows.Count(r => r.Selected); skip = rows.Count(r => r.Status.StartsWith("略過") || r.Status.StartsWith("已下載"));
            chipAll.Text = "共 " + rows.Count + " 本・勾選 " + pick; chipOk.Text = "✔ 完成 " + ok; chipSkip.Text = "已下載/略過 " + skip; chipBad.Text = "✘ 失敗 " + bad;
            download.Text = "②  下載勾選的相簿" + (pick > 0 ? "（" + pick + "）" : "");
            int dels = rows.Count(r => r.Delete && r.Deletable), gone = rows.Count(r => r.Status.StartsWith("已刪除"));
            del.Text = "🗑  刪除勾選的相簿" + (dels > 0 ? "（" + dels + "）" : "");
            if (gone > 0) chipSkip.Text += "・已刪除 " + gone;
        }

        void RefreshWindows() {
            if (busy) return;
            Cursor = Cursors.WaitCursor; windows.Items.Clear();
            try {
                if (ocr == null) ocr = new Ocr();
                var list = Detect.Windows(ocr);
                foreach (var w in list) windows.Items.Add(w);
                if (list.Count == 0) Log("找不到 LINE 視窗，請先開啟並登入電腦版 LINE。");
                var firstOverview = list.FirstOrDefault(w => w.IsOverview);
                if (firstOverview != null) windows.SelectedItem = firstOverview; else if (list.Count > 0) windows.SelectedIndex = 0;
                if (firstOverview == null && list.Count > 0) Log("目前沒有開著的相簿總覽。請在群組選單點「相簿」，打開後按「重新整理」。");
            } catch (Exception ex) { Log("讀取視窗失敗：" + ex.Message); }
            finally { Cursor = Cursors.Default; UpdateButtons(); }
        }

        void UpdateButtons() {
            var w = windows.SelectedItem as LineWindow;
            bool ready = !busy && w != null && w.IsOverview && w.OnAlbumTab;
            scan.Enabled = ready; download.Enabled = ready && rows.Any(r => r.Selected && r.Abs >= 0);
            del.Enabled = ready && rows.Any(r => r.Delete && r.Deletable);
            stop.Enabled = busy; refresh.Enabled = browse.Enabled = recheck.Enabled = windows.Enabled = selAll.Enabled = selNone.Enabled = !busy;
            grid.Columns[0].ReadOnly = busy;
            header.Invalidate();
            if (busy) return;
            hint.ForeColor = Theme.Muted;
            if (w != null && !w.IsOverview) { hint.Text = "⚠ 這是「" + w.Label.Trim() + "」，不是相簿總覽。請選名稱後面是「相簿」的那一個。"; hint.ForeColor = Theme.Amber; }
            else if (w != null && !w.OnAlbumTab) { hint.Text = "⚠ 相簿總覽目前在「記事本」頁籤，請在 LINE 切到「相簿」再按重新整理。"; hint.ForeColor = Theme.Amber; }
            else if (rows.Count == 0) hint.Text = "在 LINE 打開群組的「相簿」總覽，按「① 快速掃描相簿」列出全部相簿（不會點開）。執行中請勿動滑鼠，F8 可隨時停止。";
            else hint.Text = "在下面清單勾選要下載的相簿，再按「② 下載勾選的相簿」。已下載過、張數相符的預設不勾。";
        }

        bool CanRun(out LineWindow w) {
            w = windows.SelectedItem as LineWindow;
            if (w == null || !w.IsOverview) return false;
            if (string.IsNullOrWhiteSpace(dest.Text) || !Directory.Exists(dest.Text)) { MessageBox.Show(this, "請先選擇儲存位置。", "LINE 相簿批次儲存"); return false; }
            return true;
        }

        // 執行期間把工具縮小（讓 LINE 露出來給 OCR 讀），結束再還原。
        async Task<bool> RunJob(Action<Runner> job) {
            busy = true; cts = new CancellationTokenSource(); UpdateButtons();
            hint.Text = "● 執行中，請勿動滑鼠或鍵盤。按 F8 停止。"; hint.ForeColor = Theme.Amber;
            var token = cts.Token; int secs = (int)timeout.Value; bool ok = false;
            WindowState = FormWindowState.Minimized;
            try {
                await Task.Run(() => job(new Runner(ocr, Log, Report, token) { TimeoutSeconds = secs }));
                ok = true;
            } catch (OperationCanceledException) { Log("已停止。"); }
            catch (Exception ex) { Log("停止：" + ex.Message); MessageBox.Show(this, ex.Message, "已停止"); }
            finally { busy = false; cts = null; WindowState = FormWindowState.Normal; Activate(); UpdateButtons(); }
            return ok;
        }

        // ① 快速掃描：只讀總覽卡片，不點開相簿
        async void Scan() {
            LineWindow w; if (!CanRun(out w)) return;
            rows.Clear(); grid.Rows.Clear(); UpdateSummary();
            string root = dest.Text;
            await RunJob(r => r.Scan(w.Hwnd));
            // 對照儲存位置：同名資料夾已有且張數相符 → 已下載、預設不勾
            foreach (var r in rows) {
                string folder = Path.Combine(root, Txt.SafeFolder(r.Name.Length > 0 ? r.Name : "相簿_" + r.No));
                int have = Directory.Exists(folder) ? Ui.CountFiles(folder) : 0;
                if (have > 0) r.Folder = folder;
                r.Actual = have;
                if (have > 0 && r.Expected > 0 && have == r.Expected) { r.Status = "已下載（張數相符）"; r.Selected = false; }
                else if (have > 0) { r.Status = "資料夾已有 " + have + (r.Expected > 0 ? "/" + r.Expected : "") + " 張"; r.Selected = true; }
                else { r.Status = "未下載"; r.Selected = true; }
                Report(r);
            }
            UpdateSummary(); UpdateButtons();
            if (rows.Count > 0) Log("掃描到 " + rows.Count + " 本，已勾選 " + rows.Count(r => r.Selected) + " 本（已下載過的不勾）。確認勾選後按「② 下載勾選的相簿」。");
        }

        // ② 只下載勾選的
        async void DownloadPicked() {
            LineWindow w; if (!CanRun(out w)) return;
            var picks = rows.Where(r => r.Selected && r.Abs >= 0).ToList();
            if (picks.Count == 0) return;
            foreach (var r in picks) { r.Status = "等待下載"; Report(r); }
            string root = dest.Text;
            if (await RunJob(r => r.Download(w.Hwnd, root, picks))) Log("勾選的相簿都處理完畢。");
            foreach (var r in picks) {
                if (r.Status == "等待下載") r.Status = "已停止";
                if (r.Status.StartsWith("完成") || r.Status.StartsWith("略過")) r.Selected = false;   // 成功的取消勾選，失敗的留著方便重跑
                Report(r);
            }
            UpdateSummary(); UpdateButtons();
            SaveReport(root);
        }

        // 🗑 刪除：列出要刪的相簿，必須輸入「刪除」兩個字才會執行
        async void DeletePicked() {
            LineWindow w; if (!CanRun(out w)) return;
            var picks = rows.Where(r => r.Delete && r.Deletable).ToList();
            if (picks.Count == 0 || !ConfirmDeleteDialog(picks)) return;
            var gaps = rows.Where(r => r.Abs >= 0).Select(r => r.Abs).OrderBy(a => a).ToList();
            var diffs = gaps.Zip(gaps.Skip(1), (a, b) => b - a).Where(d => d > 30).OrderBy(d => d).ToList();
            double step = diffs.Count > 0 ? diffs[diffs.Count / 2] : 0;
            await RunJob(r => r.Delete(w.Hwnd, picks));
            // 刪掉的相簿下面那些會往上移一格：調整位置，之後下載前仍會再比對名稱
            foreach (var d in picks.Where(p => p.Status.StartsWith("已刪除")).OrderByDescending(p => p.Abs)) {
                foreach (var r in rows) if (r.Abs > d.Abs) r.Abs -= step;
                d.Abs = -1; d.Selected = false; d.Delete = false;
            }
            foreach (var r in picks) Report(r);
            UpdateSummary(); UpdateButtons();
            int n = picks.Count(p => p.Status.StartsWith("已刪除"));
            Log("刪除結束：成功 " + n + " 本，未刪除 " + (picks.Count - n) + " 本。");
            SaveReport(dest.Text);
        }

        bool ConfirmDeleteDialog(List<AlbumRow> picks) {
            using (var f = new Form { Text = "確認刪除 LINE 相簿", FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, StartPosition = FormStartPosition.CenterParent, BackColor = Theme.Card, ForeColor = Theme.Text, Font = Font, ClientSize = new Size(P(460), P(380)) }) {
                var warn = new Label { Dock = DockStyle.Top, Height = P(62), ForeColor = Theme.Red, Padding = new Padding(P(12), P(10), P(12), 0), Font = new Font(Font, FontStyle.Bold),
                    Text = "以下 " + picks.Count + " 本相簿會從 LINE 群組刪除，群組裡所有人都看不到，而且救不回來。\n每本刪除前會再打開核對一次，張數不符就不刪。" };
                var list = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BackColor = Theme.Field, ForeColor = Theme.Text, BorderStyle = BorderStyle.None,
                    Text = string.Join(Environment.NewLine, picks.Select(p => "・" + p.Name + "　（" + p.Expected + " 張，資料夾 " + p.Actual + " 個檔）")) };
                var listHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(P(12), P(4), P(12), P(4)) }; listHost.Controls.Add(list);
                var bottom = new Panel { Dock = DockStyle.Bottom, Height = P(84), Padding = new Padding(P(12), P(4), P(12), P(8)) };
                var ask = new Label { Text = "確定要刪除的話，請在下面輸入「刪除」：", AutoSize = true, Location = new Point(P(12), P(4)), ForeColor = Theme.Muted };
                var input = new TextBox { Location = new Point(P(12), P(26)), Width = P(150), BackColor = Theme.Field, ForeColor = Theme.Text };
                var ok = new Btn { Text = "刪除", Danger = true, Enabled = false, AutoSize = true, Padding = new Padding(P(14), P(2), P(14), P(2)), DialogResult = DialogResult.OK, ForeColor = Theme.Text };
                var cancel = new Btn { Text = "取消", AutoSize = true, Padding = new Padding(P(14), P(2), P(14), P(2)), DialogResult = DialogResult.Cancel, ForeColor = Theme.Text };
                input.TextChanged += (s, e) => ok.Enabled = input.Text.Trim() == "刪除";
                bottom.Controls.Add(ask); bottom.Controls.Add(input); bottom.Controls.Add(ok); bottom.Controls.Add(cancel);
                bottom.Layout += (s, e) => { cancel.Location = new Point(bottom.Width - cancel.Width - P(12), P(24)); ok.Location = new Point(cancel.Left - ok.Width - P(8), P(24)); };
                f.Controls.Add(listHost); f.Controls.Add(bottom); f.Controls.Add(warn);
                f.CancelButton = cancel;
                f.Shown += (s, e) => input.Focus();
                return f.ShowDialog(this) == DialogResult.OK && input.Text.Trim() == "刪除";
            }
        }

        void SetAll(bool on) {
            foreach (var r in rows) { r.Selected = on; Report(r); }
            UpdateSummary(); UpdateButtons();
        }

        void Recheck() {
            foreach (var r in rows) {
                if (!Directory.Exists(r.Folder) || r.Status.StartsWith("已刪除")) continue;
                r.Actual = Ui.CountFiles(r.Folder);
                if (r.Expected > 0) r.Status = r.Actual >= r.Expected ? (r.Actual == r.Expected ? "完成 ✔" : "完成（多 " + (r.Actual - r.Expected) + " 個檔）") : "不完整 " + r.Actual + "/" + r.Expected;
                Report(r);
            }
            Log("已重新核對 " + rows.Count + " 本的檔案數。");
            if (rows.Count > 0) SaveReport(dest.Text);
        }

        void SaveReport(string root) {
            if (rows.Count == 0 || !Directory.Exists(root)) return;
            try {
                string path = Path.Combine(root, "LINE相簿下載核對_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".csv");
                var sb = new StringBuilder("序號,相簿名稱,應有張數,實際檔案數,結果,資料夾\r\n");
                foreach (var r in rows) sb.Append(string.Join(",", new object[] { r.No, Txt.Csv(r.Name), r.Expected >= 0 ? r.Expected.ToString() : "", r.Actual, Txt.Csv(r.Status), Txt.Csv(r.Folder) })).Append("\r\n");
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
                Log("核對表已存：" + path);
            } catch (Exception ex) { Log("核對表儲存失敗：" + ex.Message); }
        }

        // 產生介面預覽圖（假資料），用來檢查版面有沒有被切到。
        public static void RenderPreview(string path, int width, int height, bool running) {
            PreviewMode = true;
            using (var f = new MainForm()) {
                f.ShowInTaskbar = false; f.StartPosition = FormStartPosition.Manual; f.Location = new Point(-32000, -32000);
                if (width > 0) f.Size = new Size(width, height);
                f.windows.Items.Add(new LineWindow { Label = "範例群組 相簿", IsOverview = true }); f.windows.SelectedIndex = 0;
                f.SetDest(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "LINE相簿"));
                f.Show(); Application.DoEvents();
                f.Report(new AlbumRow { No = 1, Name = "20260831 員工旅遊", Expected = 18, Actual = 18, Status = "完成 ✔", Folder = @"C:\x\20260831 員工旅遊", Selected = true, Abs = 0 });
                f.Report(new AlbumRow { No = 2, Name = "20260826 聚餐", Expected = 7, Actual = 7, Status = "完成 ✔", Folder = @"C:\x\20260826 聚餐" });
                f.Report(new AlbumRow { No = 3, Name = "20260725 晨會", Expected = 14, Actual = 11, Status = "不完整 11/14", Folder = @"C:\x\20260725 晨會" });
                f.Report(new AlbumRow { No = 4, Name = "2026/10/31 很長很長的相簿名稱測試", Expected = 9, Actual = 9, Status = "已下載（張數相符）", Folder = @"C:\x\20261031 相簿" });
                f.Report(new AlbumRow { No = 6, Name = "20260401 春酒", Expected = 32, Status = "未下載", Selected = true, Abs = 900 });
                f.Report(new AlbumRow { No = 5, Name = "20260517 活動照片", Expected = 24, Actual = 13, Status = "下載中… 13/24", Folder = @"C:\x\20260517 活動照片" });
                f.Log("開始：從目前捲軸位置往下逐本處理。螢幕縮放 150%。"); f.Log("第 1 本：點「20260831 員工旅遊」"); f.Log("　完成 ✔　→ C:\\x\\20260831 員工旅遊");
                if (running) { f.busy = true; f.UpdateButtons(); }
                Application.DoEvents(); f.PerformLayout(); Application.DoEvents();
                using (var b = new Bitmap(f.Width, f.Height)) { f.DrawToBitmap(b, new Rectangle(Point.Empty, f.Size)); b.Save(path, ImageFormat.Png); }
                f.Close();
            }
        }
    }

    static class Program {
        [STAThread]
        static void Main(string[] args) {
            N.SetProcessDPIAware();
            if (args.Length > 0 && args[0] == "--probe") { Probe(args.Length > 1 ? args[1] : "probe.txt"); return; }
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            if (args.Length > 1 && args[0] == "--preview") { try { MainForm.RenderPreview(args[1], args.Length > 3 ? int.Parse(args[2]) : 0, args.Length > 3 ? int.Parse(args[3]) : 0, args.Contains("--running")); } catch (Exception ex) { File.WriteAllText(args[1] + ".err.txt", ex.ToString()); } return; }
            Application.Run(new MainForm());
        }
        // 不點擊任何東西：列出視窗判斷結果，並讀出總覽目前畫面上每本相簿的名稱區。
        static void Probe(string outPath) {
            var o = new StringBuilder();
            try {
                var ocr = new Ocr();
                foreach (var w in Detect.Windows(ocr)) o.AppendLine("[" + w.Kind + "] " + w.Label + (w.IsOverview ? " ← 可選" : ""));
                foreach (var win in Ui.TopWindows("GroupHomeWindow")) {
                    double scale = N.Scale(Ui.Hwnd(win));
                    var list = Ui.First(win, "LcListView"); var bar = Ui.First(win, "LcScrollBar");
                    object p; string sv = "無捲軸";
                    if (bar != null && bar.TryGetCurrentPattern(RangeValuePattern.Pattern, out p)) sv = ((RangeValuePattern)p).Current.Value + " / " + ((RangeValuePattern)p).Current.Maximum;
                    o.AppendLine("總覽 scale=" + scale + " 捲軸=" + sv + " 清單=" + Ui.R(list));
                    foreach (var it in Ui.Rows(list)) {
                        var r = Ui.R(it); int h = (int)(56 * scale); object sc; if (bar != null && bar.TryGetCurrentPattern(RangeValuePattern.Pattern, out sc) && ((RangeValuePattern)sc).Current.SmallChange * scale >= 150 * scale) r.Height = Math.Min(r.Height, (int)Math.Round(((RangeValuePattern)sc).Current.SmallChange * scale));
                        var band = new Rectangle(r.Left + (int)(8 * scale), r.Bottom - h, r.Width - (int)(60 * scale), h - (int)(2 * scale));
                        o.AppendLine("  項目 " + r + " 名稱區 → " + string.Join(" | ", ocr.Read(band).Select(l => Txt.Fix(l.Text) + " @" + (int)l.Box.X + "," + (int)l.Box.Y)));
                    }
                }
                foreach (var win in Ui.TopWindows("AlbumDetailWindow")) {
                    string title; int count; bool ok = Runner.AlbumInfo(win, ocr, out title, out count);
                    o.AppendLine("單本相簿：" + (ok ? title : "（讀不到）") + "　張數解析 = " + count + "　資料夾 = " + Txt.SafeFolder(title));
                }
                foreach (var menu in Ui.TopWindows("LcContextMenu")) {
                    o.AppendLine("選單原文：" + string.Join(" | ", Ui.Find(menu, "LcContextMenuItem").Select(e => e.Current.Name + (Txt.IsSaveAll(e.Current.Name) ? "(★儲存全部)" : ""))));
                    var ocrLines = ocr.Read(Ui.R(menu));
                    o.AppendLine("選單：" + string.Join(" | ", ocrLines.Select(l => Txt.Fix(l.Text) + (Txt.IsSaveAll(l.Text) ? "(★儲存全部)" : ""))));
                }
                // 其他 LINE 視窗（例如刪除確認視窗）：類別、按鈕原文
                foreach (var w in Ui.TopWindows()) {
                    string cls = ""; try { cls = w.Current.ClassName; } catch { }
                    if (new[] { "GroupHomeWindow", "AlbumDetailWindow", "LcContextMenu", "ChatWindow", "AllInOneWindow" }.Contains(cls)) continue;
                    var btns = new List<string>(); try { foreach (AutomationElement b in w.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button))) btns.Add(b.Current.Name + (Txt.IsConfirmWord(b.Current.Name) ? "(★確認)" : "")); } catch { }
                    o.AppendLine("其他視窗［" + cls + "］文字：" + ocr.Text(Ui.R(w)) + "｜按鈕：" + string.Join(" | ", btns));
                }
            } catch (Exception ex) { o.AppendLine("ERROR " + ex); }
            File.WriteAllText(outPath, o.ToString(), new UTF8Encoding(true));
        }
    }
}
