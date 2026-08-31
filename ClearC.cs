using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Reflection;
using VBIO = Microsoft.VisualBasic.FileIO;

[assembly: AssemblyTitle("ClearC")]
[assembly: AssemblyDescription("C盘清理工具")]
[assembly: AssemblyCompany("zsqstudio")]
[assembly: AssemblyProduct("ClearC C盘清理工具")]
[assembly: AssemblyCopyright("Copyright © zsqstudio   联系: 11016795@qq.com")]
[assembly: AssemblyVersion("1.1.0.0")]
[assembly: AssemblyFileVersion("1.1.0.0")]

namespace Cleaner
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

    // ======================= 工具类 =======================
    static class Util
    {
        public static string SizeStr(long b)
        {
            if (b <= 0) return "0 B";
            if (b >= 1L << 30) return (b / (double)(1L << 30)).ToString("0.00") + " GB";
            if (b >= 1L << 20) return (b / (double)(1L << 20)).ToString("0.0") + " MB";
            if (b >= 1L << 10) return (b / (double)(1L << 10)).ToString("0.0") + " KB";
            return b + " B";
        }

        public static bool IsAdmin()
        {
            try
            {
                using (WindowsIdentity wi = WindowsIdentity.GetCurrent())
                {
                    WindowsPrincipal wp = new WindowsPrincipal(wi);
                    return wp.IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
            catch { return false; }
        }

        public static void AppendLog(string line)
        {
            try
            {
                File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "clean_log.txt"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + line + Environment.NewLine);
            }
            catch { }
        }

        public static long DirSize(string path)
        {
            try { if (File.Exists(path)) { try { return new FileInfo(path).Length; } catch { return 0; } } }
            catch { return 0; }
            long sum = 0;
            try
            {
                foreach (string f in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
                    try { sum += new FileInfo(f).Length; } catch { }
            }
            catch { }
            return sum;
        }

        // 迭代遍历文件, 跳过符号链接/junction 防止死循环, 无权限目录静默跳过
        // progress: 遍历期间的进度回调(400ms 节流), 防止大范围扫描时状态栏长时间无反馈像"卡死"
        public static void EnumFiles(string root, long minBytes, List<string> outFiles, Action<string> progress)
        {
            Stack<string> st = new Stack<string>();
            st.Push(root);
            int lastTick = Environment.TickCount;
            while (st.Count > 0)
            {
                string dir = st.Pop();
                if (progress != null && (unchecked(Environment.TickCount - lastTick)) > 400)
                {
                    lastTick = Environment.TickCount;
                    progress(outFiles.Count + " 个文件 | " + dir);
                }
                string[] files = null;
                try { files = Directory.GetFiles(dir); } catch { }
                if (files != null)
                {
                    foreach (string f in files)
                    {
                        try
                        {
                            FileInfo fi = new FileInfo(f);
                            if (fi.Length >= minBytes) outFiles.Add(f);
                        }
                        catch { }
                    }
                }
                string[] dirs = null;
                try { dirs = Directory.GetDirectories(dir); } catch { }
                if (dirs != null)
                {
                    foreach (string d in dirs)
                    {
                        try { if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0) continue; }
                        catch { }
                        st.Push(d);
                    }
                }
            }
        }

        // 在 root 下(限定深度)找出名字像缓存的子目录: cache/temp/tmp/log/logs/crash
        public static void FindCacheDirs(string root, int maxDepth, List<string> result)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return;
            List<string> level = new List<string>();
            level.Add(root);
            for (int depth = 0; depth <= maxDepth; depth++)
            {
                List<string> next = new List<string>();
                foreach (string d in level)
                {
                    string[] subs = null;
                    try { subs = Directory.GetDirectories(d); } catch { }
                    if (subs == null) continue;
                    foreach (string s in subs)
                    {
                        try { if ((File.GetAttributes(s) & FileAttributes.ReparsePoint) != 0) continue; }
                        catch { }
                        string nm = Path.GetFileName(s).ToLowerInvariant();
                        bool cacheLike = nm.Contains("cache") || nm == "temp" || nm == "tmp"
                                      || nm == "logs" || nm == "log" || nm == "crash";
                        if (cacheLike) result.Add(s);   // 命中后不再下探, 避免父子重复计数
                        else next.Add(s);
                    }
                }
                level = next;
                if (level.Count == 0) break;
            }
        }

        public static string HashHead(string path, int bytes)
        {
            try
            {
                using (FileStream fs = File.OpenRead(path))
                {
                    byte[] buf = new byte[bytes];
                    int n = fs.Read(buf, 0, bytes);
                    if (n <= 0) return "empty";
                    using (MD5 md5 = MD5.Create())
                        return Convert.ToBase64String(md5.ComputeHash(buf, 0, n));
                }
            }
            catch { return null; }
        }

        public static string HashFull(string path)
        {
            try
            {
                using (FileStream fs = File.OpenRead(path))
                using (MD5 md5 = MD5.Create())
                    return Convert.ToBase64String(md5.ComputeHash(fs));
            }
            catch { return null; }
        }

        // 删除到回收站, 成功返回 true
        public static bool RecycleFile(string p)
        {
            try
            {
                VBIO.FileSystem.DeleteFile(p, VBIO.UIOption.OnlyErrorDialogs, VBIO.RecycleOption.SendToRecycleBin);
                return true;
            }
            catch { return false; }
        }

        // 简易通配符匹配 (* 和 ?), 两个参数都应已转小写
        public static bool WildMatch(string input, string pattern)
        {
            int i = 0, j = 0, star = -1, mark = -1;
            while (i < input.Length)
            {
                if (j < pattern.Length && (pattern[j] == '?' || pattern[j] == input[i])) { i++; j++; }
                else if (j < pattern.Length && pattern[j] == '*') { star = j++; mark = i; }
                else if (star >= 0) { j = star + 1; i = ++mark; }
                else return false;
            }
            while (j < pattern.Length && pattern[j] == '*') j++;
            return j == pattern.Length;
        }
    }

    // ======================= 数据结构 =======================
    internal class CleanItem
    {
        public string Level;      // "安全" / "需确认"
        public string Category;
        public string Name;
        public List<string> Targets = new List<string>();   // 整体删除的目标(文件或目录)
        public List<string> Files = new List<string>();     // 按日期过滤后逐个删除的文件
        public string AgeDir;                                // 按日期过滤的目录(如 WPS 备份)
        public int AgeDays;
        public long Size;
        public long Freed;
        public bool Skip;

        public string DisplayLine
        {
            get { return "[" + Level + "][" + Category + "]  " + Name + "    >>>    " + Util.SizeStr(Size); }
        }
    }

    internal class FileEntry
    {
        public string Path;
        public long Size;
        public DateTime MTime;
    }

    // ======================= 主窗体 =======================
    internal class MainForm : Form
    {
        // 共用
        private TabControl tabs;
        private Label lblStatus;
        private TextBox txtLog;
        private bool working = false;

        // Tab1 缓存清理
        private CheckedListBox clb;
        private Button btnSafe, btnAll2, btnNone2, btnRefresh, btnClean;
        private Label lblSelected, lblNote, lblAge1, lblAge2;
        private NumericUpDown numAge;
        private List<CleanItem> items = new List<CleanItem>();

        // Tab2 重复文件
        private TextBox txtDupPath;
        private ComboBox cboDupScope;
        private NumericUpDown numDupMin;
        private Button btnDupBrowse, btnDupScan, btnApply, btnDupNone, btnDupDelete;
        private Label lblDupInfo;
        private ComboBox cboStrategy;
        private ListView lvDup;

        // Tab3 批处理
        private TextBox txtBFolder, txtBPattern;
        private ComboBox cboBScope;
        private NumericUpDown numBMin;
        private ComboBox cboBDate;
        private Button btnBBrowse, btnBSearch, btnBAll, btnBDel, btnBMove;
        private Label lblBInfo;
        private CheckedListBox clbBatch;
        private List<FileEntry> batchFiles = new List<FileEntry>();

        // Tab4 大文件TOP
        private TextBox txtTopPath;
        private ComboBox cboTopScope;
        private Button btnTopBrowse, btnTopScan, btnOpenLoc;
        private Label lblTopInfo;
        private ListView lvTop;

        public MainForm()
        {
            Text = "C盘清理工具 ClearC v1.1   © zsqstudio   |   11016795@qq.com";
            Width = 936; Height = 760;
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;

            lblStatus = new Label { Left = 12, Top = 10, Width = 900, Height = 20, Text = "准备扫描...", Font = F(9) };

            tabs = new TabControl { Left = 12, Top = 34, Width = 904, Height = 496, Font = F(9) };
            BuildCleanTab();
            BuildDupTab();
            BuildBatchTab();
            BuildTopTab();
            BuildAboutTab();

            txtLog = new TextBox
            {
                Left = 12, Top = 538, Width = 904, Height = 170, Multiline = true,
                ScrollBars = ScrollBars.Vertical, ReadOnly = true,
                Font = new Font("Consolas", 9F), BackColor = Color.Black, ForeColor = Color.LightGreen
            };

            Controls.AddRange(new Control[] { lblStatus, tabs, txtLog });

            Load += delegate
            {
                Log("ClearC v1.1.0   Copyright (c) zsqstudio   联系: 11016795@qq.com");
                Log("模块: 缓存清理 / 重复文件对比 / 文件批处理 / 大文件TOP");
                if (!Util.IsAdmin())
                    Log("[提示] 当前未以管理员身份运行, Windows更新缓存/系统Temp 等项目将自动跳过");
                Scan();
            };
        }

        private Font F(float s) { return new Font("Microsoft YaHei", s); }
        private Button Btn(string t, int l, int tp, int w, Action a)
        {
            Button b = new Button { Text = t, Left = l, Top = tp, Width = w, Height = 28, Font = F(9) };
            b.Click += delegate { a(); };
            return b;
        }
        private Label Lbl(string t, int l, int tp, int w)
        {
            return new Label { Text = t, Left = l, Top = tp, Width = w, Height = 20, Font = F(9) };
        }

        private void Log(string m)
        {
            if (InvokeRequired) { BeginInvoke(new Action<string>(Log), m); return; }
            txtLog.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + m + Environment.NewLine);
        }
        private void SetStatus(string s)
        {
            if (InvokeRequired) { BeginInvoke(new Action<string>(SetStatus), s); return; }
            lblStatus.Text = s;
        }

        private void SetBusy(bool on)
        {
            if (InvokeRequired) { BeginInvoke(new Action<bool>(SetBusy), on); return; }
            working = on;
            // 注意: 各"扫描/搜索"按钮不再禁用 —— 忙碌时由 BusyGuard() 弹窗提示,
            // 否则启动扫描期间按钮灰掉无任何反馈, 用户会以为"按钮无效"
            btnClean.Enabled = !on; btnSafe.Enabled = !on; btnAll2.Enabled = !on; btnNone2.Enabled = !on;
            btnApply.Enabled = !on; btnDupDelete.Enabled = !on;
            btnBDel.Enabled = !on; btnBMove.Enabled = !on;
        }

        // ================== 扫描范围选择 (整个电脑/各盘/用户目录/自定义) ==================
        private const string SCOPE_ALL = "\x01ALL";       // 整个电脑
        private const string SCOPE_CUSTOM = "\x01CUSTOM"; // 自定义路径
        private bool suppressScope = false;

        private List<string> AllFixedDrives()
        {
            List<string> ds = new List<string>();
            foreach (DriveInfo di in DriveInfo.GetDrives())
            {
                try { if (di.DriveType == DriveType.Fixed && di.IsReady) ds.Add(di.RootDirectory.FullName); }
                catch { }
            }
            return ds;
        }

        // 生成"扫描范围"下拉框, 选中某项后由调用方挂事件自动启动扫描; defTag 为默认选中项
        private ComboBox ScopeCombo(string defTag)
        {
            ComboBox c = new ComboBox { Left = 50, Top = 8, Width = 190, DropDownStyle = ComboBoxStyle.DropDownList, Font = F(9) };
            List<string> tags = new List<string>();
            c.Items.Add("整个电脑 (所有硬盘)"); tags.Add(SCOPE_ALL);
            foreach (string d in AllFixedDrives())
            {
                string lbl = "本地磁盘";
                try { string v = new DriveInfo(d).VolumeLabel; if (!string.IsNullOrEmpty(v)) lbl = v; } catch { }
                if (lbl.Length > 8) lbl = lbl.Substring(0, 8);
                c.Items.Add(d + " " + lbl); tags.Add(d);
            }
            string up = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            c.Items.Add("用户目录"); tags.Add(up);
            string dl = Path.Combine(up, "Downloads");
            if (Directory.Exists(dl)) { c.Items.Add("下载目录"); tags.Add(dl); }
            c.Items.Add("自定义路径..."); tags.Add(SCOPE_CUSTOM);
            c.Tag = tags;
            int idx = tags.IndexOf(defTag);
            if (idx < 0) idx = Math.Min(1, tags.Count - 2);   // 默认选第一块硬盘
            c.SelectedIndex = idx;
            return c;
        }

        // 范围下拉变化: 解析路径写入文本框并立即启动扫描 ("选范围即启动")
        private void OnScopeChanged(ComboBox cbo, TextBox txt, Action startScan)
        {
            if (suppressScope) return;
            List<string> tags = cbo.Tag as List<string>;
            int i = cbo.SelectedIndex;
            if (tags == null || i < 0 || i >= tags.Count) return;
            string tag = tags[i];
            if (tag == SCOPE_CUSTOM)
            {
                using (FolderBrowserDialog fd = new FolderBrowserDialog())
                {
                    fd.Description = "选择要扫描的文件夹";
                    if (fd.ShowDialog() != DialogResult.OK) return;
                    suppressScope = true; txt.Text = fd.SelectedPath; suppressScope = false;
                }
            }
            else
            {
                suppressScope = true;
                txt.Text = tag == SCOPE_ALL ? "(整个电脑: 所有本地硬盘)" : tag;
                suppressScope = false;
            }
            startScan();
        }

        // 解析当前要扫描的根目录列表(多个=整个电脑)
        private List<string> GetRoots(ComboBox cbo, TextBox txt)
        {
            List<string> tags = cbo.Tag as List<string>;
            int i = cbo.SelectedIndex;
            if (tags != null && i >= 0 && i < tags.Count)
            {
                string tag = tags[i];
                if (tag == SCOPE_ALL) return AllFixedDrives();
                if (tag == SCOPE_CUSTOM)
                {
                    string p = txt.Text.Trim();
                    return Directory.Exists(p) ? new List<string> { p } : new List<string>();
                }
                return new List<string> { tag };
            }
            string q = txt.Text.Trim();
            return Directory.Exists(q) ? new List<string> { q } : new List<string>();
        }

        // 手动修改路径文本 → 下拉框切到"自定义路径..."(不再反向覆盖)
        private void WireCustomPath(TextBox txt, ComboBox cbo)
        {
            txt.TextChanged += delegate
            {
                if (suppressScope) return;
                suppressScope = true;
                cbo.SelectedIndex = cbo.Items.Count - 1;   // 最后一项固定是"自定义路径..."
                suppressScope = false;
            };
        }

        // 忙碌时明确提示, 而不是静默忽略(否则像"按钮无效")
        private bool BusyGuard()
        {
            if (!working) return false;
            MessageBox.Show("正在执行其他任务，请等待它完成后再试。\n进度见底部状态栏。", "ClearC");
            return true;
        }

        // ============================================================
        // Tab1 : 缓存清理
        // ============================================================
        private void BuildCleanTab()
        {
            TabPage tp = new TabPage(" 缓存清理 ");
            clb = new CheckedListBox
            {
                Left = 10, Top = 8, Width = 872, Height = 300,
                Font = new Font("Consolas", 9F), CheckOnClick = true, HorizontalScrollbar = true
            };
            clb.ItemCheck += delegate { BeginInvoke(new Action(UpdateSelected)); };

            btnSafe = Btn("仅勾选安全项", 10, 316, 110, delegate { SetLevelCheck(true); });
            btnAll2 = Btn("全选(含需确认)", 126, 316, 116, delegate { SetAll(true); });
            btnNone2 = Btn("取消全选", 248, 316, 82, delegate { SetAll(false); });
            btnRefresh = Btn("重新扫描", 336, 316, 96, delegate { Scan(); });

            lblAge1 = Lbl("WPS备份只清", 442, 320, 76);
            numAge = new NumericUpDown { Left = 518, Top = 317, Width = 48, Minimum = 1, Maximum = 3650, Value = 30, Font = F(9) };
            lblAge2 = Lbl("天前的", 568, 320, 48);

            lblSelected = Lbl("已选: 0 B", 10, 350, 700);
            lblNote = new Label
            {
                Left = 10, Top = 372, Width = 872, Height = 34, Font = F(8.5F), ForeColor = Color.DimGray,
                Text = "· [安全]级已自动勾选, 删除后软件按需重新生成; [需确认]级涉及个人数据(微信/WPS/网盘等), 默认不勾选, 请自行确认后再选"
                    + "\r\n· 本页删除为永久删除(回收站也占C盘); \"重复文件/批处理\"页的删除才会进回收站"
            };

            btnClean = new Button
            {
                Text = ">>  开始清理选中项 (直接删除, 立即释放空间)", Left = 10, Top = 410, Width = 872, Height = 32,
                Font = new Font("Microsoft YaHei", 9.5F, FontStyle.Bold), BackColor = Color.Khaki
            };
            btnClean.Click += delegate { StartClean(); };

            tp.Controls.AddRange(new Control[] { clb, btnSafe, btnAll2, btnNone2, btnRefresh, lblAge1, numAge, lblAge2, lblSelected, lblNote, btnClean });
            tabs.TabPages.Add(tp);
        }

        private void UpdateSelected()
        {
            long sum = 0; int n = 0;
            for (int i = 0; i < items.Count; i++)
                if (clb.GetItemChecked(i)) { sum += items[i].Size; n++; }
            lblSelected.Text = "已选 " + n + " 项, 合计: " + Util.SizeStr(sum);
        }

        private void SetAll(bool on)
        {
            for (int i = 0; i < items.Count; i++) clb.SetItemChecked(i, on);
            UpdateSelected();
        }

        private void SetLevelCheck(bool safeOnly)
        {
            for (int i = 0; i < items.Count; i++)
                clb.SetItemChecked(i, safeOnly ? items[i].Level == "安全" : false);
            UpdateSelected();
        }

        // ---- 清理规则清单 ----
        private void Add(string level, string cat, string name, params string[] targets)
        {
            CleanItem it = new CleanItem();
            it.Level = level; it.Category = cat; it.Name = name;
            foreach (string t in targets)
                if (!string.IsNullOrEmpty(t) && (File.Exists(t) || Directory.Exists(t))) it.Targets.Add(t);
            if (it.Targets.Count > 0) items.Add(it);
        }

        private void AddAged(string level, string cat, string name, string dir, int ageDays)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            CleanItem it = new CleanItem();
            it.Level = level; it.Category = cat; it.Name = name;
            it.AgeDir = dir; it.AgeDays = ageDays;
            items.Add(it);
        }

        private void AddCacheDirs(string level, string cat, string name, int depth, params string[] roots)
        {
            List<string> found = new List<string>();
            foreach (string r in roots) Util.FindCacheDirs(r, depth, found);
            if (found.Count == 0) return;
            CleanItem it = new CleanItem();
            it.Level = level; it.Category = cat;
            it.Name = name + " (cache子目录 x" + found.Count + ")";
            it.Targets = found;
            items.Add(it);
        }

        private void BuildItems()
        {
            items.Clear();
            string temp = Path.GetTempPath();
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string roam = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string winTemp = @"C:\Windows\Temp";

            // ============ 安全级 ============
            Add("安全", "系统临时", "Windows诊断输出 DiagOutputDir", winTemp + @"\DiagOutputDir");
            Add("安全", "系统临时", "安全软件诊断 Whesvc", winTemp + @"\Whesvc");
            List<string> wOthers = new List<string>();
            try
            {
                foreach (string f in Directory.GetFileSystemEntries(winTemp))
                {
                    string nm = Path.GetFileName(f);
                    if (nm == "DiagOutputDir" || nm == "Whesvc") continue;
                    wOthers.Add(f);
                }
            }
            catch { }
            Add("安全", "系统临时", "Windows Temp 其他残留(log等)", wOthers.ToArray());

            // 用户 Temp 指定项
            Add("安全", "用户临时", "迅雷安装缓存 ThunderInstall", temp + @"\ThunderInstall");
            Add("安全", "用户临时", "迅雷直播 XLLiveUD+ThunderEv", temp + @"\XLLiveUD", temp + @"\ThunderEv");
            List<string> nodeTmp = new List<string>();
            try { foreach (string f in Directory.GetFiles(temp, "*.tmp.node")) nodeTmp.Add(f); } catch { }
            Add("安全", "用户临时", "Node原生模块残留 (*.tmp.node x" + nodeTmp.Count + ")", nodeTmp.ToArray());
            Add("安全", "用户临时", "百度云管家缓存", temp + @"\baiduyunguanjia");
            Add("安全", "用户临时", "在线安装包残留 OnlineInstall", temp + @"\OnlineInstall");
            List<string> dep = new List<string>();
            try { foreach (string f in Directory.GetFiles(temp, "*.tar.gz")) dep.Add(f); } catch { }
            try { foreach (string f in Directory.GetFiles(temp, "*.7z")) dep.Add(f); } catch { }
            try { foreach (string f in Directory.GetFiles(temp, "*.zip")) dep.Add(f); } catch { }
            Add("安全", "用户临时", "部署/压缩包残留(tar.gz/7z/zip)", dep.ToArray());

            // 用户 Temp 7天前残留
            List<string> tmpOld = new List<string>();
            try
            {
                DateTime cutoff = DateTime.Now.AddDays(-7);
                foreach (string f in Directory.GetFileSystemEntries(temp))
                {
                    try { if (Directory.GetLastWriteTime(f) < cutoff) tmpOld.Add(f); } catch { }
                }
            }
            catch { }
            Add("安全", "用户临时", "用户Temp 7天前残留(占用中自动跳过)", tmpOld.ToArray());

            // 更新残留: 所有 *updater* 目录下的 installer.exe / pending
            List<string> upd = new List<string>();
            try
            {
                foreach (string d in Directory.GetDirectories(local, "*updater*"))
                {
                    string ins = Path.Combine(d, "installer.exe");
                    if (File.Exists(ins)) upd.Add(ins);
                    string pend = Path.Combine(d, "pending");
                    if (Directory.Exists(pend)) upd.Add(pend);
                }
            }
            catch { }
            Add("安全", "更新残留", "各软件更新安装包残留 (x" + (upd.Count) + ")", upd.ToArray());

            // 开发缓存
            Add("安全", "开发缓存", "npm 全局缓存 npm-cache", local + @"\npm-cache");
            Add("安全", "开发缓存", "pip 缓存", local + @"\pip\cache");
            Add("安全", "开发缓存", "Go 模块下载缓存", home + @"\go\pkg\mod\cache");
            Add("安全", "开发缓存", "Go 构建缓存 go-build", local + @"\go-build");
            Add("安全", "开发缓存", "Gradle 缓存 .gradle\\caches", home + @"\.gradle\caches");
            Add("安全", "开发缓存", "Playwright 浏览器缓存(下次自动重下)", local + @"\ms-playwright");
            List<string> jb = new List<string>();
            try
            {
                foreach (string baseDir in new string[] { Path.Combine(local, "JetBrains"), Path.Combine(roam, "JetBrains") })
                    if (Directory.Exists(baseDir))
                        foreach (string prod in Directory.GetDirectories(baseDir))
                        {
                            string c1 = Path.Combine(prod, "caches");
                            if (Directory.Exists(c1)) jb.Add(c1);
                            string c2 = Path.Combine(prod, "log");
                            if (Directory.Exists(c2)) jb.Add(c2);
                        }
            }
            catch { }
            Add("安全", "开发缓存", "JetBrains 旧缓存(x" + jb.Count + ")", jb.ToArray());

            // 系统/显卡缓存
            Add("安全", "系统缓存", "崩溃转储 CrashDumps", local + @"\CrashDumps");
            Add("安全", "系统缓存", "DirectX 着色器缓存", local + @"\D3DSCache", local + @"\NVIDIA\DXCache", local + @"\NVIDIA\GLCache");
            List<string> thumbs = new List<string>();
            try
            {
                foreach (string f in Directory.GetFiles(local + @"\Microsoft\Windows\Explorer", "thumbcache_*.db")) thumbs.Add(f);
            }
            catch { }
            Add("安全", "系统缓存", "资源管理器缩略图缓存(x" + thumbs.Count + ")", thumbs.ToArray());
            Add("安全", "系统缓存", "Windows 错误报告 WER", @"C:\ProgramData\Microsoft\Windows\WER\ReportArchive", @"C:\ProgramData\Microsoft\Windows\WER\ReportQueue");
            Add("安全", "系统缓存", "Windows 更新下载缓存(需管理员)", @"C:\Windows\SoftwareDistribution\Download");

            // 浏览器缓存: Chrome / Edge / Firefox
            AddBrowserCache("Chrome", local + @"\Google\Chrome\User Data");
            AddBrowserCache("Edge", local + @"\Microsoft\Edge\User Data");
            List<string> ff = new List<string>();
            try
            {
                foreach (string d in Directory.GetDirectories(local + @"\Mozilla\Firefox\Profiles"))
                { string c2 = Path.Combine(d, "cache2"); if (Directory.Exists(c2)) ff.Add(c2); }
            }
            catch { }
            Add("安全", "浏览器缓存", "Firefox 缓存 (运行中将跳过)", ff.ToArray());

            // ============ 需确认级 ============
            AddAged("需确认", "WPS", "WPS 自动备份 (只清 " + ((int)numAge.Value) + " 天前, 可在上方调整)",
                Path.Combine(roam, @"kingsoft\office6\backup"), (int)numAge.Value);
            Add("需确认", "WPS", "WPS 稻壳模板/插件缓存 addons", Path.Combine(roam, @"kingsoft\wps\addons"));
            AddCacheDirs("需确认", "WPS", "WPS 本地缓存", 3, Path.Combine(local, "Kingsoft"));
            AddCacheDirs("需确认", "网盘", "百度网盘缓存", 4, Path.Combine(roam, @"baidu\BaiduNetdisk"));
            AddCacheDirs("需确认", "多媒体", "剪映缓存(不含草稿)", 4, Path.Combine(local, "JianyingPro"));

            // 聊天软件: 只动名字含 cache/temp/log 的子目录, 不碰聊天图片/视频/文件
            List<string> wxOld = new List<string>();
            try
            {
                string wroot = Path.Combine(docs, "WeChat Files");
                if (Directory.Exists(wroot))
                    foreach (string u in Directory.GetDirectories(wroot))
                    {
                        string c = Path.Combine(u, @"FileStorage\Cache");
                        if (Directory.Exists(c)) wxOld.Add(c);
                    }
            }
            catch { }
            Util.FindCacheDirs(Path.Combine(roam, @"Tencent\WeChat"), 2, wxOld);
            Add("需确认", "聊天软件", "微信(旧版)缓存 (不含聊天图片视频)", wxOld.ToArray());
            AddCacheDirs("需确认", "聊天软件", "微信(新版)缓存", 3, Path.Combine(home, "xwechat_files"));
            AddCacheDirs("需确认", "聊天软件", "企业微信缓存", 3,
                Path.Combine(docs, "WXWorkLocal"), Path.Combine(docs, "WXWorkLocal_data"), Path.Combine(roam, @"Tencent\WXWorkLocal"));
            AddCacheDirs("需确认", "聊天软件", "QQ 缓存", 3, Path.Combine(roam, @"Tencent\QQ"));
            AddCacheDirs("需确认", "AI助手", "腾讯ima/千问 缓存", 3, Path.Combine(local, "ima.copilot"), Path.Combine(local, "Qianwen"));
            AddCacheDirs("需确认", "下载工具", "迅雷缓存", 3, @"C:\ProgramData\Thunder Network");
        }

        private void AddBrowserCache(string browser, string userDataDir)
        {
            List<string> list = new List<string>();
            try
            {
                foreach (string d in Directory.GetDirectories(userDataDir))
                {
                    string nm = Path.GetFileName(d);
                    if (nm == "System Profile" || nm == "LostFound") continue;
                    foreach (string sub in new string[] { "Cache", "Code Cache", "GPUCache" })
                    { string p = Path.Combine(d, sub); if (Directory.Exists(p)) list.Add(p); }
                    string sw = Path.Combine(d, "Service Worker");
                    if (Directory.Exists(sw))
                        foreach (string sub in new string[] { "CacheStorage", "ScriptCache" })
                        { string p = Path.Combine(sw, sub); if (Directory.Exists(p)) list.Add(p); }
                }
            }
            catch { }
            Add("安全", "浏览器缓存", browser + " 缓存 (运行中将跳过)", list.ToArray());
        }

        // CheckedListBox 与 ListBox 不同: HorizontalScrollbar=true 不会自动测量条目宽度,
        // 必须手动设置 HorizontalExtent, 否则长条目右侧大小文字被裁断
        private void FitClbWidth(CheckedListBox box)
        {
            int max = 0;
            foreach (object o in box.Items)
            {
                int w = TextRenderer.MeasureText(Convert.ToString(o), box.Font).Width;
                if (w > max) max = w;
            }
            if (max > 0) box.HorizontalExtent = max + 45;   // 补上复选框与边距
        }

        // ---- 扫描 ----
        private void Scan()
        {
            if (BusyGuard()) return;
            SetBusy(true);
            SetStatus("正在扫描...");
            Log("开始扫描可清理项目...");
            clb.Items.Clear();
            BuildItems();
            foreach (CleanItem it in items) clb.Items.Add(it.DisplayLine, false);
            FitClbWidth(clb);
            UpdateSelected();
            SetStatus("扫描中... 0/" + items.Count);

            List<CleanItem> snapshot = new List<CleanItem>(items);
            Task.Run(delegate
            {
                try
                {
                for (int i = 0; i < snapshot.Count; i++)
                {
                    int idx = i;
                    CleanItem it = snapshot[i];
                    long sz = 0;
                    if (it.AgeDir != null)
                    {
                        DateTime cutoff = DateTime.Now.AddDays(-it.AgeDays);
                        try
                        {
                            foreach (string f in Directory.GetFiles(it.AgeDir))
                            {
                                try
                                {
                                    FileInfo fi = new FileInfo(f);
                                    if (fi.LastWriteTime < cutoff) { it.Files.Add(f); sz += fi.Length; }
                                }
                                catch { }
                            }
                        }
                        catch { }
                    }
                    else
                    {
                        foreach (string t in it.Targets) sz += Util.DirSize(t);
                    }
                    it.Size = sz;
                    BeginInvoke(new Action(delegate
                    {
                        if (idx < clb.Items.Count)
                        {
                            clb.Items[idx] = it.DisplayLine;
                            if (sz > 0 && it.Level == "安全") clb.SetItemChecked(idx, true);
                            FitClbWidth(clb);
                        }
                        UpdateSelected();
                    }));
                    SetStatus("扫描中... " + (idx + 1) + "/" + snapshot.Count);
                }
                long total = 0;
                foreach (CleanItem it in snapshot) total += it.Size;
                SetStatus("扫描完成: " + snapshot.Count + " 项, 可清理总量 " + Util.SizeStr(total) + "  (安全级已自动勾选)");
                Log("扫描完成: " + snapshot.Count + " 项, 可清理总量 " + Util.SizeStr(total));
                }
                catch (Exception ex) { Log("扫描出错: " + ex.Message); }
                finally { BeginInvoke(new Action(delegate { SetBusy(false); })); }   // 兜底: 任何异常都不允许 working 卡在 true
            });
        }

        // ---- 清理 ----
        private void StartClean()
        {
            if (BusyGuard()) return;
            List<CleanItem> todo = new List<CleanItem>();
            for (int i = 0; i < items.Count; i++)
                if (clb.GetItemChecked(i) && items[i].Size > 0) todo.Add(items[i]);
            if (todo.Count == 0) { MessageBox.Show("没有勾选任何可清理项目。"); return; }

            long est = 0, szSafe = 0, szWarn = 0;
            int nSafe = 0, nWarn = 0;
            List<string> warnNames = new List<string>();
            foreach (CleanItem it in todo)
            {
                est += it.Size;
                if (it.Level == "安全") { nSafe++; szSafe += it.Size; }
                else { nWarn++; szWarn += it.Size; warnNames.Add(it.Name + "  (" + Util.SizeStr(it.Size) + ")"); }
            }

            string msg = "确认清理 " + todo.Count + " 项, 预计释放 " + Util.SizeStr(est) + " ?\r\n\r\n"
                + "安全级 " + nSafe + " 项 (约 " + Util.SizeStr(szSafe) + ")\r\n"
                + "需确认级 " + nWarn + " 项 (约 " + Util.SizeStr(szWarn) + ")";
            if (warnNames.Count > 0)
                msg += "\r\n\r\n需确认级包含:\r\n  · " + string.Join("\r\n  · ", warnNames.ToArray())
                    + "\r\n\r\n⚠ 以上项目含个人数据, 删除后不可恢复!";
            msg += "\r\n\r\n浏览器/聊天软件缓存需先关闭对应程序, 运行中会自动跳过";

            if (MessageBox.Show(msg, "确认清理", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;

            SetBusy(true);
            Log("====== 开始清理, 预计释放 " + Util.SizeStr(est) + " ======");

            bool chromeRun = Process.GetProcessesByName("chrome").Length > 0;
            bool edgeRun = Process.GetProcessesByName("msedge").Length > 0;
            bool ffRun = Process.GetProcessesByName("firefox").Length > 0;

            Task.Run(delegate
            {
                long totalFreed = 0;
                foreach (CleanItem it in todo)
                {
                    if (it.Category == "浏览器缓存")
                    {
                        if (it.Name.Contains("Chrome") && chromeRun) { it.Skip = true; Log("[跳过] Chrome 正在运行, 请关闭后重试"); continue; }
                        if (it.Name.Contains("Edge") && edgeRun) { it.Skip = true; Log("[跳过] Edge 正在运行, 请关闭后重试"); continue; }
                        if (it.Name.Contains("Firefox") && ffRun) { it.Skip = true; Log("[跳过] Firefox 正在运行, 请关闭后重试"); continue; }
                    }
                    long freed = 0;
                    if (it.AgeDir != null)
                    {
                        foreach (string f in it.Files)
                        {
                            try { long s = new FileInfo(f).Length; File.Delete(f); freed += s; }
                            catch { }
                        }
                    }
                    else
                    {
                        foreach (string t in it.Targets) freed += DeleteTarget(t);
                    }
                    it.Freed = freed;
                    totalFreed += freed;
                    Log("[清理] [" + it.Level + "] " + it.Category + " / " + it.Name + "  =>  释放 " + Util.SizeStr(freed) + " / " + Util.SizeStr(it.Size));
                }
                Log("====== 清理完成, 共释放 " + Util.SizeStr(totalFreed) + " ======");
                Util.AppendLog("[缓存清理] " + todo.Count + " 项, 释放 " + Util.SizeStr(totalFreed));
                BeginInvoke(new Action(delegate
                {
                    MessageBox.Show("清理完成!\r\n共释放: " + Util.SizeStr(totalFreed) + "\r\n\r\n将自动重新扫描查看最新状态。", "完成",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    SetBusy(false);
                    Scan();
                }));
            });
        }

        private long DeleteTarget(string path)
        {
            if (string.IsNullOrEmpty(path)) return 0;
            long size = Util.DirSize(path);
            if (size == 0) return 0;
            try
            {
                if (Directory.Exists(path)) { Directory.Delete(path, true); return size; }
                if (File.Exists(path)) { File.Delete(path); return size; }
            }
            catch { return PartialDelete(path); }
            return 0;
        }

        private long PartialDelete(string path)
        {
            long freed = 0;
            if (!Directory.Exists(path)) return 0;
            try
            {
                foreach (string f in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
                    try { long sz = new FileInfo(f).Length; File.Delete(f); freed += sz; } catch { }
                string[] dirs = Directory.GetDirectories(path, "*", SearchOption.AllDirectories);
                Array.Sort(dirs, delegate(string a, string b) { return b.Length.CompareTo(a.Length); });
                foreach (string d in dirs) { try { Directory.Delete(d, true); } catch { } }
                try { Directory.Delete(path, true); } catch { }
            }
            catch { }
            return freed;
        }

        // ============================================================
        // Tab2 : 重复文件对比
        // ============================================================
        private void BuildDupTab()
        {
            TabPage tp = new TabPage(" 重复文件对比 ");

            tp.Controls.Add(Lbl("范围:", 10, 12, 40));
            cboDupScope = ScopeCombo(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            txtDupPath = new TextBox { Left = 244, Top = 8, Width = 262, Font = F(9), Text = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) };
            btnDupBrowse = Btn("浏览...", 510, 6, 64, delegate
            {
                using (FolderBrowserDialog fd = new FolderBrowserDialog())
                {
                    if (fd.ShowDialog() == DialogResult.OK) { txtDupPath.Text = fd.SelectedPath; DupScan(); }
                }
            });
            btnDupScan = Btn("立即扫描", 578, 6, 90, delegate { DupScan(); });
            WireCustomPath(txtDupPath, cboDupScope);
            cboDupScope.SelectedIndexChanged += delegate { OnScopeChanged(cboDupScope, txtDupPath, delegate { DupScan(); }); };
            tp.Controls.Add(Lbl("最小MB:", 676, 12, 52));
            numDupMin = new NumericUpDown { Left = 730, Top = 8, Width = 58, Minimum = 0, Maximum = 4096, Value = 1, Font = F(9) };

            lblDupInfo = Lbl("提示: 上方选择范围即自动开始扫描; 同一内容只保留一份即可, 删除进回收站可恢复", 10, 40, 860);

            lvDup = new ListView
            {
                Left = 10, Top = 62, Width = 872, Height = 302, View = View.Details, CheckBoxes = true,
                FullRowSelect = true, Font = new Font("Consolas", 9F)
            };
            lvDup.Columns.Add("文件名", 200);
            lvDup.Columns.Add("大小", 80);
            lvDup.Columns.Add("修改时间", 130);
            lvDup.Columns.Add("完整路径", 430);

            tp.Controls.Add(Lbl("勾选策略:", 10, 374, 64));
            cboStrategy = new ComboBox { Left = 76, Top = 370, Width = 150, DropDownStyle = ComboBoxStyle.DropDownList, Font = F(9) };
            cboStrategy.Items.Add("保留每组最新");
            cboStrategy.Items.Add("保留每组最旧");
            cboStrategy.Items.Add("保留路径最短");
            cboStrategy.SelectedIndex = 0;
            btnApply = Btn("按策略勾选", 232, 369, 100, delegate { ApplyStrategy(); });
            btnDupNone = Btn("清空勾选", 338, 369, 88, delegate
            {
                foreach (ListViewItem li in lvDup.Items) li.Checked = false;
                DupUpdateInfo();
            });
            btnDupDelete = Btn("删除勾选项 → 回收站", 432, 369, 200, delegate { DupDelete(); });

            tp.Controls.AddRange(new Control[] { cboDupScope, txtDupPath, btnDupBrowse, btnDupScan, numDupMin, lblDupInfo, lvDup, cboStrategy, btnApply, btnDupNone, btnDupDelete });
            tabs.TabPages.Add(tp);
        }

        private void DupScan()
        {
            if (BusyGuard()) return;
            List<string> roots = GetRoots(cboDupScope, txtDupPath);
            if (roots.Count == 0) { MessageBox.Show("扫描范围无效, 请重新选择范围或路径。"); return; }
            string rootDesc = roots.Count == 1 ? roots[0] : "整个电脑 (" + roots.Count + " 个硬盘)";
            long minBytes = (long)(numDupMin.Value * 1024m * 1024m);
            SetBusy(true);
            lvDup.Items.Clear(); lvDup.Groups.Clear();
            lblDupInfo.Text = "扫描中, 请稍候... (范围大时可能需要几分钟)";
            Log("重复文件扫描开始: " + rootDesc + " (最小 " + numDupMin.Value + " MB)");

            Task.Run(delegate
            {
                try
                {
                List<string> paths = new List<string>();
                foreach (string r in roots)
                {
                    SetStatus("重复扫描: 正在遍历 " + r + " ...");
                    Util.EnumFiles(r, minBytes, paths, delegate(string s) { SetStatus("重复扫描: " + s); });
                }
                SetStatus("重复扫描: " + paths.Count + " 个文件, 正在计算分组...");

                List<FileEntry> es = new List<FileEntry>();
                foreach (string p in paths)
                {
                    try
                    {
                        FileInfo fi = new FileInfo(p);
                        FileEntry fe = new FileEntry();
                        fe.Path = p; fe.Size = fi.Length; fe.MTime = fi.LastWriteTime;
                        es.Add(fe);
                    }
                    catch { }
                }

                Dictionary<long, List<FileEntry>> bySize = new Dictionary<long, List<FileEntry>>();
                foreach (FileEntry e in es)
                {
                    List<FileEntry> l;
                    if (!bySize.TryGetValue(e.Size, out l)) { l = new List<FileEntry>(); bySize[e.Size] = l; }
                    l.Add(e);
                }

                List<List<FileEntry>> result = new List<List<FileEntry>>();
                foreach (KeyValuePair<long, List<FileEntry>> kv in bySize)
                {
                    if (kv.Value.Count < 2) continue;
                    SetStatus("重复扫描: 正在校验 " + Util.SizeStr(kv.Key) + " x" + kv.Value.Count + " ...");
                    if (kv.Value.Count == 2)
                    {
                        string h1 = Util.HashFull(kv.Value[0].Path);
                        string h2 = Util.HashFull(kv.Value[1].Path);
                        if (h1 != null && h1 == h2) result.Add(kv.Value);
                        continue;
                    }
                    Dictionary<string, List<FileEntry>> byHead = new Dictionary<string, List<FileEntry>>();
                    foreach (FileEntry e in kv.Value)
                    {
                        string h = Util.HashHead(e.Path, 4096);
                        if (h == null) continue;
                        List<FileEntry> l;
                        if (!byHead.TryGetValue(h, out l)) { l = new List<FileEntry>(); byHead[h] = l; }
                        l.Add(e);
                    }
                    foreach (KeyValuePair<string, List<FileEntry>> hg in byHead)
                    {
                        if (hg.Value.Count < 2) continue;
                        Dictionary<string, List<FileEntry>> byFull = new Dictionary<string, List<FileEntry>>();
                        foreach (FileEntry e in hg.Value)
                        {
                            string h = Util.HashFull(e.Path);
                            if (h == null) continue;
                            List<FileEntry> l;
                            if (!byFull.TryGetValue(h, out l)) { l = new List<FileEntry>(); byFull[h] = l; }
                            l.Add(e);
                        }
                        foreach (KeyValuePair<string, List<FileEntry>> fg in byFull)
                            if (fg.Value.Count >= 2) result.Add(fg.Value);
                    }
                }

                long wasted = 0;
                foreach (List<FileEntry> g in result) wasted += g[0].Size * (g.Count - 1);

                BeginInvoke(new Action(delegate { BuildDupList(result, wasted); }));
                Log("重复扫描完成: " + result.Count + " 组, 可回收约 " + Util.SizeStr(wasted));
                Util.AppendLog("[重复文件] 扫描 " + rootDesc + " => " + result.Count + " 组, 可回收 " + Util.SizeStr(wasted));
                }
                catch (Exception ex) { Log("重复扫描出错: " + ex.Message); }
                finally { BeginInvoke(new Action(delegate { SetBusy(false); })); }
            });
        }

        private void BuildDupList(List<List<FileEntry>> groups, long wasted)
        {
            int maxShow = 3000;
            int shown = 0;
            for (int i = 0; i < groups.Count && shown < maxShow; i++)
            {
                List<FileEntry> g = groups[i];
                ListViewGroup grp = new ListViewGroup("G" + (i + 1),
                    "第" + (i + 1) + "组 · 每份 " + Util.SizeStr(g[0].Size) + " · " + g.Count + " 份 · 可回收 " + Util.SizeStr(g[0].Size * (g.Count - 1)));
                lvDup.Groups.Add(grp);
                foreach (FileEntry fe in g)
                {
                    ListViewItem li = new ListViewItem(Path.GetFileName(fe.Path));
                    li.SubItems.Add(Util.SizeStr(fe.Size));
                    li.SubItems.Add(fe.MTime.ToString("yyyy-MM-dd HH:mm"));
                    li.SubItems.Add(fe.Path);
                    li.Tag = fe;
                    li.Group = grp;
                    lvDup.Items.Add(li);
                }
                shown++;
            }
            string extra = groups.Count > maxShow ? " (仅显示前 " + maxShow + " 组)" : "";
            lblDupInfo.Text = "共 " + groups.Count + " 组重复" + extra + ", 浪费空间合计约 " + Util.SizeStr(wasted)
                + " —— 选择策略后点[按策略勾选], 再手动微调, 确认无误再删除";
            SetStatus("重复文件: " + groups.Count + " 组, 可回收 " + Util.SizeStr(wasted));
        }

        private void ApplyStrategy()
        {
            int mode = cboStrategy.SelectedIndex; // 0最新 1最旧 2路径最短
            foreach (ListViewGroup grp in lvDup.Groups)
            {
                if (grp.Items.Count < 2) continue;
                ListViewItem keep = grp.Items[0];
                foreach (ListViewItem li in grp.Items)
                {
                    FileEntry fe = li.Tag as FileEntry;
                    if (fe == null) continue;
                    FileEntry kf = keep.Tag as FileEntry;
                    if (kf == null) { keep = li; continue; }
                    if (mode == 0 && fe.MTime > kf.MTime) keep = li;
                    else if (mode == 1 && fe.MTime < kf.MTime) keep = li;
                    else if (mode == 2 && fe.Path.Length < kf.Path.Length) keep = li;
                }
                foreach (ListViewItem li in grp.Items) li.Checked = (li != keep);
            }
            DupUpdateInfo();
        }

        private void DupUpdateInfo()
        {
            int n = 0; long sz = 0;
            foreach (ListViewItem li in lvDup.Items)
                if (li.Checked)
                {
                    n++;
                    FileEntry fe = li.Tag as FileEntry;
                    if (fe != null) sz += fe.Size;
                }
            lblDupInfo.Text = "已勾选 " + n + " 个文件, 合计 " + Util.SizeStr(sz) + " —— 删除后进入回收站, 可恢复";
        }

        private void DupDelete()
        {
            if (BusyGuard()) return;
            int cnt = 0; long sz = 0;
            foreach (ListViewItem li in lvDup.Items) if (li.Checked) { cnt++; FileEntry fe = li.Tag as FileEntry; if (fe != null) sz += fe.Size; }
            if (cnt == 0) { MessageBox.Show("请先勾选要删除的重复文件。\r\n可用[按策略勾选]一键勾选后手动微调。"); return; }

            if (MessageBox.Show("确认将 " + cnt + " 个重复文件(共 " + Util.SizeStr(sz) + ")删除到回收站?\r\n\r\n删除前请再次确认保留的那份是对的。",
                "确认删除", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;

            SetBusy(true);
            Log("开始删除重复文件: " + cnt + " 个, 共 " + Util.SizeStr(sz));
            Task.Run(delegate
            {
                List<ListViewItem> remove = new List<ListViewItem>();
                int ok = 0, fail = 0; long freed = 0;
                foreach (ListViewItem li in lvDup.Items)
                {
                    if (!li.Checked) continue;
                    FileEntry fe = li.Tag as FileEntry;
                    if (fe == null) continue;
                    if (Util.RecycleFile(fe.Path)) { ok++; freed += fe.Size; remove.Add(li); }
                    else fail++;
                }
                BeginInvoke(new Action(delegate
                {
                    foreach (ListViewItem li in remove) lvDup.Items.Remove(li);
                    DupUpdateInfo();
                    SetBusy(false);
                }));
                Log("重复文件删除完成: 成功 " + ok + " 个, 失败(占用中) " + fail + " 个, 合计 " + Util.SizeStr(freed));
                Util.AppendLog("[重复文件] 删除 " + ok + " 个 (失败" + fail + "), 回收 " + Util.SizeStr(freed));
            });
        }

        // ============================================================
        // Tab3 : 文件批处理
        // ============================================================
        private void BuildBatchTab()
        {
            TabPage tp = new TabPage(" 文件批处理 ");

            tp.Controls.Add(Lbl("范围:", 10, 12, 40));
            string defB = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            cboBScope = ScopeCombo(Directory.Exists(defB) ? defB : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            txtBFolder = new TextBox { Left = 244, Top = 8, Width = 262, Font = F(9), Text = defB };
            btnBBrowse = Btn("浏览...", 510, 6, 64, delegate
            {
                using (FolderBrowserDialog fd = new FolderBrowserDialog())
                {
                    if (fd.ShowDialog() == DialogResult.OK) { txtBFolder.Text = fd.SelectedPath; BatchSearch(); }
                }
            });
            WireCustomPath(txtBFolder, cboBScope);
            cboBScope.SelectedIndexChanged += delegate { OnScopeChanged(cboBScope, txtBFolder, delegate { BatchSearch(); }); };

            tp.Controls.Add(Lbl("类型:", 10, 42, 40));
            txtBPattern = new TextBox { Left = 50, Top = 38, Width = 120, Font = F(9), Text = "*.*" };
            tp.Controls.Add(Lbl("大于MB:", 176, 42, 52));
            numBMin = new NumericUpDown { Left = 230, Top = 38, Width = 52, Minimum = 0, Maximum = 10240, Value = 0, Font = F(9) };
            tp.Controls.Add(Lbl("修改时间:", 292, 42, 56));
            cboBDate = new ComboBox { Left = 350, Top = 38, Width = 90, DropDownStyle = ComboBoxStyle.DropDownList, Font = F(9) };
            cboBDate.Items.Add("不限");
            cboBDate.Items.Add("7天前");
            cboBDate.Items.Add("30天前");
            cboBDate.Items.Add("90天前");
            cboBDate.SelectedIndex = 0;
            btnBSearch = Btn("搜索文件", 448, 36, 96, delegate { BatchSearch(); });

            lblBInfo = Lbl("按条件搜索后勾选, 可批量删除(回收站)或移动到其他磁盘", 10, 68, 860);

            clbBatch = new CheckedListBox
            {
                Left = 10, Top = 90, Width = 872, Height = 290,
                Font = new Font("Consolas", 9F), CheckOnClick = true, HorizontalScrollbar = true
            };

            btnBAll = Btn("全选", 10, 386, 70, delegate
            {
                for (int i = 0; i < clbBatch.Items.Count; i++) clbBatch.SetItemChecked(i, true);
            });
            btnBDel = Btn("批量删除 → 回收站", 86, 386, 150, delegate { BatchDelete(); });
            btnBMove = Btn("批量移动到...", 242, 386, 130, delegate { BatchMove(); });

            tp.Controls.AddRange(new Control[] { cboBScope, txtBFolder, btnBBrowse, txtBPattern, numBMin, cboBDate, btnBSearch, lblBInfo, clbBatch, btnBAll, btnBDel, btnBMove });
            tabs.TabPages.Add(tp);
        }

        private void BatchSearch()
        {
            if (BusyGuard()) return;
            List<string> roots = GetRoots(cboBScope, txtBFolder);
            if (roots.Count == 0) { MessageBox.Show("搜索范围无效, 请重新选择范围或路径。"); return; }
            string rootDesc = roots.Count == 1 ? roots[0] : "整个电脑 (" + roots.Count + " 个硬盘)";

            List<string> pats = new List<string>();
            foreach (string s in txtBPattern.Text.Split(new char[] { ';', ',', '，' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string t = s.Trim();
                if (t.Length > 0) pats.Add(t.ToLowerInvariant());
            }
            if (pats.Count == 0) pats.Add("*.*");

            long minBytes = (long)(numBMin.Value * 1024m * 1024m);
            int days = new int[] { 0, 7, 30, 90 }[cboBDate.SelectedIndex];
            DateTime cutoff = days > 0 ? DateTime.Now.AddDays(-days) : DateTime.MaxValue;

            SetBusy(true);
            clbBatch.Items.Clear();
            batchFiles = new List<FileEntry>();
            lblBInfo.Text = "搜索中...";
            Log("批处理搜索: " + rootDesc + "  类型 " + txtBPattern.Text + "  >" + numBMin.Value + "MB  " + cboBDate.Text);

            Task.Run(delegate
            {
                try
                {
                List<string> paths = new List<string>();
                foreach (string r in roots)
                {
                    SetStatus("批处理: 正在遍历 " + r + " ...");
                    Util.EnumFiles(r, minBytes, paths, delegate(string s) { SetStatus("批处理搜索: " + s); });
                }
                List<FileEntry> matched = new List<FileEntry>();
                foreach (string p in paths)
                {
                    try
                    {
                        FileInfo fi = new FileInfo(p);
                        if (fi.LastWriteTime >= cutoff) continue;
                        string name = Path.GetFileName(p).ToLowerInvariant();
                        bool hit = false;
                        foreach (string pat in pats)
                        {
                            string pt = pat == "*.*" ? "*" : pat;
                            if (Util.WildMatch(name, pt)) { hit = true; break; }
                        }
                        if (!hit) continue;
                        FileEntry fe = new FileEntry();
                        fe.Path = p; fe.Size = fi.Length; fe.MTime = fi.LastWriteTime;
                        matched.Add(fe);
                    }
                    catch { }
                }
                matched.Sort(delegate(FileEntry a, FileEntry b) { return b.Size.CompareTo(a.Size); });

                int maxShow = 5000;
                BeginInvoke(new Action(delegate
                {
                    int shown = 0;
                    foreach (FileEntry fe in matched)
                    {
                        if (shown >= maxShow) break;
                        clbBatch.Items.Add("[" + Util.SizeStr(fe.Size).PadLeft(9) + "]  " + fe.MTime.ToString("yyyy-MM-dd") + "  " + fe.Path, false);
                        batchFiles.Add(fe);
                        shown++;
                    }
                    FitClbWidth(clbBatch);
                    lblBInfo.Text = "匹配 " + matched.Count + " 个文件" + (matched.Count > maxShow ? " (仅显示前 " + maxShow + ")" : "")
                        + ", 已按大小倒序, 勾选后执行操作";
                    SetStatus("批处理搜索完成: " + matched.Count + " 个匹配");
                    Log("批处理搜索完成: 匹配 " + matched.Count + " 个");
                }));
                }
                catch (Exception ex) { Log("批处理搜索出错: " + ex.Message); }
                finally { BeginInvoke(new Action(delegate { SetBusy(false); })); }
            });
        }

        private void BatchDelete()
        {
            if (BusyGuard()) return;
            List<int> idxs = new List<int>();
            for (int i = 0; i < clbBatch.Items.Count; i++)
                if (clbBatch.GetItemChecked(i) && i < batchFiles.Count) idxs.Add(i);
            if (idxs.Count == 0) { MessageBox.Show("请先搜索并勾选文件。"); return; }

            long sz = 0; foreach (int i in idxs) sz += batchFiles[i].Size;
            if (MessageBox.Show("确认将 " + idxs.Count + " 个文件(共 " + Util.SizeStr(sz) + ")删除到回收站?",
                "确认删除", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;

            SetBusy(true);
            Task.Run(delegate
            {
                int ok = 0, fail = 0; long freed = 0;
                foreach (int i in idxs)
                {
                    if (Util.RecycleFile(batchFiles[i].Path)) { ok++; freed += batchFiles[i].Size; }
                    else fail++;
                }
                Log("批处理删除完成: 成功 " + ok + ", 失败 " + fail + ", 合计 " + Util.SizeStr(freed));
                Util.AppendLog("[批处理] 删除 " + ok + " 个文件 (失败" + fail + "), " + Util.SizeStr(freed));
                BeginInvoke(new Action(delegate
                {
                    // 从后往前移除, 保持索引有效
                    for (int k = idxs.Count - 1; k >= 0; k--)
                    {
                        int i = idxs[k];
                        if (i < clbBatch.Items.Count)
                        {
                            clbBatch.Items.RemoveAt(i);
                            batchFiles.RemoveAt(i);
                        }
                    }
                    lblBInfo.Text = "已删除 " + ok + " 个文件到回收站, 失败 " + fail + " 个";
                    SetBusy(false);
                }));
            });
        }

        private void BatchMove()
        {
            if (BusyGuard()) return;
            List<int> idxs = new List<int>();
            for (int i = 0; i < clbBatch.Items.Count; i++)
                if (clbBatch.GetItemChecked(i) && i < batchFiles.Count) idxs.Add(i);
            if (idxs.Count == 0) { MessageBox.Show("请先搜索并勾选文件。"); return; }

            string destDir = null;
            using (FolderBrowserDialog fd = new FolderBrowserDialog())
            {
                fd.Description = "选择移动目标文件夹 (建议选择 D 盘)";
                if (fd.ShowDialog() != DialogResult.OK) return;
                destDir = fd.SelectedPath;
            }

            long sz = 0; foreach (int i in idxs) sz += batchFiles[i].Size;
            if (MessageBox.Show("确认将 " + idxs.Count + " 个文件(共 " + Util.SizeStr(sz) + ")移动到:\r\n" + destDir + " ?\r\n\r\n同名文件会自动改名, 不覆盖。",
                "确认移动", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;

            SetBusy(true);
            string target = destDir;
            Task.Run(delegate
            {
                int ok = 0, fail = 0; long moved = 0;
                foreach (int i in idxs)
                {
                    if (MoveFile(batchFiles[i].Path, target) != null) { ok++; moved += batchFiles[i].Size; }
                    else fail++;
                }
                Log("批处理移动完成: 成功 " + ok + ", 失败 " + fail + ", 合计 " + Util.SizeStr(moved) + " -> " + target);
                Util.AppendLog("[批处理] 移动 " + ok + " 个文件到 " + target + " (失败" + fail + "), " + Util.SizeStr(moved));
                BeginInvoke(new Action(delegate
                {
                    for (int k = idxs.Count - 1; k >= 0; k--)
                    {
                        int i = idxs[k];
                        if (i < clbBatch.Items.Count)
                        {
                            clbBatch.Items.RemoveAt(i);
                            batchFiles.RemoveAt(i);
                        }
                    }
                    lblBInfo.Text = "已移动 " + ok + " 个文件到 " + target + ", 失败 " + fail + " 个";
                    SetBusy(false);
                }));
            });
        }

        private string MoveFile(string src, string destDir)
        {
            try
            {
                string name = Path.GetFileName(src);
                string dest = Path.Combine(destDir, name);
                int k = 1;
                while (File.Exists(dest))
                {
                    dest = Path.Combine(destDir, Path.GetFileNameWithoutExtension(name) + "(" + k + ")" + Path.GetExtension(name));
                    k++;
                }
                File.Move(src, dest);
                return dest;
            }
            catch { return null; }
        }

        // ============================================================
        // Tab4 : 大文件TOP
        // ============================================================
        private void BuildTopTab()
        {
            TabPage tp = new TabPage(" 大文件TOP ");

            tp.Controls.Add(Lbl("范围:", 10, 12, 40));
            List<string> fixedDrv = AllFixedDrives();
            cboTopScope = ScopeCombo(fixedDrv.Count > 0 ? fixedDrv[0] : null);
            txtTopPath = new TextBox { Left = 244, Top = 8, Width = 262, Font = F(9), Text = (fixedDrv.Count > 0 ? fixedDrv[0] : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) };
            btnTopBrowse = Btn("浏览...", 510, 6, 64, delegate
            {
                using (FolderBrowserDialog fd = new FolderBrowserDialog())
                {
                    if (fd.ShowDialog() == DialogResult.OK) { txtTopPath.Text = fd.SelectedPath; TopScan(); }
                }
            });
            btnTopScan = Btn("立即扫描", 578, 6, 90, delegate { TopScan(); });
            WireCustomPath(txtTopPath, cboTopScope);
            cboTopScope.SelectedIndexChanged += delegate { OnScopeChanged(cboTopScope, txtTopPath, delegate { TopScan(); }); };

            lblTopInfo = Lbl("上方选择范围即自动开始; 找出占用最大的 50 个文件和 20 个文件夹 (文件夹可能互相包含, 数字有重叠)", 10, 40, 860);

            lvTop = new ListView
            {
                Left = 10, Top = 62, Width = 872, Height = 336, View = View.Details, FullRowSelect = true,
                Font = new Font("Consolas", 9F)
            };
            lvTop.Columns.Add("类型", 52);
            lvTop.Columns.Add("名称", 240);
            lvTop.Columns.Add("大小", 90);
            lvTop.Columns.Add("修改时间", 130);
            lvTop.Columns.Add("路径", 340);

            btnOpenLoc = Btn("打开所在位置", 10, 402, 130, delegate
            {
                if (lvTop.SelectedItems.Count == 0) { MessageBox.Show("请先选中一行。"); return; }
                string p = lvTop.SelectedItems[0].Tag as string;
                if (!string.IsNullOrEmpty(p))
                {
                    try { Process.Start("explorer.exe", "/select,\"" + p + "\""); }
                    catch { }
                }
            });

            tp.Controls.AddRange(new Control[] { cboTopScope, txtTopPath, btnTopBrowse, btnTopScan, lblTopInfo, lvTop, btnOpenLoc });
            tabs.TabPages.Add(tp);
        }

        private void TopScan()
        {
            if (BusyGuard()) return;
            List<string> roots = GetRoots(cboTopScope, txtTopPath);
            if (roots.Count == 0) { MessageBox.Show("扫描范围无效, 请重新选择范围或路径。"); return; }
            string rootDesc = roots.Count == 1 ? roots[0] : "整个电脑 (" + roots.Count + " 个硬盘)";
            SetBusy(true);
            lvTop.Items.Clear(); lvTop.Groups.Clear();
            lblTopInfo.Text = "扫描中(统计每个文件夹大小需要遍历全部文件, 请稍候)...";
            Log("大文件TOP扫描开始: " + rootDesc);

            Task.Run(delegate
            {
                try
                {
                List<string> rootsFull = new List<string>();
                List<FileEntry> files = new List<FileEntry>();
                Dictionary<string, long> dirSizes = new Dictionary<string, long>();

                List<string> paths = new List<string>();
                foreach (string r in roots)
                {
                    rootsFull.Add(Path.GetFullPath(r).TrimEnd('\\'));
                    SetStatus("大文件TOP: 正在遍历 " + r + " ...");
                    Util.EnumFiles(r, 0, paths, delegate(string s) { SetStatus("大文件TOP: " + s); });
                }
                int done = 0;
                foreach (string p in paths)
                {
                    try
                    {
                        FileInfo fi = new FileInfo(p);
                        FileEntry fe = new FileEntry();
                        fe.Path = p; fe.Size = fi.Length; fe.MTime = fi.LastWriteTime;
                        files.Add(fe);
                        // 文件大小逐级累加到各级父目录(支持多根目录/整个电脑)
                        string d = Path.GetDirectoryName(p);
                        while (!string.IsNullOrEmpty(d))
                        {
                            string dt = d.TrimEnd('\\');
                            bool atRoot = false;
                            foreach (string rf in rootsFull)
                                if (dt.Equals(rf, StringComparison.OrdinalIgnoreCase)) { atRoot = true; break; }
                            if (atRoot) break;
                            long old;
                            dirSizes.TryGetValue(d, out old);
                            dirSizes[d] = old + fe.Size;
                            d = Path.GetDirectoryName(d);
                        }
                        done++;
                        if (done % 5000 == 0) SetStatus("大文件TOP扫描: 已处理 " + done + " 个文件...");
                    }
                    catch { }
                }

                files.Sort(delegate(FileEntry a, FileEntry b) { return b.Size.CompareTo(a.Size); });
                List<KeyValuePair<string, long>> dirs = new List<KeyValuePair<string, long>>(dirSizes);
                dirs.Sort(delegate(KeyValuePair<string, long> a, KeyValuePair<string, long> b) { return b.Value.CompareTo(a.Value); });

                BeginInvoke(new Action(delegate
                {
                    ListViewGroup gFile = new ListViewGroup("gf", "TOP 50 大文件");
                    ListViewGroup gDir = new ListViewGroup("gd", "TOP 20 大文件夹 (可能互相包含)");
                    lvTop.Groups.Add(gFile); lvTop.Groups.Add(gDir);

                    for (int i = 0; i < files.Count && i < 50; i++)
                    {
                        FileEntry fe = files[i];
                        ListViewItem li = new ListViewItem("文件");
                        li.SubItems.Add(Path.GetFileName(fe.Path));
                        li.SubItems.Add(Util.SizeStr(fe.Size));
                        li.SubItems.Add(fe.MTime.ToString("yyyy-MM-dd HH:mm"));
                        li.SubItems.Add(Path.GetDirectoryName(fe.Path));
                        li.Tag = fe.Path;
                        li.Group = gFile;
                        lvTop.Items.Add(li);
                    }
                    for (int i = 0; i < dirs.Count && i < 20; i++)
                    {
                        string dpath = dirs[i].Key;
                        ListViewItem li = new ListViewItem("文件夹");
                        li.SubItems.Add(Path.GetFileName(dpath.TrimEnd('\\')));
                        li.SubItems.Add(Util.SizeStr(dirs[i].Value));
                        try { li.SubItems.Add(Directory.GetLastWriteTime(dpath).ToString("yyyy-MM-dd HH:mm")); }
                        catch { li.SubItems.Add(""); }
                        li.SubItems.Add(dpath);
                        li.Tag = dpath;
                        li.Group = gDir;
                        lvTop.Items.Add(li);
                    }
                    lblTopInfo.Text = "扫描完成: 共 " + files.Count + " 个文件, 选中一行后可[打开所在位置]";
                    SetStatus("大文件TOP: " + files.Count + " 文件 / " + dirs.Count + " 文件夹");
                    Log("大文件TOP扫描完成: " + files.Count + " 个文件");
                }));
                }
                catch (Exception ex) { Log("大文件TOP扫描出错: " + ex.Message); }
                finally { BeginInvoke(new Action(delegate { SetBusy(false); })); }
            });
        }

        // ============================================================
        // Tab5 : 关于
        // ============================================================
        private void BuildAboutTab()
        {
            TabPage tp = new TabPage(" 关于 ");

            Label title = new Label
            {
                Text = "ClearC  C盘清理工具", Left = 10, Top = 20, Width = 872, Height = 36,
                Font = new Font("Microsoft YaHei", 16F, FontStyle.Bold), TextAlign = ContentAlignment.MiddleCenter
            };
            Label ver = new Label
            {
                Text = "版本 1.1.0.0", Left = 10, Top = 60, Width = 872, Height = 20,
                Font = F(10), TextAlign = ContentAlignment.MiddleCenter
            };
            Label cp = new Label
            {
                Text = "版权所有 © zsqstudio      联系: 11016795@qq.com", Left = 10, Top = 86, Width = 872, Height = 22,
                Font = new Font("Microsoft YaHei", 10F, FontStyle.Bold), TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.SteelBlue
            };

            Label body = new Label
            {
                Left = 24, Top = 126, Width = 844, Height = 300, Font = F(9),
                Text =
                    "功能模块:\r\n"
                    + "  1. 缓存清理    —— 安全级自动勾选; 需确认级(微信/WPS/网盘等含个人数据)默认不勾选, 确认后再清\r\n"
                    + "  2. 重复文件对比 —— MD5 全量校验, 分组展示, 一键保留最新/最旧/路径最短, 删除进回收站\r\n"
                    + "  3. 文件批处理   —— 按类型/大小/时间筛选, 批量删除(回收站)或移动到其他磁盘(同名自动改名)\r\n"
                    + "  4. 大文件TOP   —— 找出占用最大的文件与文件夹, 一键定位到资源管理器\r\n"
                    + "\r\n"
                    + "安全说明:\r\n"
                    + "  · \"缓存清理\"页为永久删除, 立即释放空间 (回收站本身也占C盘)\r\n"
                    + "  · \"重复文件\"/\"批处理\"页的删除进入回收站, 可恢复\r\n"
                    + "  · 所有清理/删除/移动操作均记录在本程序目录 clean_log.txt\r\n"
                    + "\r\n"
                    + "免责声明:\r\n"
                    + "  本工具按\"默认最保守\"原则设计, 但删除操作不可逆, 使用前请自行确认重要数据已备份。\r\n"
                    + "  因使用本工具造成的任何数据损失, 由使用者自行承担。"
            };

            tp.Controls.AddRange(new Control[] { title, ver, cp, body });
            tabs.TabPages.Add(tp);
        }
    }
}
