using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Newtonsoft.Json;

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
        const int WM_HOTKEY = 0x0312;
        const byte VK_CONTROL = 0x11;
        const byte VK_C = 0x43;
        const byte VK_V = 0x56;
        const uint KEYEVENTF_KEYUP = 0x0002;

        private readonly HttpClient httpClient = new HttpClient();
        private NotifyIcon trayIcon;
        private HiddenWindow hiddenWindow;
        private bool previewMode = true;
        private PreviewForm currentPreview;
        private string pendingCorrectedText = "";
        private string originalClipboard = "";

        public CorrectorContext()
        {
            InitializeSystem();
        }

        private void InitializeSystem()
        {
            hiddenWindow = new HiddenWindow();
            hiddenWindow.HotKeyPressed += OnHotKeyPressed;

            trayIcon = new NotifyIcon
            {
                Icon = SystemIcons.Information,
                Text = $"Correcteur LanguageTool - Mode: {(previewMode ? "Aperçu" : "Direct")}",
                Visible = true
            };

            var contextMenu = new ContextMenuStrip();
            contextMenu.Items.Add("Quitter", null, (s, e) => ExitApplication());
            trayIcon.ContextMenuStrip = contextMenu;

            RegisterHotKeys();
            Debug.WriteLine("Correcteur démarré - Aucune fenêtre visible");
        }

        private void RegisterHotKeys()
        {
            RegisterHotKey(hiddenWindow.Handle, HOTKEY_CTRL_ENTER, 0x2, (uint)Keys.Enter);
            RegisterHotKey(hiddenWindow.Handle, HOTKEY_CTRL_Y, 0x2, (uint)Keys.Y);
            RegisterHotKey(hiddenWindow.Handle, HOTKEY_CTRL_N, 0x2, (uint)Keys.N);
            RegisterHotKey(hiddenWindow.Handle, HOTKEY_CTRL_ALT_ENTER, 0x2 | 0x1, (uint)Keys.Enter);
            Debug.WriteLine("Hotkeys enregistrées");
        }

        private void OnHotKeyPressed(int hotkeyId)
        {
            switch (hotkeyId)
            {
                case HOTKEY_CTRL_ENTER:
                    Task.Run(ProcessCtrlEnter);
                    break;
                case HOTKEY_CTRL_Y:
                    if (previewMode && !string.IsNullOrEmpty(pendingCorrectedText))
                        ApplyCorrection();
                    break;
                case HOTKEY_CTRL_N:
                    if (previewMode)
                        CancelCorrection();
                    break;
                case HOTKEY_CTRL_ALT_ENTER:
                    TogglePreviewMode();
                    break;
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
            trayIcon.Text = $"Correcteur LanguageTool - Mode: {modeTxt}";
            trayIcon.ShowBalloonTip(2000, "Correcteur", $"Mode : {modeTxt}", ToolTipIcon.Info);
            Debug.WriteLine($"Mode basculé vers: {modeTxt}");

            if (currentPreview != null)
            {
                currentPreview.Close();
                currentPreview = null;
            }
        }

        private void ShowPreview(string originalText, string correctedText)
        {
            if (currentPreview != null && !currentPreview.IsDisposed)
                currentPreview.Close();

            GetCursorPos(out Point cursorPos);
            currentPreview = new PreviewForm(originalText, correctedText);
            currentPreview.StartPosition = FormStartPosition.Manual;
            currentPreview.Location = new Point(cursorPos.X - currentPreview.Width / 2, cursorPos.Y - currentPreview.Height - 20);
            currentPreview.TopMost = true;
            currentPreview.Show();
        }

        private void ApplyCorrection()
        {
            Debug.WriteLine($"ApplyCorrection appelé avec: '{pendingCorrectedText ?? "NULL"}'");

            if (currentPreview != null)
            {
                currentPreview.Close();
                currentPreview = null;
            }

            if (string.IsNullOrEmpty(pendingCorrectedText))
            {
                Debug.WriteLine("ERREUR: pendingCorrectedText est null ou vide");
                ClearPendingState();
                return;
            }

            Task.Run(() =>
            {
                try
                {
                    Thread.Sleep(100);
                    hiddenWindow.Invoke(new Action(() =>
                    {
                        Clipboard.SetText(pendingCorrectedText);
                    }));
                    Thread.Sleep(200);
                    SendCtrlV();
                    Debug.WriteLine("Correction appliquée avec succès");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Erreur dans ApplyCorrection: {ex.Message}");
                }
                finally
                {
                    ClearPendingState();
                }
            });
        }

        private void CancelCorrection()
        {
            Debug.WriteLine("Correction annulée");

            if (currentPreview != null)
            {
                currentPreview.Close();
                currentPreview = null;
            }

            ClearPendingState();
        }

        private void ClearPendingState()
        {
            pendingCorrectedText = "";
            originalClipboard = "";
        }

        private async Task<string> CorrectTextWithLanguageTool(string text)
        {
            try
            {
                var content = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("text", text),
                    new KeyValuePair<string, string>("language", "fr")
                });

                var response = await httpClient.PostAsync("https://languagetool.org/api/v2/check", content);
                var jsonResponse = await response.Content.ReadAsStringAsync();

                var result = JsonConvert.DeserializeObject<LanguageToolResponse>(jsonResponse);

                if (result?.Matches == null || result.Matches.Count == 0)
                {
                    Debug.WriteLine("Aucune correction trouvée");
                    return text;
                }

                var correctedText = new StringBuilder(text);
                var corrections = result.Matches;

                corrections.Sort((a, b) => b.Offset.CompareTo(a.Offset));

                foreach (var match in corrections)
                {
                    if (match.Replacements != null && match.Replacements.Count > 0)
                    {
                        var replacement = match.Replacements[0].Value;
                        correctedText.Remove(match.Offset, match.Length);
                        correctedText.Insert(match.Offset, replacement);
                        Debug.WriteLine($"Correction: '{text.Substring(match.Offset, match.Length)}' -> '{replacement}'");
                    }
                }

                return correctedText.ToString();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Erreur LanguageTool: {ex.Message}");
                return text;
            }
        }

        private async Task ProcessCtrlEnter()
        {
            try
            {
                string clipboardContent = "";
                hiddenWindow.Invoke(new Action(() =>
                {
                    try
                    {
                        originalClipboard = Clipboard.GetText();
                    }
                    catch
                    {
                        originalClipboard = "";
                    }
                }));

                SendCtrlC();
                Thread.Sleep(300);

                hiddenWindow.Invoke(new Action(() =>
                {
                    try
                    {
                        clipboardContent = Clipboard.GetText();
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Erreur lecture clipboard: {ex.Message}");
                    }
                }));

                if (string.IsNullOrEmpty(clipboardContent))
                {
                    Debug.WriteLine("Aucun texte sélectionné");
                    return;
                }

                Debug.WriteLine($"Texte sélectionné: {clipboardContent}");

                string correctedText = await CorrectTextWithLanguageTool(clipboardContent);

                if (correctedText != clipboardContent)
                {
                    if (previewMode)
                    {
                        pendingCorrectedText = correctedText;
                        Debug.WriteLine($"Texte de correction stocké: '{pendingCorrectedText}'");
                        hiddenWindow.Invoke(new Action(() => ShowPreview(clipboardContent, correctedText)));
                    }
                    else
                    {
                        hiddenWindow.Invoke(new Action(() =>
                        {
                            Clipboard.SetText(correctedText);
                        }));
                        Thread.Sleep(200);
                        SendCtrlV();
                        Debug.WriteLine($"Correction appliquée directement: {correctedText}");
                    }
                }
                else
                {
                    Debug.WriteLine("Aucune correction nécessaire");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Erreur générale: {ex}");
            }
        }

        private void ExitApplication()
        {
            UnregisterHotKey(hiddenWindow.Handle, HOTKEY_CTRL_ENTER);
            UnregisterHotKey(hiddenWindow.Handle, HOTKEY_CTRL_Y);
            UnregisterHotKey(hiddenWindow.Handle, HOTKEY_CTRL_N);
            UnregisterHotKey(hiddenWindow.Handle, HOTKEY_CTRL_ALT_ENTER);
            trayIcon?.Dispose();
            hiddenWindow?.Dispose();
            httpClient?.Dispose();
            ExitThread();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                ExitApplication();
            }
            base.Dispose(disposing);
        }
    }

    public class HiddenWindow : Form
    {
        public event Action<int> HotKeyPressed;

        protected override void WndProc(ref Message m)
        {
            const int WM_HOTKEY = 0x0312;
            if (m.Msg == WM_HOTKEY)
            {
                HotKeyPressed?.Invoke(m.WParam.ToInt32());
            }
            base.WndProc(ref m);
        }

        protected override void SetVisibleCore(bool value)
        {
            base.SetVisibleCore(false);
        }
    }

    public class PreviewForm : Form
    {
        public PreviewForm(string originalText, string correctedText)
        {
            InitializeComponent(originalText, correctedText);
            ApplyRoundedCorners();
        }

        private void ApplyRoundedCorners()
        {
            int cornerRadius = 15;
            System.Drawing.Drawing2D.GraphicsPath path = new System.Drawing.Drawing2D.GraphicsPath();

            path.AddArc(0, 0, cornerRadius, cornerRadius, 180, 90);
            path.AddArc(this.Width - cornerRadius, 0, cornerRadius, cornerRadius, 270, 90);
            path.AddArc(this.Width - cornerRadius, this.Height - cornerRadius, cornerRadius, cornerRadius, 0, 90);
            path.AddArc(0, this.Height - cornerRadius, cornerRadius, cornerRadius, 90, 90);
            path.CloseFigure();

            this.Region = new Region(path);
        }

        private void InitializeComponent(string originalText, string correctedText)
        {
            this.FormBorderStyle = FormBorderStyle.None;
            this.Size = new Size(500, 200);
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.ShowIcon = false;
            this.BackColor = Color.FromArgb(45, 45, 48);

            var panel = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(15),
                BackColor = Color.Transparent
            };

            var lblOriginal = new Label
            {
                Text = "Original:",
                Font = new Font("Segoe UI", 10, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(15, 15),
                ForeColor = Color.FromArgb(255, 107, 107),
                BackColor = Color.Transparent
            };

            var txtOriginal = new TextBox
            {
                Text = originalText,
                ReadOnly = true,
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                Font = new Font("Consolas", 9),
                BackColor = Color.FromArgb(255, 235, 235),
                ForeColor = Color.FromArgb(139, 0, 0),
                BorderStyle = BorderStyle.None,
                Size = new Size(460, 40),
                Location = new Point(15, 40)
            };

            var lblCorrected = new Label
            {
                Text = "Corrigé:",
                Font = new Font("Segoe UI", 10, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(15, 90),
                ForeColor = Color.FromArgb(144, 238, 144),
                BackColor = Color.Transparent
            };

            var txtCorrected = new TextBox
            {
                Text = correctedText,
                ReadOnly = true,
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                Font = new Font("Consolas", 9),
                BackColor = Color.FromArgb(235, 255, 235),
                ForeColor = Color.FromArgb(34, 139, 34),
                BorderStyle = BorderStyle.None,
                Size = new Size(460, 40),
                Location = new Point(15, 115)
            };

            var lblInstructions = new Label
            {
                Text = "Ctrl+Y pour accepter | Ctrl+N pour annuler | Ctrl+Alt+Enter pour changer le mode",
                Font = new Font("Segoe UI", 8, FontStyle.Italic),
                ForeColor = Color.FromArgb(200, 200, 200),
                BackColor = Color.Transparent,
                AutoSize = true,
                Location = new Point(15, 165)
            };

            panel.Controls.Add(lblOriginal);
            panel.Controls.Add(txtOriginal);
            panel.Controls.Add(lblCorrected);
            panel.Controls.Add(txtCorrected);
            panel.Controls.Add(lblInstructions);

            this.Controls.Add(panel);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            using (Pen pen = new Pen(Color.FromArgb(100, 100, 100), 1))
            {
                int cornerRadius = 15;
                System.Drawing.Drawing2D.GraphicsPath path = new System.Drawing.Drawing2D.GraphicsPath();

                path.AddArc(0, 0, cornerRadius, cornerRadius, 180, 90);
                path.AddArc(this.Width - cornerRadius - 1, 0, cornerRadius, cornerRadius, 270, 90);
                path.AddArc(this.Width - cornerRadius - 1, this.Height - cornerRadius - 1, cornerRadius, cornerRadius, 0, 90);
                path.AddArc(0, this.Height - cornerRadius - 1, cornerRadius, cornerRadius, 90, 90);
                path.CloseFigure();

                e.Graphics.DrawPath(pen, path);
            }
        }
    }

    public class LanguageToolResponse
    {
        [JsonProperty("matches")]
        public List<LanguageToolMatch> Matches { get; set; }
    }

    public class LanguageToolMatch
    {
        [JsonProperty("message")]
        public string Message { get; set; }

        [JsonProperty("offset")]
        public int Offset { get; set; }

        [JsonProperty("length")]
        public int Length { get; set; }

        [JsonProperty("replacements")]
        public List<LanguageToolReplacement> Replacements { get; set; }
    }

    public class LanguageToolReplacement
    {
        [JsonProperty("value")]
        public string Value { get; set; }
    }
}