using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Text;
using System.Windows.Forms;

namespace SGFloatingTranslator
{
    internal sealed class AiSettingsDialog : Form
    {
        private readonly ComboBox providerBox;
        private readonly ComboBox modelBox;
        private string deepSeekModelText;
        private string geminiModelText;
        private int lastProviderIndex;
        private readonly TextBox deepSeekKeyBox;
        private readonly TextBox geminiKeyBox;
        private readonly CheckBox clearDeepSeekBox;
        private readonly CheckBox clearGeminiBox;
        private readonly CheckBox persistBox;
        private readonly CheckBox consentBox;
        private readonly Label privacyLabel;
        private readonly Label statusLabel;
        private readonly bool hasDeepSeekKey;
        private readonly bool hasGeminiKey;
        private readonly bool deepSeekEnvironmentKey;
        private readonly bool geminiEnvironmentKey;

        private readonly float uiScale;

        internal string SelectedProvider { get; private set; }
        internal string DeepSeekKey { get; private set; }
        internal string GeminiKey { get; private set; }
        internal string DeepSeekModel { get; private set; }
        internal string GeminiModel { get; private set; }
        internal bool Persist { get; private set; }
        internal bool ClearDeepSeekKey { get { return clearDeepSeekBox.Checked; } }
        internal bool ClearGeminiKey { get { return clearGeminiBox.Checked; } }

