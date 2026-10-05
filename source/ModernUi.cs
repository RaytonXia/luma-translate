using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SGFloatingTranslator
{
    // Keep classic GDI controls opaque: extending DWM glass through them causes
    // black edges and damaged text. macOS uses native behind-window material;
    // Windows uses a deterministic mist surface with native rounded chrome.
    internal class GlassForm : Form
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (SystemInformation.HighContrast) return;
            try
            {
                int rounded = 2;
                DwmSetWindowAttribute(Handle, 33, ref rounded, sizeof(int));
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            catch (System.Security.SecurityException) { }
        }
    }

    internal static class UiAccessibility
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SystemParametersInfo(uint action, uint param, out bool value, uint flags);
        internal static bool Animate
        {
            get
            {
                bool enabled;
                return !SystemInformation.HighContrast &&
                    SystemParametersInfo(0x1042, 0, out enabled, 0) && enabled;
            }
        }
    }

    // Mist / sage: shared semantic colors for all native windows.
    internal static class UiPalette
    {
        internal static readonly Color Ink = Color.FromArgb(35, 55, 50);
        internal static readonly Color Muted = Color.FromArgb(96, 116, 109);
        internal static readonly Color Surface = Color.FromArgb(237, 244, 240);
        internal static readonly Color Card = Color.FromArgb(251, 253, 252);
        internal static readonly Color Border = Color.FromArgb(214, 227, 219);
        internal static readonly Color Teal = Color.FromArgb(58, 113, 91);
        internal static readonly Color TealDark = Color.FromArgb(43, 86, 70);
        internal static readonly Color Blue = Color.FromArgb(74, 111, 119);
        internal static readonly Color Violet = Color.FromArgb(75, 105, 96);
        internal static readonly Color Coral = Color.FromArgb(165, 76, 68);
        internal static readonly Color Mint = Color.FromArgb(228, 239, 232);
        internal static readonly Color Lavender = Color.FromArgb(236, 241, 239);
    }

    internal static class DpiLayout
    {
        /// <summary>
        /// For manually sized dialogs only. Do not also call Control.Scale on the
        /// same tree: it already scales absolute table styles on this runtime.
        /// </summary>
        internal static void ScaleTableStyles(Control parent, float factor)
        {
            if (parent == null || factor <= 1.01F) return;
            TableLayoutPanel table = parent as TableLayoutPanel;
            if (table != null)
            {
                foreach (ColumnStyle column in table.ColumnStyles)
                {
                    if (column.SizeType == SizeType.Absolute) column.Width *= factor;
                }
                foreach (RowStyle row in table.RowStyles)
                {
                    if (row.SizeType == SizeType.Absolute) row.Height *= factor;
                }
            }
            foreach (Control child in parent.Controls) ScaleTableStyles(child, factor);
        }

        internal static float ScreenScaleFactor(Control control)
        {
            try
            {
                using (Graphics graphics = control.CreateGraphics())
                    return Math.Max(1F, graphics.DpiX / 96F);
            }
            catch
            {
                return 1F;
            }
        }
    }

    internal static class RoundedGeometry
    {
        internal static GraphicsPath Create(Rectangle bounds, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                path.AddRectangle(bounds);
                return path;
            }
            int diameter = Math.Max(2, Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height)));
            if (diameter <= 2)
            {
                path.AddRectangle(bounds);
                return path;
            }
            Rectangle arc = new Rectangle(bounds.X, bounds.Y, diameter, diameter);
            path.AddArc(arc, 180, 90);
            arc.X = bounds.Right - diameter;
            path.AddArc(arc, 270, 90);
            arc.Y = bounds.Bottom - diameter;
            path.AddArc(arc, 0, 90);
            arc.X = bounds.X;
            path.AddArc(arc, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    internal class ModernGradientPanel : Panel
    {
        private Color startColor;
        private Color endColor;
        private Color borderColor;
        private float gradientAngle;
        private int cornerRadius;

        internal Color StartColor { get { return startColor; } set { startColor = value; Invalidate(); } }
        internal Color EndColor { get { return endColor; } set { endColor = value; Invalidate(); } }
        internal Color BorderColor { get { return borderColor; } set { borderColor = value; Invalidate(); } }
        internal float GradientAngle { get { return gradientAngle; } set { gradientAngle = value; Invalidate(); } }
        internal int CornerRadius { get { return cornerRadius; } set { cornerRadius = Math.Max(0, value); UpdateRegion(); Invalidate(); } }

        internal ModernGradientPanel()
        {
            startColor = UiPalette.Card;
            endColor = UiPalette.Card;
            borderColor = Color.Transparent;
            gradientAngle = 30F;
            cornerRadius = 18;
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw |
                     ControlStyles.UserPaint |
                     ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
        }

        protected override void OnResize(EventArgs eventArgs)
        {
            base.OnResize(eventArgs);
            UpdateRegion();
        }

        private void UpdateRegion()
        {
            if (Width <= 0 || Height <= 0) return;
            Region old = Region;
            using (GraphicsPath path = RoundedGeometry.Create(
                new Rectangle(0, 0, Width, Height),
                ScaleLogical(cornerRadius)))
            {
                Region = new Region(path);
            }
            if (old != null) old.Dispose();
        }

        protected override void OnPaintBackground(PaintEventArgs eventArgs)
        {
            eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle bounds = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
            using (GraphicsPath path = RoundedGeometry.Create(bounds, ScaleLogical(cornerRadius)))
            using (LinearGradientBrush brush = new LinearGradientBrush(bounds, startColor, endColor, gradientAngle))
            {
                eventArgs.Graphics.FillPath(brush, path);
                if (borderColor.A > 0)
                {
                    using (Pen pen = new Pen(borderColor, Math.Max(1F, DeviceDpi / 96F)))
                        eventArgs.Graphics.DrawPath(pen, path);
                }
            }
        }

        private int ScaleLogical(int value)
        {
            return Math.Max(1, (int)Math.Round(value * Math.Max(96, DeviceDpi) / 96F));
        }
    }

    internal sealed class ModernButton : Button
    {
        private bool hovered;
        private bool pressed;
        private Color startColor;
        private Color endColor;
        private Color borderColor;
        private int cornerRadius;

        internal Color StartColor { get { return startColor; } set { startColor = value; Invalidate(); } }
        internal Color EndColor { get { return endColor; } set { endColor = value; Invalidate(); } }
        internal Color BorderColor { get { return borderColor; } set { borderColor = value; Invalidate(); } }
        internal int CornerRadius { get { return cornerRadius; } set { cornerRadius = Math.Max(4, value); Invalidate(); } }

        internal ModernButton()
        {
            startColor = UiPalette.Teal;
            endColor = UiPalette.Teal;
            borderColor = Color.Transparent;
            cornerRadius = 11;
            ForeColor = Color.White;
            Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Regular);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            FlatAppearance.MouseOverBackColor = Color.Transparent;
            FlatAppearance.MouseDownBackColor = Color.Transparent;
            Cursor = Cursors.Hand;
            UseVisualStyleBackColor = false;
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw |
                     ControlStyles.UserPaint |
                     ControlStyles.SupportsTransparentBackColor, true);
            SetStyle(ControlStyles.Opaque, false);
            BackColor = Color.Transparent;
        }

        protected override void OnMouseEnter(EventArgs eventArgs) { hovered = true; Invalidate(); base.OnMouseEnter(eventArgs); }
        protected override void OnMouseLeave(EventArgs eventArgs) { hovered = false; pressed = false; Invalidate(); base.OnMouseLeave(eventArgs); }
        protected override void OnMouseDown(MouseEventArgs eventArgs) { if (eventArgs.Button == MouseButtons.Left) pressed = true; Invalidate(); base.OnMouseDown(eventArgs); }
        protected override void OnMouseUp(MouseEventArgs eventArgs) { pressed = false; Invalidate(); base.OnMouseUp(eventArgs); }

        protected override void OnPaint(PaintEventArgs eventArgs)
        {
            eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle bounds = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
            Color first = !Enabled ? UiPalette.Lavender : pressed ? ControlPaint.Dark(startColor, 0.10F) : (hovered ? ControlPaint.Light(startColor, 0.10F) : startColor);
            Color second = !Enabled ? UiPalette.Lavender : pressed ? ControlPaint.Dark(endColor, 0.10F) : (hovered ? ControlPaint.Light(endColor, 0.10F) : endColor);
            using (GraphicsPath path = RoundedGeometry.Create(bounds, ScaleLogical(cornerRadius)))
            using (LinearGradientBrush brush = new LinearGradientBrush(bounds, first, second, 18F))
            {
                eventArgs.Graphics.FillPath(brush, path);
                if (borderColor.A > 0)
                {
                    using (Pen pen = new Pen(borderColor, Math.Max(1F, DeviceDpi / 96F)))
                        eventArgs.Graphics.DrawPath(pen, path);
                }
            }
            TextRenderer.DrawText(
                eventArgs.Graphics,
                Text,
                Font,
                bounds,
                Enabled ? ForeColor : UiPalette.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            if (Focused && ShowFocusCues)
            {
                Rectangle focus = Rectangle.Inflate(bounds, -4, -4);
                ControlPaint.DrawFocusRectangle(eventArgs.Graphics, focus, ForeColor, Color.Transparent);
            }
        }

        public override Size GetPreferredSize(Size proposedSize)
        {
            Size textSize = TextRenderer.MeasureText(
                Text ?? String.Empty,
                Font,
                new Size(Int32.MaxValue, Int32.MaxValue),
                TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            return new Size(
                Math.Max(MinimumSize.Width, textSize.Width + ScaleLogical(28)),
                Math.Max(MinimumSize.Height, ScaleLogical(34)));
        }

        private int ScaleLogical(int value)
        {
            return Math.Max(1, (int)Math.Round(value * Math.Max(96, DeviceDpi) / 96F));
        }
    }

    internal sealed class ModernPillToggle : CheckBox
    {
        internal ModernPillToggle()
        {
            Appearance = Appearance.Button;
            AutoSize = false;
            Size = new Size(116, 34);
            TextAlign = ContentAlignment.MiddleCenter;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            FlatAppearance.MouseOverBackColor = Color.Transparent;
            FlatAppearance.MouseDownBackColor = Color.Transparent;
            Cursor = Cursors.Hand;
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular);
            UseVisualStyleBackColor = false;
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw |
                     ControlStyles.UserPaint |
                     ControlStyles.SupportsTransparentBackColor, true);
            SetStyle(ControlStyles.Opaque, false);
            BackColor = Color.Transparent;
        }

        protected override void OnPaint(PaintEventArgs eventArgs)
        {
            eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle bounds = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
            Color start = Checked ? UiPalette.Mint : UiPalette.Card;
            Color end = start;
            Color textColor = Checked ? UiPalette.TealDark : UiPalette.Muted;
            using (GraphicsPath path = RoundedGeometry.Create(bounds, Math.Max(4, Height / 2 - 1)))
            using (LinearGradientBrush brush = new LinearGradientBrush(bounds, start, end, 12F))
            {
                eventArgs.Graphics.FillPath(brush, path);
                using (Pen pen = new Pen(
                    Checked ? Color.FromArgb(168, 199, 183) : UiPalette.Border,
                    Math.Max(1F, DeviceDpi / 96F)))
                    eventArgs.Graphics.DrawPath(pen, path);
            }
            TextRenderer.DrawText(eventArgs.Graphics, Text, Font, bounds, textColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            if (Focused && ShowFocusCues)
                ControlPaint.DrawFocusRectangle(eventArgs.Graphics,
                    Rectangle.Inflate(bounds, -5, -5), UiPalette.TealDark, BackColor);
        }

        public override Size GetPreferredSize(Size proposedSize)
        {
            Size textSize = TextRenderer.MeasureText(
                Text ?? String.Empty,
                Font,
                new Size(Int32.MaxValue, Int32.MaxValue),
                TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            return new Size(
                Math.Max(MinimumSize.Width, textSize.Width + ScaleLogical(28)),
                Math.Max(MinimumSize.Height, ScaleLogical(34)));
        }

        private int ScaleLogical(int value)
        {
            return Math.Max(1, (int)Math.Round(value * Math.Max(96, DeviceDpi) / 96F));
        }
    }
}
