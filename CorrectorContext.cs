using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using WeCantSpell.Hunspell;

namespace AutoClearText
{
    public class CorrectorContext : ApplicationContext
    {
        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")] static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
        [DllImport("user32.dll")] static extern bool GetCursorPos(out Point lpPoint);

        const int HOTKEY_CTRL_ENTER = 1;
        const int HOTKEY_CTRL_Y = 2;
        const int HOTKEY_CTRL_N = 3;
        const int HOTKEY_CTRL_ALT_ENTER = 4;
        const byte VK_CONTROL = 0x11;
        const byte VK_C = 0x43;
        const byte VK_V = 0x56;
        const uint KEYEVENTF_KEYUP = 0x0002;

        private static readonly string AppDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AutoClearText");
        private static readonly string LtDir = AppDataDir;
        private static readonly string LtJar = Path.Combine(AppDataDir, "languagetool-server.jar");
        private const string GITHUB_RELEASES = "https://github.com/OxiWanV2/AutoClearText/releases";
        private static string LT_URL => $"http://localhost:{_ltPort}/v2/check";
        private static string LT_HEALTH => $"http://localhost:{_ltPort}/v2/languages";

        private static WordList _dictionary;
        private static readonly Dictionary<string, string> _suggestCache = new();
        private static readonly char[] Separators = new[] { ' ', '\n', '\r', '\t', '\'' };
        private static readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        private static Process _ltProcess;
        private static bool _ltReady = false;

        private NotifyIcon trayIcon;
        private HiddenWindow hiddenWindow;
        private bool previewMode = true;
        private PreviewForm currentPreview;
        private string pendingCorrectedText = "";
        private string originalClipboard = "";

        private static bool EnsureResources()
        {
            var assembly = Assembly.GetExecutingAssembly();
            var missing = new List<string>();

            if (!File.Exists(LtJar))
            {
                const string resName = "AutoClearText.Resources.languagetool.zip";
                using var stream = assembly.GetManifestResourceStream(resName);
                if (stream == null)
                    missing.Add("languagetool.zip  (dossier Resources/ du projet)");
                else
                {
                    Directory.CreateDirectory(LtDir);
                    ExtractZipWithProgress(stream, AppDataDir, "Extraction LanguageTool...");

                    if (!File.Exists(LtJar))
                        missing.Add($"languagetool-server.jar introuvable après extraction.\nStructure ZIP incorrecte.\nFichier attendu : {LtJar}");
                }
            }

            if (missing.Count > 0)
            {
                string fileList = string.Join("\n  • ", missing);
                var result = MessageBox.Show(
                    $"Erreur au démarrage :\n\n  • {fileList}\n\n" +
                    $"Télécharge la dernière release sur GitHub.\n\n" +
                    $"Ouvrir la page GitHub ?",
                    "AutoClearText — Erreur",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Error);

                if (result == DialogResult.Yes)
                    Process.Start(new ProcessStartInfo(GITHUB_RELEASES) { UseShellExecute = true });

                return false;
            }

            return true;
        }

        private static void ExtractZipWithProgress(Stream zipStream, string destDir, string label)
        {
            Form splash = null;
            ProgressBar bar = null;
            Label lbl = null;

            var splashThread = new Thread(() =>
            {
                splash = new Form
                {
                    FormBorderStyle = FormBorderStyle.None,
                    Size = new Size(380, 80),
                    StartPosition = FormStartPosition.CenterScreen,
                    BackColor = Color.FromArgb(45, 45, 48),
                    TopMost = true
                };
                lbl = new Label
                {
                    Text = label,
                    ForeColor = Color.White,
                    Font = new Font("Segoe UI", 10),
                    AutoSize = true,
                    Location = new Point(15, 12)
                };
                bar = new ProgressBar
                {
                    Style = ProgressBarStyle.Marquee,
                    MarqueeAnimationSpeed = 30,
                    Size = new Size(350, 18),
                    Location = new Point(15, 40)
                };
                splash.Controls.Add(lbl);
                splash.Controls.Add(bar);
                Application.Run(splash);
            });
            splashThread.SetApartmentState(ApartmentState.STA);
            splashThread.Start();

            Thread.Sleep(200);

            try
            {
                using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);
                foreach (var entry in archive.Entries)
                {
                    string dest = Path.Combine(destDir, entry.FullName);
                    if (string.IsNullOrEmpty(entry.Name))
                    {
                        Directory.CreateDirectory(dest);
                        continue;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    entry.ExtractToFile(dest, overwrite: true);
                }
            }
            finally
            {
                splash?.Invoke(new Action(() => splash.Close()));
                splashThread.Join();
            }
        }