        internal AiSettingsDialog(
            string selectedProvider,
            bool deepSeekConfigured,
            bool geminiConfigured,
            bool deepSeekFromEnvironment,
            bool geminiFromEnvironment,
            bool deepSeekApplicationManaged,
            bool geminiApplicationManaged,
            string deepSeekModel,
            string geminiModel)
        {
            hasDeepSeekKey = deepSeekConfigured;
            hasGeminiKey = geminiConfigured;
            deepSeekEnvironmentKey = deepSeekFromEnvironment;
            geminiEnvironmentKey = geminiFromEnvironment;
            SelectedProvider = String.Equals(selectedProvider, "gemini", StringComparison.OrdinalIgnoreCase)
                ? "gemini" : "deepseek";

            Text = "AI 接口设置";
            Font = new Font("Microsoft YaHei UI", 9.5F);
            // Fully manual sizing, like the dictionary bubble. Framework auto-scaling of
            // this borderless dialog was unreliable on scaled displays: the flexible
            // privacy row collapsed to zero height and hid the consent checkbox, which
            // silently blocked saving. Every row is now a fixed, DPI-multiplied height.
            AutoScaleMode = AutoScaleMode.None;
            uiScale = DpiLayout.ScreenScaleFactor(this);
            ClientSize = new Size(S(640), S(640));
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            TopMost = false;
            BackColor = Color.FromArgb(232, 237, 246);
            Padding = new Padding(1);
            DoubleBuffered = true;

            ModernGradientPanel canvas = new ModernGradientPanel();
            canvas.Dock = DockStyle.Fill;
            canvas.CornerRadius = 24;
            canvas.StartColor = Color.FromArgb(249, 252, 253);
            canvas.EndColor = Color.FromArgb(243, 240, 253);
            canvas.GradientAngle = 24F;
            canvas.Padding = new Padding(S(20));
            Controls.Add(canvas);

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.BackColor = Color.Transparent;
            // The original code set RowCount = 8 with GrowStyle = FixedSize but never set
            // ColumnCount, so the fixed capacity was 8 rows × 0 columns = 0 cells and adding
            // the very first control threw "TableLayoutPanel is full and its GrowStyle is
            // FixedSize" — the AI settings dialog could never open. Declare the single column
            // AND use AddRows so the panel can never overflow even if the row list changes.
            root.ColumnCount = 1;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowCount = 9;
            root.GrowStyle = TableLayoutPanelGrowStyle.AddRows;
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 86));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 86));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            // Fixed height (not Percent): the consent checkbox lives here and must
            // never be squeezed out of view by the surrounding rows.
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 118));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
            canvas.Controls.Add(root);

            TableLayoutPanel titleRow = new TableLayoutPanel();
            titleRow.Dock = DockStyle.Fill;
            titleRow.BackColor = Color.Transparent;
            titleRow.ColumnCount = 2;
            titleRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            titleRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 48));
            Label title = new Label();
            title.Dock = DockStyle.Fill;
            title.Text = "AI 翻译设置";
            title.Font = new Font("Microsoft YaHei UI", 18F, FontStyle.Bold);
            title.ForeColor = UiPalette.Ink;
            title.TextAlign = ContentAlignment.MiddleLeft;
            ModernButton close = NewButton("×", Color.FromArgb(239, 233, 249), Color.FromArgb(247, 238, 242), UiPalette.Muted);
            close.Size = new Size(S(40), S(40));
            close.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            close.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            titleRow.Controls.Add(title, 0, 0);
            titleRow.Controls.Add(close, 1, 0);
            root.Controls.Add(titleRow, 0, 0);
            WireDrag(titleRow);
            WireDrag(title);

            TableLayoutPanel providerRow = FieldRow("首选服务", out providerBox);
            providerBox.Items.Add("DeepSeek");
            providerBox.Items.Add("Gemini · Google");
            providerBox.SelectedIndex = SelectedProvider == "gemini" ? 1 : 0;
            providerBox.SelectedIndexChanged += delegate
            {
                // Keep each provider's model text while flipping between them.
                StoreModelText(lastProviderIndex);
                lastProviderIndex = providerBox.SelectedIndex;
                PopulateModelBox();
                UpdatePrivacyCopy();
                // Consent is stored per service host; reflect the saved decision for the
                // provider now selected instead of forcing a fresh tick on every visit.
                consentBox.Checked = AppStorage.HasCloudConsent(CurrentProviderHost());
            };
            root.Controls.Add(providerRow, 0, 1);

            deepSeekModelText = String.IsNullOrWhiteSpace(deepSeekModel) ? "deepseek-v4-flash" : deepSeekModel.Trim();
            geminiModelText = String.IsNullOrWhiteSpace(geminiModel) ? "gemini-3.5-flash-lite" : geminiModel.Trim();
            TableLayoutPanel modelRow = FieldRow("模型", out modelBox);
            modelBox.DropDownStyle = ComboBoxStyle.DropDown; // editable: any model id can be typed
            modelBox.AccessibleName = "当前服务使用的模型";
            lastProviderIndex = providerBox.SelectedIndex;
            PopulateModelBox();
            root.Controls.Add(modelRow, 0, 2);

            deepSeekKeyBox = NewKeyBox();
            CheckBox deepSeekClear;
            root.Controls.Add(KeyRow(
                "DeepSeek",
                KeyHint(hasDeepSeekKey, deepSeekEnvironmentKey, deepSeekApplicationManaged, "从 platform.deepseek.com 获取"),
                deepSeekKeyBox,
                "https://platform.deepseek.com/api_keys",
                deepSeekApplicationManaged,
                out deepSeekClear), 0, 3);
            clearDeepSeekBox = deepSeekClear;

            geminiKeyBox = NewKeyBox();
            CheckBox geminiClear;
            root.Controls.Add(KeyRow(
                "Gemini",
                KeyHint(hasGeminiKey, geminiEnvironmentKey, geminiApplicationManaged, "从 Google AI Studio 获取"),
                geminiKeyBox,
                "https://aistudio.google.com/apikey",
                geminiApplicationManaged,
                out geminiClear), 0, 4);
            clearGeminiBox = geminiClear;

            persistBox = new CheckBox();
            persistBox.Dock = DockStyle.Fill;
            persistBox.Text = "新密钥使用 Windows DPAPI 加密保存";
            persistBox.ForeColor = UiPalette.Ink;
            persistBox.Checked = true;
            persistBox.Padding = new Padding(6, 0, 0, 0);
            persistBox.AutoEllipsis = true;
            root.Controls.Add(persistBox, 0, 5);

            ModernGradientPanel privacyCard = new ModernGradientPanel();
            privacyCard.Dock = DockStyle.Fill;
            privacyCard.CornerRadius = 16;
            privacyCard.StartColor = Color.FromArgb(226, 248, 242);
            privacyCard.EndColor = Color.FromArgb(237, 231, 252);
            privacyCard.BorderColor = UiPalette.Border;
            privacyCard.Padding = new Padding(S(14), S(10), S(14), S(10));
            TableLayoutPanel privacyLayout = new TableLayoutPanel();
            privacyLayout.Dock = DockStyle.Fill;
            privacyLayout.BackColor = Color.Transparent;
            privacyLayout.RowCount = 2;
            privacyLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            privacyLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            privacyLabel = new Label();
            privacyLabel.Dock = DockStyle.Fill;
            privacyLabel.ForeColor = UiPalette.Ink;
            privacyLabel.Font = new Font("Microsoft YaHei UI", 9.2F);
            privacyLabel.TextAlign = ContentAlignment.MiddleLeft;
            consentBox = new CheckBox();
            consentBox.Dock = DockStyle.Top;
            consentBox.AutoSize = true;
            consentBox.Text = "我同意在点击 AI 或长按拖拽时发送英文和密钥";
            consentBox.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
            consentBox.ForeColor = UiPalette.TealDark;
            consentBox.Margin = new Padding(0, 5, 0, 0);
            consentBox.Checked = AppStorage.HasCloudConsent(CurrentProviderHost());
            privacyLayout.Controls.Add(privacyLabel, 0, 0);
            privacyLayout.Controls.Add(consentBox, 0, 1);
            privacyCard.Controls.Add(privacyLayout);
            root.Controls.Add(privacyCard, 0, 6);

            statusLabel = new Label();
            statusLabel.Dock = DockStyle.Fill;
            statusLabel.ForeColor = Color.FromArgb(169, 65, 72);
            statusLabel.TextAlign = ContentAlignment.MiddleLeft;
            statusLabel.AutoEllipsis = true;
            root.Controls.Add(statusLabel, 0, 7);

            FlowLayoutPanel actions = new FlowLayoutPanel();
            actions.Dock = DockStyle.Fill;
            actions.FlowDirection = FlowDirection.RightToLeft;
            actions.WrapContents = true;
            actions.BackColor = Color.Transparent;
            ModernButton save = NewButton("保存设置", UiPalette.Teal, UiPalette.Blue, Color.White);
            save.Size = new Size(S(126), S(40));
            save.Click += SaveClicked;
            ModernButton cancel = NewButton("取消", Color.FromArgb(239, 242, 248), Color.FromArgb(247, 244, 252), UiPalette.Muted);
            cancel.BorderColor = UiPalette.Border;
            cancel.Size = new Size(S(92), S(40));
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            actions.Controls.Add(save);
            actions.Controls.Add(cancel);
            root.Controls.Add(actions, 0, 8);
            AcceptButton = save;

            DpiLayout.ScaleTableStyles(this, uiScale);
            UpdatePrivacyCopy();
            KeyDown += delegate(object sender, KeyEventArgs eventArgs)
            {
                if (eventArgs.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); }
            };
            KeyPreview = true;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams parameters = base.CreateParams;
                parameters.ClassStyle |= 0x00020000; // CS_DROPSHADOW
                return parameters;
            }
        }

        protected override void OnResize(EventArgs eventArgs)
        {
            base.OnResize(eventArgs);
            if (Width <= 0 || Height <= 0) return;
            Region old = Region;
            int radius = Math.Max(12, (int)Math.Round(24F * Math.Max(96, DeviceDpi) / 96F));
            using (GraphicsPath path = RoundedGeometry.Create(new Rectangle(0, 0, Width, Height), radius))
                Region = new Region(path);
            if (old != null) old.Dispose();
        }

        private int S(int logical)
        {
            return (int)Math.Round(logical * uiScale);
        }

        private string CurrentProviderHost()
        {
            return providerBox != null && providerBox.SelectedIndex == 1
                ? "generativelanguage.googleapis.com"
                : "api.deepseek.com";
        }

        private static TableLayoutPanel FieldRow(string labelText, out ComboBox box)
        {
            TableLayoutPanel row = new TableLayoutPanel();
            row.Dock = DockStyle.Fill;
            row.BackColor = Color.Transparent;
            row.ColumnCount = 2;
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 116));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Label label = new Label();
            label.Dock = DockStyle.Fill;
            label.Text = labelText;
            label.ForeColor = UiPalette.Ink;
            label.Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold);
            label.TextAlign = ContentAlignment.MiddleLeft;
            box = new ComboBox();
            box.Dock = DockStyle.Fill;
            box.DropDownStyle = ComboBoxStyle.DropDownList;
            box.FlatStyle = FlatStyle.Flat;
            box.Font = new Font("Microsoft YaHei UI", 10F);
            box.Margin = new Padding(0, 12, 0, 8);
            row.Controls.Add(label, 0, 0);
            row.Controls.Add(box, 1, 0);
            return row;
        }

        private static TableLayoutPanel KeyRow(
            string title,
            string hint,
            TextBox box,
            string linkUrl,
            bool hasExisting,
            out CheckBox clearBox)
        {
            TableLayoutPanel row = new TableLayoutPanel();
            row.Dock = DockStyle.Fill;
            row.BackColor = Color.Transparent;
            row.ColumnCount = 3;
            row.RowCount = 2;
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76));
            row.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Label label = new Label();
            label.Dock = DockStyle.Fill;
            label.Text = title;
            label.ForeColor = UiPalette.Ink;
            label.Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold);
            label.TextAlign = ContentAlignment.BottomLeft;
            LinkLabel link = new LinkLabel();
            link.Dock = DockStyle.Fill;
            link.Text = hint + "  ↗";
            link.TextAlign = ContentAlignment.BottomLeft;
            link.LinkColor = UiPalette.Violet;
            link.ActiveLinkColor = UiPalette.Coral;
            link.AutoEllipsis = true;
            link.LinkClicked += delegate
            {
                try { Process.Start(new ProcessStartInfo(linkUrl) { UseShellExecute = true }); }
                catch { }
            };
            clearBox = new CheckBox();
            clearBox.Dock = DockStyle.Fill;
            clearBox.Text = "删除";
            clearBox.AccessibleName = "删除应用内保存的密钥";
            clearBox.TextAlign = ContentAlignment.BottomRight;
            clearBox.ForeColor = Color.FromArgb(154, 61, 73);
            clearBox.Enabled = hasExisting;
            CheckBox capturedClear = clearBox;
            clearBox.CheckedChanged += delegate { box.Enabled = !capturedClear.Checked; };
            box.Margin = new Padding(0, 5, 0, 5);
            row.Controls.Add(label, 0, 0);
            row.Controls.Add(link, 1, 0);
            row.Controls.Add(clearBox, 2, 0);
            row.Controls.Add(box, 0, 1);
            row.SetColumnSpan(box, 3);
            return row;
        }

        private static string KeyHint(bool configured, bool fromEnvironment, bool applicationManaged, string emptyHint)
        {
            if (fromEnvironment && applicationManaged) return "环境变量 + 应用内密钥 · 粘贴新密钥可覆盖";
            if (fromEnvironment) return "Windows 环境变量密钥";
            if (configured) return "已配置 · 粘贴新密钥可直接覆盖";
            return emptyHint;
        }

        private void StoreModelText(int providerIndex)
        {
            if (modelBox == null) return;
            string value = modelBox.Text == null ? String.Empty : modelBox.Text.Trim();
            if (providerIndex == 1) geminiModelText = value;
            else deepSeekModelText = value;
        }

        private void PopulateModelBox()
        {
            if (modelBox == null) return;
            modelBox.Items.Clear();
            if (providerBox.SelectedIndex == 1)
            {
                modelBox.Items.Add("gemini-3.5-flash-lite");
                modelBox.Items.Add("gemini-3.5-flash");
                modelBox.Items.Add("gemini-3.5-pro");
                modelBox.Text = geminiModelText;
            }
            else
            {
                modelBox.Items.Add("deepseek-v4-flash");
                modelBox.Items.Add("deepseek-v4-pro");
                modelBox.Text = deepSeekModelText;
            }
        }

        private static string NormaliseModel(string value, string fallback)
        {
            string text = value == null ? String.Empty : value.Trim();
            if (text.Length == 0 || text.Length > 80) return fallback;
            foreach (char c in text)
            {
                bool allowed = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
                               (c >= '0' && c <= '9') || c == '-' || c == '_' || c == '.';
                if (!allowed) return fallback;
            }
            return text;
        }

        private static TextBox NewKeyBox()
        {
            TextBox box = new TextBox();
            box.Dock = DockStyle.Fill;
            box.BorderStyle = BorderStyle.FixedSingle;
            box.UseSystemPasswordChar = true;
            box.Font = new Font("Segoe UI", 10F);
            box.BackColor = Color.White;
            return box;
        }

        private static ModernButton NewButton(string text, Color start, Color end, Color foreground)
        {
            ModernButton button = new ModernButton();
            button.Text = text;
            button.StartColor = start;
            button.EndColor = end;
            button.ForeColor = foreground;
            button.Margin = new Padding(5, 2, 0, 2);
            return button;
        }

        private void UpdatePrivacyCopy()
        {
            bool deepSeek = providerBox.SelectedIndex != 1;
            privacyLabel.Text = deepSeek
                ? "点击 AI 或长按拖拽后，仅把识别到的英文与密钥发送至 api.deepseek.com；截图不会上传。"
                : "点击 AI 或长按拖拽后，仅把识别到的英文与密钥发送至 Google Gemini；截图不会上传。";
            bool environmentKey = deepSeek ? deepSeekEnvironmentKey : geminiEnvironmentKey;
            if (environmentKey)
                privacyLabel.Text += " 环境变量密钥需在 Windows 中管理。";
        }

        private void SaveClicked(object sender, EventArgs eventArgs)
        {
            if (!consentBox.Checked)
            {
                statusLabel.Text = "请先确认隐私说明。";
                return;
            }
            SelectedProvider = providerBox.SelectedIndex == 1 ? "gemini" : "deepseek";
            StoreModelText(providerBox.SelectedIndex);
            DeepSeekModel = NormaliseModel(deepSeekModelText, "deepseek-v4-flash");
            GeminiModel = NormaliseModel(geminiModelText, "gemini-3.5-flash-lite");
            DeepSeekKey = CleanKey(deepSeekKeyBox.Text);
            GeminiKey = CleanKey(geminiKeyBox.Text);
            // A pasted key sometimes arrives wrapped in quotes or split by a line break,
            // and double-click selection often stops at the dot in the new "AQ." Gemini
            // keys — catch obviously truncated keys before they cause a confusing 401.
            if ((DeepSeekKey.Length > 0 && DeepSeekKey.Length < 15) ||
                (GeminiKey.Length > 0 && GeminiKey.Length < 15))
            {
                statusLabel.Text = "密钥似乎不完整：请用平台的“复制”按钮复制整串密钥（含开头的 AQ. / sk-）。";
                return;
            }
            bool selectedConfigured = SelectedProvider == "deepseek"
                ? (DeepSeekKey.Length > 0 || (hasDeepSeekKey && !ClearDeepSeekKey))
                : (GeminiKey.Length > 0 || (hasGeminiKey && !ClearGeminiKey));
            bool selectedExplicitlyCleared = SelectedProvider == "deepseek"
                ? ClearDeepSeekKey
                : ClearGeminiKey;
            if (!selectedConfigured && !selectedExplicitlyCleared)
            {
                statusLabel.Text = "请填写当前所选服务的 API Key。";
                return;
            }
            Persist = persistBox.Checked;
            DialogResult = DialogResult.OK;
            Close();
        }

        /// <summary>
        /// Removes whitespace/control characters and wrapping quotes from a pasted key.
        /// Real API keys never contain spaces, so this only repairs copy artefacts.
        /// </summary>
        private static string CleanKey(string raw)
        {
            if (String.IsNullOrWhiteSpace(raw)) return String.Empty;
            StringBuilder builder = new StringBuilder(raw.Length);
            foreach (char c in raw)
            {
                if (Char.IsWhiteSpace(c) || Char.IsControl(c)) continue;
                builder.Append(c);
            }
            return builder.ToString().Trim('"', '\'', '“', '”', '‘', '’', '「', '」');
        }

        private void WireDrag(Control control)
        {
            control.MouseDown += delegate(object sender, MouseEventArgs eventArgs)
            {
                if (eventArgs.Button != MouseButtons.Left) return;
                NativeMethods.ReleaseCapture();
                NativeMethods.SendMessage(Handle, NativeMethods.WM_NCLBUTTONDOWN, new IntPtr(NativeMethods.HT_CAPTION), IntPtr.Zero);
            };
        }
    }
}
