using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SGFloatingTranslator
{
    // Uses the real controls and dictionary, with no hooks, credentials or cloud calls.
    internal static class UiSmokeTest
    {
        private static int failures;
        [StructLayout(LayoutKind.Sequential)]
        private struct Rect { internal int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        private struct CharRange { internal int Min, Max; }
        [StructLayout(LayoutKind.Sequential)]
        private struct FormatRange
        {
            internal IntPtr Hdc, Target;
            internal Rect Area, Page;
            internal CharRange Chars;
        }
        [DllImport("user32.dll", EntryPoint = "SendMessage")]
        private static extern IntPtr RenderRichText(IntPtr handle, int message, IntPtr render, ref FormatRange range);
        [STAThread]
        private static int Main(string[] args)
        {
            DpiAwareness.EnablePerMonitorV2();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string output = args.Length > 0 ? args[0] : "verification";
            Directory.CreateDirectory(output);
            using (FloatingTranslatorForm main = new FloatingTranslatorForm(true))
            {
                main.Show();
                Application.DoEvents();
                Save(main, output, "windows-main-empty");
                TextBox source = Field<TextBox>(main, "sourceBox");
                source.Text = "serendipity";
                Invoke(main, "TranslateManualText");
                Application.DoEvents();
                Check(Field<RichTextBox>(main, "translationBox").Text.Length > 0, "manual lookup renders result");
                Save(main, output, "windows-main-result");
                CheckVisibleChildren(main);
                main.ClientSize = main.MinimumSize;
                Application.DoEvents();
                CheckVisibleChildren(main);
                Save(main, output, "windows-main-small");
                main.Hide();
            }
            using (AiSettingsDialog settings = new AiSettingsDialog("deepseek", false, false, false, false, false, false, "deepseek-chat", "gemini-2.5-flash"))
            {
                settings.Show();
                Application.DoEvents();
                Check(Field<CheckBox>(settings, "consentBox").Height > 18, "consent is not collapsed");
                ComboBox provider = Field<ComboBox>(settings, "providerBox");
                ComboBox model = Field<ComboBox>(settings, "modelBox");
                model.Text = "custom-deepseek";
                provider.SelectedIndex = 1;
                model.Text = "custom-gemini";
                provider.SelectedIndex = 0;
                Check(model.Text == "custom-deepseek", "provider switch preserves each model");
                Save(settings, output, "windows-settings");
                CheckVisibleChildren(settings);
                settings.Hide();
            }
            OfflineDictionaryTranslator dictionary = new OfflineDictionaryTranslator();
            using (QuickTranslationPopup popup = new QuickTranslationPopup())
            {
                TranslationResult result = dictionary.Translate("serendipity");
                popup.ShowResult("serendipity", result, "serendipity", new Point(400, 280));
                Settle();
                Save(popup, output, "windows-popup");
                CheckVisibleChildren(popup);
                TranslationResult longResult = dictionary.Translate("understanding");
                popup.ShowResult("A thoughtful translation should never interrupt the flow of reading.", longResult, "", new Point(400, 280));
                Settle();
                Save(popup, output, "windows-popup-long");
                CheckVisibleChildren(popup);
                popup.Hide();
            }
            Console.WriteLine("UI checks: " + (failures == 0 ? "PASS" : "FAIL " + failures));
            return failures == 0 ? 0 : 1;
        }
        private static void Settle()
        {
            DateTime until = DateTime.UtcNow.AddMilliseconds(180);
            while (DateTime.UtcNow < until) { Application.DoEvents(); System.Threading.Thread.Sleep(10); }
        }
        private static T Field<T>(object value, string name)
        {
            return (T)value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(value);
        }
        private static void Invoke(object value, string name)
        {
            value.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(value, null);
        }
        private static void Check(bool okay, string name)
        {
            Console.WriteLine((okay ? "PASS " : "FAIL ") + name);
            if (!okay) failures++;
        }
        private static void Save(Form form, string output, string name)
        {
            using (Bitmap image = new Bitmap(form.ClientSize.Width, form.ClientSize.Height))
            {
                form.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
                RenderRichTextChildren(form, form, image);
                image.Save(Path.Combine(output, name + ".png"), ImageFormat.Png);
            }
        }
        private static void RenderRichTextChildren(Form form, Control parent, Bitmap image)
        {
            // RichEdit does not support DrawToBitmap; use its own print renderer.
            foreach (Control child in parent.Controls)
            {
                RichTextBox rich = child as RichTextBox;
                if (rich != null && rich.Visible && rich.Width > 0 && rich.Height > 0)
                {
                    using (Bitmap text = new Bitmap(rich.Width, rich.Height))
                    using (Graphics g = Graphics.FromImage(text))
                    {
                        float dpi;
                        using (Graphics display = rich.CreateGraphics()) { dpi = display.DpiX; }
                        text.SetResolution(dpi, dpi);
                        g.Clear(rich.BackColor);
                        IntPtr hdc = g.GetHdc();
                        FormatRange range = new FormatRange();
                        range.Hdc = hdc; range.Target = hdc;
                        range.Area.Right = (int)(rich.Width * 1440F / dpi);
                        range.Area.Bottom = (int)(rich.Height * 1440F / dpi);
                        range.Page = range.Area;
                        range.Chars.Min = 0; range.Chars.Max = -1;
                        try { RenderRichText(rich.Handle, 0x0439, new IntPtr(1), ref range); }
                        finally
                        {
                            g.ReleaseHdc(hdc);
                            NativeMethods.SendMessage(rich.Handle, 0x0439, IntPtr.Zero, IntPtr.Zero);
                        }
                        using (Graphics target = Graphics.FromImage(image))
                            target.DrawImageUnscaled(text, form.PointToClient(rich.PointToScreen(Point.Empty)));
                    }
                }
                RenderRichTextChildren(form, child, image);
            }
        }
        private static void CheckVisibleChildren(Control parent)
        {
            foreach (Control child in parent.Controls)
            {
                if (!child.Visible) continue;
                if (child is Button || child is CheckBox || child is TextBoxBase)
                {
                    Check(child.Width > 12 && child.Height > 12, "usable " + (child.AccessibleName ?? child.Text));
                    if (child.FindForm() is FloatingTranslatorForm)
                    {
                        Form form = child.FindForm();
                        Rectangle rect = new Rectangle(form.PointToClient(child.PointToScreen(Point.Empty)), child.Size);
                        Check(form.ClientRectangle.Contains(rect), "inside window " + (child.AccessibleName ?? child.Text));
                    }
                }
                CheckVisibleChildren(child);
            }
        }
    }
}