        private static WordList LoadDictionary()
        {
            string dicPath = Path.Combine(AppDataDir, "fr-toutesvariantes.dic");
            string affPath = Path.Combine(AppDataDir, "fr-toutesvariantes.aff");

            if (!File.Exists(dicPath) || !File.Exists(affPath))
            {
                var asm = Assembly.GetExecutingAssembly();
                using var dicStream = asm.GetManifestResourceStream("AutoClearText.Dictionaries.fr-toutesvariantes.dic");
                using var affStream = asm.GetManifestResourceStream("AutoClearText.Dictionaries.fr-toutesvariantes.aff");
                if (dicStream != null && affStream != null)
                    return WordList.CreateFromStreams(dicStream, affStream);
                throw new FileNotFoundException("Dictionnaire Hunspell introuvable.");
            }

            using var dic = File.OpenRead(dicPath);
            using var aff = File.OpenRead(affPath);
            return WordList.CreateFromStreams(dic, aff);
        }

        private static bool CheckJava()
        {
            try
            {
                var p = Process.Start(new ProcessStartInfo
                {
                    FileName = "java",
                    Arguments = "-version",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true
                });
                string ver = p.StandardError.ReadToEnd();
                p.WaitForExit();
                Debug.WriteLine($"Java détecté: {ver.Split('\n')[0]}");

                for (int v = 17; v <= 30; v++)
                    if (ver.Contains($"\"{v}.") || ver.Contains($"version \"{v}\""))
                        return true;

                MessageBox.Show(
                    $"Java détecté mais version insuffisante.\n\n" +
                    $"Version trouvée : {ver.Split('\n')[0].Trim()}\n" +
                    $"Version requise : Java 17 minimum\n\n" +
                    $"Télécharge OpenJDK 21 JRE sur :\nhttps://adoptium.net",
                    "AutoClearText — Java trop ancien",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);

                Process.Start(new ProcessStartInfo(
                    "https://adoptium.net/temurin/releases/?version=21&os=windows&arch=x64&package=jre")
                { UseShellExecute = true });
                return false;
            }
            catch
            {
                MessageBox.Show(
                    "Java n'est pas installé ou introuvable dans le PATH.\n\n" +
                    "LanguageTool nécessite Java 17 minimum.\n\n" +
                    "Télécharge OpenJDK 21 JRE sur :\nhttps://adoptium.net",
                    "AutoClearText — Java manquant",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);

                Process.Start(new ProcessStartInfo(
                    "https://adoptium.net/temurin/releases/?version=21&os=windows&arch=x64&package=jre")
                { UseShellExecute = true });
                return false;
            }
        }

        private static int _ltPort = 0;

