using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Tahakkum
{
    static class Theme
    {
        // The brand: teal #1FAEB4, charcoal #0E1517, light gray #F5F6F6. Text on teal is charcoal,
        // because white on this teal is too faint to read comfortably.
        public static readonly Color Bg = Color.FromArgb(14, 21, 23);
        public static readonly Color Card = Color.FromArgb(22, 32, 35);
        public static readonly Color CardRaised = Color.FromArgb(31, 43, 47);
        public static readonly Color Border = Color.FromArgb(47, 62, 67);
        public static readonly Color Accent = Color.FromArgb(31, 174, 180);
        public static readonly Color AccentHover = Color.FromArgb(60, 194, 200);
        public static readonly Color AccentDown = Color.FromArgb(23, 140, 145);
        public static readonly Color OnAccent = Bg;
        public static readonly Color Success = Color.FromArgb(34, 197, 94);
        public static readonly Color SuccessHover = Color.FromArgb(58, 212, 116);
        public static readonly Color Danger = Color.FromArgb(239, 68, 68);
        public static readonly Color DangerHover = Color.FromArgb(246, 96, 96);
        public static readonly Color Warning = Color.FromArgb(245, 170, 60);
        public static readonly Color Text = Color.FromArgb(245, 246, 246);
        public static readonly Color Muted = Color.FromArgb(143, 163, 168);

        public const string GlyphConnect = "";
        public const string GlyphMonitor = "";
        public const string GlyphShield = "";
        public const string GlyphCheck = "";
        public const string GlyphClose = "";
        public const string GlyphFullscreen = "";
        public const string GlyphWindowed = "";
        public const string GlyphCopy = "";
        public const string GlyphKeyboard = "";
        public const string GlyphPower = "";
        public const string GlyphView = "";
        public const string GlyphPause = "";
        public const string GlyphPlay = "";
        public const string GlyphFit = "";
        public const string GlyphQuality = "";
        public const string GlyphHistory = "";
        public const string GlyphAdmin = "";

        public const string GlyphFile = "";
        public const string GlyphFolder = "";
        public const string GlyphAdd = "";
        public const string GlyphEdit = "";
        public const string GlyphDelete = "";

        public static float Scale = 1f;

        public static int S(int v)
        {
            return (int)Math.Round(v * Scale);
        }

        public static Font Ui(float size)
        {
            return new Font("Segoe UI", size, FontStyle.Regular, GraphicsUnit.Point);
        }

        public static Font UiBold(float size)
        {
            return new Font("Segoe UI Semibold", size, FontStyle.Regular, GraphicsUnit.Point);
        }

        public static Font Icons(float size)
        {
            return new Font("Segoe MDL2 Assets", size, FontStyle.Regular, GraphicsUnit.Point);
        }

        public static GraphicsPath Rounded(RectangleF r, float radius)
        {
            GraphicsPath p = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            if (d <= 0)
            {
                p.AddRectangle(r);
                return p;
            }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void DarkTitleBar(IntPtr hwnd)
        {
            try
            {
                int on = 1;
                Native.DwmSetWindowAttribute(hwnd, 20, ref on, 4);
                int caption = Bg.R | (Bg.G << 8) | (Bg.B << 16);
                Native.DwmSetWindowAttribute(hwnd, 35, ref caption, 4);
            }
            catch (Exception) { }
        }

        public static Label MakeLabel(string text, Font font, Color color, ContentAlignment align)
        {
            Label l = new Label();
            l.Text = text;
            l.Font = font;
            l.ForeColor = color;
            l.BackColor = Color.Transparent;
            l.TextAlign = align;
            l.AutoSize = false;
            l.RightToLeft = Lang.Rtl;
            l.UseMnemonic = false;
            return l;
        }

        public static ContextMenuStrip MakeMenu()
        {
            ContextMenuStrip m = new ContextMenuStrip();
            m.Renderer = new DarkMenuRenderer();
            m.RightToLeft = Lang.Rtl;
            m.ShowImageMargin = false;
            m.ShowCheckMargin = true;
            m.Font = Ui(10f);
            m.ForeColor = Text;
            m.BackColor = Card;
            return m;
        }
    }

    sealed class DarkMenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground { get { return Theme.Card; } }
        public override Color MenuBorder { get { return Theme.Border; } }
        public override Color MenuItemSelected { get { return Theme.CardRaised; } }
        public override Color MenuItemBorder { get { return Theme.CardRaised; } }
        public override Color ImageMarginGradientBegin { get { return Theme.Card; } }
        public override Color ImageMarginGradientMiddle { get { return Theme.Card; } }
        public override Color ImageMarginGradientEnd { get { return Theme.Card; } }
        public override Color SeparatorDark { get { return Theme.Border; } }
        public override Color SeparatorLight { get { return Theme.Border; } }
        public override Color CheckBackground { get { return Theme.Accent; } }
        public override Color CheckSelectedBackground { get { return Theme.Accent; } }
        public override Color CheckPressedBackground { get { return Theme.Accent; } }
    }

    sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer() : base(new DarkMenuColors())
        {
            RoundedEdges = false;
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Theme.Text : Theme.Muted;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Theme.Muted;
            base.OnRenderArrow(e);
        }
    }

    enum ButtonKind { Primary, Secondary, Danger, Success, Ghost }

    // Inherits Button so keyboard, focus, and accessibility behave like a native button.
    class PillButton : Button
    {
        bool hover;
        bool press;

        public ButtonKind Kind = ButtonKind.Secondary;
        public string Glyph;
        public float GlyphSize = 11f;
        public float Radius = 8f;

        public PillButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            Cursor = Cursors.Hand;
            Font = Theme.UiBold(10f);
            UseVisualStyleBackColor = false;
            UseMnemonic = false;
        }

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            AccessibleName = Text;
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; press = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { press = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { press = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        Color Fill()
        {
            if (!Enabled) return Theme.CardRaised;
            switch (Kind)
            {
                case ButtonKind.Primary: return press ? Theme.AccentDown : hover ? Theme.AccentHover : Theme.Accent;
                case ButtonKind.Danger: return hover ? Theme.DangerHover : Theme.Danger;
                case ButtonKind.Success: return hover ? Theme.SuccessHover : Theme.Success;
                case ButtonKind.Ghost: return hover ? Theme.CardRaised : Parent != null ? Parent.BackColor : Theme.Card;
                default: return hover ? Theme.Border : Theme.CardRaised;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Color parent = Parent != null ? Parent.BackColor : Theme.Bg;
            g.Clear(parent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            RectangleF box = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
            float radius = Theme.S((int)Radius);
            using (GraphicsPath path = Theme.Rounded(box, radius))
            using (SolidBrush b = new SolidBrush(Fill()))
            {
                g.FillPath(b, path);
                if (Focused && ShowFocusCues)
                {
                    using (Pen pen = new Pen(Color.White, 1.5f)) g.DrawPath(pen, path);
                }
            }

            Color fore = !Enabled ? Theme.Muted
                : (Kind == ButtonKind.Secondary || Kind == ButtonKind.Ghost) ? Theme.Text
                : (Kind == ButtonKind.Primary || Kind == ButtonKind.Success) ? Theme.OnAccent
                : Color.White;
            TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix;
            Size textSize = string.IsNullOrEmpty(Text) ? Size.Empty : TextRenderer.MeasureText(g, Text, Font, Size.Empty, flags | TextFormatFlags.RightToLeft);
            int iconWidth = 0;
            Font icon = null;
            if (!string.IsNullOrEmpty(Glyph))
            {
                icon = Theme.Icons(GlyphSize);
                iconWidth = TextRenderer.MeasureText(g, Glyph, icon, Size.Empty, TextFormatFlags.NoPadding).Width;
            }
            int gap = iconWidth > 0 && textSize.Width > 0 ? Theme.S(8) : 0;
            int total = iconWidth + gap + textSize.Width;
            int x = (Width - total) / 2;
            // Reading order is right to left: the icon sits on the right of the label.
            if (textSize.Width > 0)
                TextRenderer.DrawText(g, Text, Font, new Rectangle(x, 0, textSize.Width + 2, Height), fore, flags | TextFormatFlags.RightToLeft);
            if (icon != null)
            {
                TextRenderer.DrawText(g, Glyph, icon, new Rectangle(x + textSize.Width + gap, 0, iconWidth + 2, Height), fore, flags);
                icon.Dispose();
            }
        }
    }

    // A checkbox drawn as a switch, with its caption to the left of it.
    sealed class ToggleSwitch : CheckBox
    {
        public ToggleSwitch()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            AutoSize = false;
            Cursor = Cursors.Hand;
            Font = Theme.Ui(10f);
            ForeColor = Theme.Text;
            UseMnemonic = false;
        }

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            AccessibleName = Text;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Card);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int h = Theme.S(22);
            int w = Theme.S(42);
            int y = (Height - h) / 2;
            int x = Width - w - 1;
            using (GraphicsPath track = Theme.Rounded(new RectangleF(x, y, w, h), h / 2f))
            using (SolidBrush b = new SolidBrush(Checked ? Theme.Accent : Theme.Border))
                g.FillPath(b, track);
            int knob = h - Theme.S(6);
            int kx = Checked ? x + Theme.S(3) : x + w - knob - Theme.S(3);
            g.FillEllipse(Brushes.White, kx, y + Theme.S(3), knob, knob);
            TextRenderer.DrawText(g, Text, Font, new Rectangle(0, 0, x - Theme.S(10), Height), ForeColor,
                TextFormatFlags.Right | TextFormatFlags.RightToLeft | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        }
    }

    sealed class CardPanel : Panel
    {
        public CardPanel()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Card;
            RightToLeft = RightToLeft.No;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent != null ? Parent.BackColor : Theme.Bg);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath p = Theme.Rounded(new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f), Theme.S(14)))
            using (SolidBrush b = new SolidBrush(BackColor))
            using (Pen pen = new Pen(Theme.Border))
            {
                e.Graphics.FillPath(b, p);
                e.Graphics.DrawPath(pen, p);
            }
        }
    }

    sealed class AddressBox : Panel
    {
        public readonly TextBox Box = new TextBox();

        public AddressBox()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.CardRaised;
            RightToLeft = RightToLeft.No;
            Box.BorderStyle = BorderStyle.None;
            Box.BackColor = Theme.CardRaised;
            Box.ForeColor = Theme.Text;
            Box.Font = Theme.UiBold(16f);
            Box.TextAlign = HorizontalAlignment.Center;
            Box.RightToLeft = RightToLeft.No;
            Box.GotFocus += delegate { Invalidate(); };
            Box.LostFocus += delegate { Invalidate(); };
            Controls.Add(Box);
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            Box.Width = Width - Theme.S(24);
            Box.Left = Theme.S(12);
            Box.Top = (Height - Box.Height) / 2;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent != null ? Parent.BackColor : Theme.Card);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath p = Theme.Rounded(new RectangleF(0.75f, 0.75f, Width - 1.5f, Height - 1.5f), Theme.S(10)))
            using (SolidBrush b = new SolidBrush(BackColor))
            using (Pen pen = new Pen(Box.Focused ? Theme.Accent : Theme.Border, 1.5f))
            {
                e.Graphics.FillPath(b, p);
                e.Graphics.DrawPath(pen, p);
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Box.Focus();
        }
    }

    sealed class StatusDot : Control
    {
        Color color = Theme.Success;

        public StatusDot()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
        }

        public Color Color
        {
            get { return color; }
            set { color = value; Invalidate(); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent != null ? Parent.BackColor : Theme.Card);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (SolidBrush b = new SolidBrush(color))
                e.Graphics.FillEllipse(b, 1, 1, Width - 3, Height - 3);
        }
    }

    // The brand symbol, drawn rather than scaled from a picture so it stays sharp at any DPI.
    // build.ps1 draws the same shape for the program's icon.
    sealed class LogoView : Control
    {
        public LogoView()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // Drawn on a 200 unit square, of which the mark takes the middle 140.
            float k = Math.Min(Width, Height) / 140f;
            g.ScaleTransform(k, k);
            g.TranslateTransform(-30f, -30f);
            using (Pen ring = new Pen(Color.White, 20f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawArc(ring, 45f, 45f, 110f, 110f, 0f, 270f);
            using (SolidBrush teal = new SolidBrush(Theme.Accent))
            using (Pen edge = new Pen(Theme.Accent, 3f) { LineJoin = LineJoin.Round })
            using (GraphicsPath cursor = new GraphicsPath())
            {
                g.FillEllipse(teal, 127f, 50f, 20f, 20f);
                cursor.AddPolygon(new[]
                {
                    new PointF(79, 68), new PointF(79, 124), new PointF(92, 112), new PointF(100, 132),
                    new PointF(115, 125), new PointF(106, 105), new PointF(122, 105)
                });
                g.FillPath(teal, cursor);
                g.DrawPath(edge, cursor);
            }
        }
    }

    // The calligraphic product name, from the brand artwork. Embedded by build.ps1; a build
    // without it, such as a test build, simply shows nothing here.
    sealed class WordmarkView : Control
    {
        static readonly Image Art = LoadArt();

        static Image LoadArt()
        {
            using (System.IO.Stream s = typeof(WordmarkView).Assembly.GetManifestResourceStream("wordmark.png"))
                return s == null ? null : new Bitmap(s);
        }

        public WordmarkView()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent != null ? Parent.BackColor : Theme.Bg);
            if (Art == null) return;
            e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            int w = Height * Art.Width / Art.Height;
            e.Graphics.DrawImage(Art, Width - w, 0, w, Height);
        }
    }

    sealed class GlyphView : Control
    {
        public string Glyph;
        public float GlyphSize = 20f;

        public GlyphView()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }

        public Color Tile = Theme.Accent;

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent != null ? Parent.BackColor : Theme.Bg);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath p = Theme.Rounded(new RectangleF(0, 0, Width - 1f, Height - 1f), Theme.S(10)))
            using (SolidBrush b = new SolidBrush(Tile))
                e.Graphics.FillPath(b, p);
            using (Font f = Theme.Icons(GlyphSize))
                TextRenderer.DrawText(e.Graphics, Glyph, f, ClientRectangle, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }
}
