using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SGFloatingTranslator
{
    internal static class UiPalette
    {
        internal static readonly Color Ink = Color.FromArgb(30, 38, 54);
        internal static readonly Color Muted = Color.FromArgb(104, 114, 134);
        internal static readonly Color Surface = Color.FromArgb(250, 251, 255);
        internal static readonly Color Card = Color.FromArgb(255, 255, 255);
        internal static readonly Color Border = Color.FromArgb(224, 230, 241);
        internal static readonly Color Teal = Color.FromArgb(18, 138, 125);
        internal static readonly Color TealDark = Color.FromArgb(11, 88, 83);
        internal static readonly Color Blue = Color.FromArgb(82, 132, 221);
        internal static readonly Color Violet = Color.FromArgb(109, 88, 212);
        internal static readonly Color Coral = Color.FromArgb(240, 118, 96);
        internal static readonly Color Mint = Color.FromArgb(223, 247, 241);
        internal static readonly Color Lavender = Color.FromArgb(238, 234, 255);
    }

    internal static class DpiLayout
    {
        /// <summary>
        /// Classic .NET Framework auto-scaling resizes controls and fonts but NEVER
        /// scales TableLayoutPanel absolute row/column sizes, so at 125%/150% display
        /// scaling fixed cells stay small and clip their scaled content (the cropped
        /// logo and cut-off text). Walk the tree once and scale those styles manually.
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
            endColor = UiPalette.Violet;
            borderColor = Color.Transparent;
            cornerRadius = 11;
            ForeColor = Color.White;
            Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold);
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
            Color first = pressed ? ControlPaint.Dark(startColor, 0.10F) : (hovered ? ControlPaint.Light(startColor, 0.10F) : startColor);
            Color second = pressed ? ControlPaint.Dark(endColor, 0.10F) : (hovered ? ControlPaint.Light(endColor, 0.10F) : endColor);
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
                Enabled ? ForeColor : Color.FromArgb(165, ForeColor),
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
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
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
            Color start = Checked ? UiPalette.Teal : Color.FromArgb(236, 239, 247);
            Color end = Checked ? UiPalette.Blue : Color.FromArgb(246, 247, 251);
            Color textColor = Checked ? Color.White : UiPalette.Muted;
            using (GraphicsPath path = RoundedGeometry.Create(bounds, Math.Max(4, Height / 2 - 1)))
            using (LinearGradientBrush brush = new LinearGradientBrush(bounds, start, end, 12F))
            {
                eventArgs.Graphics.FillPath(brush, path);
                using (Pen pen = new Pen(
                    Checked ? Color.Transparent : UiPalette.Border,
                    Math.Max(1F, DeviceDpi / 96F)))
                    eventArgs.Graphics.DrawPath(pen, path);
            }
            TextRenderer.DrawText(eventArgs.Graphics, Text, Font, bounds, textColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
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