        private static int GetFreePort()
        {
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        private static void StartLanguageTool()
        {
            try
            {
                if (!File.Exists(LtJar)) { Debug.WriteLine("JAR introuvable"); return; }

                _ltPort = GetFreePort();
                Debug.WriteLine($"Port LT choisi : {_ltPort}");

                var psi = new ProcessStartInfo
                {
                    FileName = "java",
                    Arguments = $"-Xmx512m -jar \"{LtJar}\" --port {_ltPort} --allow-origin \"*\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                _ltProcess = Process.Start(psi);
                AppDomain.CurrentDomain.ProcessExit += (s, e) =>
                {
                    try { if (_ltProcess != null && !_ltProcess.HasExited) _ltProcess.Kill(entireProcessTree: true); }
                    catch { }
                };
                Task.Run(() => { while (!_ltProcess.StandardOutput.EndOfStream) Debug.WriteLine($"[LT stdout] {_ltProcess.StandardOutput.ReadLine()}"); });
                Task.Run(() => { while (!_ltProcess.StandardError.EndOfStream) Debug.WriteLine($"[LT stderr] {_ltProcess.StandardError.ReadLine()}"); });

                for (int i = 0; i < 30; i++)
                {
                    Thread.Sleep(1000);
                    try
                    {
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                        var resp = _httpClient.GetAsync(LT_HEALTH, cts.Token).Result;
                        if (resp.IsSuccessStatusCode) { _ltReady = true; Debug.WriteLine($"LT prêt sur port {_ltPort}"); return; }
                    }
                    catch (Exception ex) { Debug.WriteLine($"Polling {i + 1}: {ex.GetBaseException().Message}"); }
                }
            }
            catch (Exception ex) { Debug.WriteLine($"Erreur LT: {ex.Message}"); }
        }

        private static async Task<string> CorrectWithLanguageTool(string text)
        {
            if (!_ltReady) return text;
            try
            {
                var content = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("text", text),
                    new KeyValuePair<string, string>("language", "auto"),
                    new KeyValuePair<string, string>("preferredVariants", "fr-FR,en-US"),
                    new KeyValuePair<string, string>("enabledOnly", "false")
                });

                var response = await _httpClient.PostAsync(LT_URL, content);
                var json = await response.Content.ReadAsStringAsync();

                using var doc = JsonDocument.Parse(json);
                var matches = doc.RootElement.GetProperty("matches");
                if (matches.GetArrayLength() == 0) return text;

                var sb = new StringBuilder(text);
                int offset = 0;

                foreach (var match in matches.EnumerateArray())
                {
                    var replacements = match.GetProperty("replacements");
                    if (replacements.GetArrayLength() == 0) continue;

                    string matchedWord = text.Substring(
                        match.GetProperty("offset").GetInt32(),
                        match.GetProperty("length").GetInt32());
                    if (IsLikelyEnglish(matchedWord)) continue;

                    int start = match.GetProperty("offset").GetInt32() + offset;
                    int length = match.GetProperty("length").GetInt32();
                    string replacement = replacements[0].GetProperty("value").GetString();

                    sb.Remove(start, length);
                    sb.Insert(start, replacement);
                    offset += replacement.Length - length;
                }

                return sb.ToString();
            }
            catch (Exception ex) { Debug.WriteLine($"Erreur LT: {ex.Message}"); return text; }
        }

        private static readonly HashSet<string> _englishWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "the","a","an","is","are","was","were","be","been","have","has","had",
            "do","does","did","will","would","could","should","may","might","shall",
            "and","or","but","if","when","where","how","why","what","who","which",
            "this","that","these","those","it","he","she","they","we","you","i",
            "my","your","his","her","our","their","its","ok","okay","yes","no",
            "hi","hello","bye","thanks","please","sorry","lol","omg","wtf","gg",
            "update","settings","debug","error","warning","info","null","true","false"
        };

        private static bool IsLikelyEnglish(string word) =>
            _englishWords.Contains(word.Trim('.', ',', '!', '?', ';', ':'));

        public CorrectorContext()
        {
            if (!EnsureResources())
            {
                ExitThread();
                return;
            }

            _dictionary = LoadDictionary();

            InitializeSystem();
        }

        private void InitializeSystem()
        {
            hiddenWindow = new HiddenWindow();
            hiddenWindow.HotKeyPressed += OnHotKeyPressed;

            trayIcon = new NotifyIcon
            {
                Icon = SystemIcons.Information,
                Text = "Correcteur - [LT: démarrage...]",
                Visible = true
            };

            var contextMenu = new ContextMenuStrip();
            contextMenu.Items.Add("Quitter", null, (s, e) => ExitApplication());
            trayIcon.ContextMenuStrip = contextMenu;

            RegisterHotKeys();

            Task.Run(() =>
            {
                if (!CheckJava())
                {
                    hiddenWindow.Invoke(new Action(() => ExitApplication()));
                    return;
                }

                if (!File.Exists(LtJar))
                {
                    var result = MessageBox.Show(
                        $"LanguageTool introuvable après extraction.\n\n" +
                        $"Fichier attendu :\n{LtJar}\n\n" +
                        $"Vérifie que le ZIP embarqué contient bien le fichier\n" +
                        $"'languagetool-server.jar' à la racine.\n\n" +
                        $"Ouvrir GitHub pour télécharger la bonne version ?",
                        "AutoClearText — LanguageTool manquant",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Error);

                    if (result == DialogResult.Yes)
                        Process.Start(new ProcessStartInfo(GITHUB_RELEASES) { UseShellExecute = true });

                    hiddenWindow.Invoke(new Action(() => ExitApplication()));
                    return;
                }

                StartLanguageTool();
                trayIcon.Text = $"Correcteur - Mode: {(previewMode ? "Aperçu" : "Direct")} [LT: {(_ltReady ? "prêt" : "indispo")}]";
            });
        }

        private void RegisterHotKeys()
        {
            RegisterHotKey(hiddenWindow.Handle, HOTKEY_CTRL_ENTER, 0x2, (uint)Keys.Enter);
            RegisterHotKey(hiddenWindow.Handle, HOTKEY_CTRL_Y, 0x2, (uint)Keys.Y);
            RegisterHotKey(hiddenWindow.Handle, HOTKEY_CTRL_N, 0x2, (uint)Keys.N);
            RegisterHotKey(hiddenWindow.Handle, HOTKEY_CTRL_ALT_ENTER, 0x2 | 0x1, (uint)Keys.Enter);
        }

        private void OnHotKeyPressed(int hotkeyId)
        {
            switch (hotkeyId)
            {
                case HOTKEY_CTRL_ENTER: Task.Run(ProcessCtrlEnter); break;
                case HOTKEY_CTRL_Y: if (previewMode && !string.IsNullOrEmpty(pendingCorrectedText)) ApplyCorrection(); break;
                case HOTKEY_CTRL_N: if (previewMode) CancelCorrection(); break;
                case HOTKEY_CTRL_ALT_ENTER: TogglePreviewMode(); break;
            }
        }

        private void SendCtrlC()
        {
            keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
            keybd_event(VK_C, 0, 0, UIntPtr.Zero);
            keybd_event(VK_C, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }

        private void SendCtrlV()
        {
            keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
            keybd_event(VK_V, 0, 0, UIntPtr.Zero);
            keybd_event(VK_V, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }

        private void TogglePreviewMode()
        {
            previewMode = !previewMode;
            string modeTxt = previewMode ? "Aperçu" : "Direct";
            trayIcon.Text = $"Correcteur - Mode: {modeTxt} [LT: {(_ltReady ? "prêt" : "indispo")}]";
            trayIcon.ShowBalloonTip(2000, "Correcteur", $"Mode : {modeTxt}", ToolTipIcon.Info);
            if (currentPreview != null) { currentPreview.Close(); currentPreview = null; }
        }

        private void ShowPreview(string originalText, string correctedText)
        {
            if (currentPreview != null && !currentPreview.IsDisposed) currentPreview.Close();
            GetCursorPos(out Point cursorPos);
            currentPreview = new PreviewForm(originalText, correctedText);
            currentPreview.StartPosition = FormStartPosition.Manual;
            currentPreview.Location = new Point(cursorPos.X - currentPreview.Width / 2, cursorPos.Y - currentPreview.Height - 20);
            currentPreview.TopMost = true;
            currentPreview.Show();
        }

        private void ApplyCorrection()
        {
            if (currentPreview != null) { currentPreview.Close(); currentPreview = null; }
            if (string.IsNullOrEmpty(pendingCorrectedText)) { ClearPendingState(); return; }
            Task.Run(() =>
            {
                try
                {
                    Thread.Sleep(100);
                    hiddenWindow.Invoke(new Action(() => Clipboard.SetText(pendingCorrectedText)));
                    Thread.Sleep(200);
                    SendCtrlV();
                }
                catch (Exception ex) { Debug.WriteLine($"Erreur ApplyCorrection: {ex.Message}"); }
                finally { ClearPendingState(); }
            });
        }

        private void CancelCorrection()
        {
            if (currentPreview != null) { currentPreview.Close(); currentPreview = null; }
            ClearPendingState();
        }

        private void ClearPendingState() { pendingCorrectedText = ""; originalClipboard = ""; }

        private string CorrectTextOffline(string text)
        {
            var tokens = new List<(string value, bool isSeparator)>();
            var current = new StringBuilder();
            foreach (char c in text)
            {
                if (Separators.Contains(c))
                {
                    if (current.Length > 0) { tokens.Add((current.ToString(), false)); current.Clear(); }
                    tokens.Add((c.ToString(), true));
                }
                else current.Append(c);
            }
            if (current.Length > 0) tokens.Add((current.ToString(), false));

            var results = new string[tokens.Count];
            Parallel.For(0, tokens.Count, i =>
            {
                var (value, isSeparator) = tokens[i];
                if (isSeparator) { results[i] = value; return; }

                int start = 0, end = value.Length;
                while (start < end && !char.IsLetterOrDigit(value[start])) start++;
                while (end > start && !char.IsLetterOrDigit(value[end - 1])) end--;

                string prefix = value[..start];
                string word = value[start..end];
                string suffix = value[end..];

                if (string.IsNullOrEmpty(word) || _dictionary.Check(word)) { results[i] = value; return; }

                string cacheKey = word.ToLowerInvariant();
                string best;
                lock (_suggestCache)
                {
                    if (!_suggestCache.TryGetValue(cacheKey, out best))
                    {
                        best = _dictionary.Suggest(word).FirstOrDefault();
                        _suggestCache[cacheKey] = best;
                    }
                }

                if (best != null)
                {
                    if (char.IsUpper(word[0]) && char.IsLower(best[0]))
                        best = char.ToUpper(best[0]) + best[1..];
                    results[i] = prefix + best + suffix;
                }
                else results[i] = value;
            });

            return string.Concat(results);
        }

        private async Task ProcessCtrlEnter()
        {
            try
            {
                string clipboardContent = "";
                hiddenWindow.Invoke(new Action(() => { try { originalClipboard = Clipboard.GetText(); } catch { } }));
                SendCtrlC();
                Thread.Sleep(150);
                hiddenWindow.Invoke(new Action(() => { try { clipboardContent = Clipboard.GetText(); } catch (Exception ex) { Debug.WriteLine($"Erreur clipboard: {ex.Message}"); } }));
                if (string.IsNullOrEmpty(clipboardContent)) return;

                string afterHunspell = CorrectTextOffline(clipboardContent);
                string correctedText = _ltReady ? await CorrectWithLanguageTool(afterHunspell) : afterHunspell;

                if (correctedText != clipboardContent)
                {
                    if (previewMode)
                    {
                        pendingCorrectedText = correctedText;
                        hiddenWindow.Invoke(new Action(() => ShowPreview(clipboardContent, correctedText)));
                    }
                    else
                    {
                        hiddenWindow.Invoke(new Action(() => Clipboard.SetText(correctedText)));
                        Thread.Sleep(200);
                        SendCtrlV();
                    }
                }
            }
            catch (Exception ex) { Debug.WriteLine($"Erreur générale: {ex}"); }
        }

        private void ExitApplication()
        {
            UnregisterHotKey(hiddenWindow.Handle, HOTKEY_CTRL_ENTER);
            UnregisterHotKey(hiddenWindow.Handle, HOTKEY_CTRL_Y);
            UnregisterHotKey(hiddenWindow.Handle, HOTKEY_CTRL_N);
            UnregisterHotKey(hiddenWindow.Handle, HOTKEY_CTRL_ALT_ENTER);
            trayIcon?.Dispose();
            hiddenWindow?.Dispose();

            try
            {
                if (_ltProcess != null && !_ltProcess.HasExited)
                {
                    _ltProcess.Kill(entireProcessTree: true);
                    _ltProcess.WaitForExit(3000);
                }
                _ltProcess?.Dispose();
            }
            catch { }

            ExitThread();
        }

        protected override void Dispose(bool disposing) { if (disposing) ExitApplication(); base.Dispose(disposing); }
    }

    public class HiddenWindow : Form
    {
        public event Action<int> HotKeyPressed;
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0312) HotKeyPressed?.Invoke(m.WParam.ToInt32());
            base.WndProc(ref m);
        }
        protected override void SetVisibleCore(bool value) => base.SetVisibleCore(false);
    }

    public class PreviewForm : Form
    {
        public PreviewForm(string originalText, string correctedText)
        { InitializeComponent(originalText, correctedText); ApplyRoundedCorners(); }

        private void ApplyRoundedCorners()
        {
            int r = 15;
            var path = new System.Drawing.Drawing2D.GraphicsPath();
            path.AddArc(0, 0, r, r, 180, 90); path.AddArc(Width - r, 0, r, r, 270, 90);
            path.AddArc(Width - r, Height - r, r, r, 0, 90); path.AddArc(0, Height - r, r, r, 90, 90);
            path.CloseFigure(); Region = new Region(path);
        }

        private void InitializeComponent(string originalText, string correctedText)
        {
            FormBorderStyle = FormBorderStyle.None; Size = new Size(500, 200);
            MaximizeBox = false; MinimizeBox = false; ShowIcon = false;
            BackColor = Color.FromArgb(45, 45, 48);
            var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(15), BackColor = Color.Transparent };
            panel.Controls.Add(new Label { Text = "Original:", Font = new Font("Segoe UI", 10, FontStyle.Bold), AutoSize = true, Location = new Point(15, 15), ForeColor = Color.FromArgb(255, 107, 107), BackColor = Color.Transparent });
            panel.Controls.Add(new TextBox { Text = originalText, ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 9), BackColor = Color.FromArgb(255, 235, 235), ForeColor = Color.FromArgb(139, 0, 0), BorderStyle = BorderStyle.None, Size = new Size(460, 40), Location = new Point(15, 40) });
            panel.Controls.Add(new Label { Text = "Corrigé:", Font = new Font("Segoe UI", 10, FontStyle.Bold), AutoSize = true, Location = new Point(15, 90), ForeColor = Color.FromArgb(144, 238, 144), BackColor = Color.Transparent });
            panel.Controls.Add(new TextBox { Text = correctedText, ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 9), BackColor = Color.FromArgb(235, 255, 235), ForeColor = Color.FromArgb(34, 139, 34), BorderStyle = BorderStyle.None, Size = new Size(460, 40), Location = new Point(15, 115) });
            panel.Controls.Add(new Label { Text = "Ctrl+Y pour accepter | Ctrl+N pour annuler | Ctrl+Alt+Enter pour changer le mode", Font = new Font("Segoe UI", 8, FontStyle.Italic), ForeColor = Color.FromArgb(200, 200, 200), BackColor = Color.Transparent, AutoSize = true, Location = new Point(15, 165) });
            Controls.Add(panel);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using var pen = new Pen(Color.FromArgb(100, 100, 100), 1);
            int r = 15;
            var path = new System.Drawing.Drawing2D.GraphicsPath();
            path.AddArc(0, 0, r, r, 180, 90); path.AddArc(Width - r - 1, 0, r, r, 270, 90);
            path.AddArc(Width - r - 1, Height - r - 1, r, r, 0, 90); path.AddArc(0, Height - r - 1, r, r, 90, 90);
            path.CloseFigure(); e.Graphics.DrawPath(pen, path);
        }
    }
}