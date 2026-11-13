using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Management;
using System.Media;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Charting = System.Windows.Forms.DataVisualization.Charting;

namespace LcrLogger
{
    // ------------------------------------------------------------------
    // 디자인 (밝은 회색 배경 + 둥근 흰 카드 + 파란 강조색)
    // ------------------------------------------------------------------

    static class Theme
    {
        public static readonly Color Bg = Color.FromArgb(245, 245, 247);
        public static readonly Color Card = Color.White;
        public static readonly Color Text = Color.FromArgb(29, 29, 31);
        public static readonly Color Secondary = Color.FromArgb(110, 110, 115);
        public static readonly Color Separator = Color.FromArgb(222, 222, 227);
        public static readonly Color Accent = Color.FromArgb(0, 113, 227);
        public static readonly Color AccentHover = Color.FromArgb(0, 119, 237);
        public static readonly Color AccentPressed = Color.FromArgb(0, 96, 196);
        public static readonly Color Fill = Color.FromArgb(232, 232, 237);
        public static readonly Color FillHover = Color.FromArgb(222, 222, 228);
        public static readonly Color FillPressed = Color.FromArgb(210, 210, 216);
        public static readonly Color Green = Color.FromArgb(36, 138, 61);
        public static readonly Color SwitchOn = Color.FromArgb(52, 199, 89);
        public static readonly Color Red = Color.FromArgb(215, 0, 21);
        public static readonly Color Selection = Color.FromArgb(232, 240, 254);
        public const string FontName = "맑은 고딕";
        public const string NumberFontName = "Segoe UI";

        public static System.Drawing.Drawing2D.GraphicsPath Rounded(RectangleF r, float radius)
        {
            float d = radius * 2;
            var path = new System.Drawing.Drawing2D.GraphicsPath();
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        public static Color ParentBack(Control c)
        {
            return c.Parent != null ? c.Parent.BackColor : Bg;
        }
    }

    // 제목이 위에 붙은 둥근 흰 카드
    class Card : Panel
    {
        readonly string title;
        const int Radius = 12;

        public Card(string title, bool autoSize)
        {
            this.title = title;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            BackColor = Theme.Bg;
            Dock = DockStyle.Fill;
            AutoSize = autoSize;
            if (autoSize) AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Margin = new Padding(0, 0, 0, 8);
            Padding = new Padding(10, title == null ? 8 : 28, 10, 6);
        }

        // 주어진 너비에서 안쪽 내용이 줄바꿈된 뒤의 높이를 돌려줌 (자동 크기 카드)
        public override Size GetPreferredSize(Size proposedSize)
        {
            int width = proposedSize.Width > 0 && proposedSize.Width < 10000 ? proposedSize.Width : Width;
            int inner = 0;
            foreach (Control c in Controls)
                inner = Math.Max(inner, c.GetPreferredSize(new Size(Math.Max(1, width - Padding.Horizontal), 0)).Height);
            return new Size(width, inner + Padding.Vertical);
        }

        int laidOutWidth = -1;

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            // 너비가 바뀌면 줄바꿈이 달라지므로 부모가 높이를 다시 계산하게 함
            if (AutoSize && Parent != null && Width != laidOutWidth)
            {
                laidOutWidth = Width;
                int want = GetPreferredSize(new Size(Width, 0)).Height;
                if (want != Height) Parent.PerformLayout();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using (var path = Theme.Rounded(r, Radius))
            using (var fill = new SolidBrush(Theme.Card))
            using (var border = new Pen(Theme.Separator))
            {
                g.FillPath(fill, path);
                g.DrawPath(border, path);
            }
            if (title != null)
            {
                using (var f = new Font(Theme.FontName, 9.5F, FontStyle.Bold))
                    TextRenderer.DrawText(g, title, f, new Point(14, 8), Theme.Text, Theme.Card);
            }
        }
    }

    // 알약 모양 버튼 (primary = 파란 배경)
    class PillButton : Button
    {
        readonly bool primary;
        bool hover, pressed;

        public PillButton(bool primary)
        {
            this.primary = primary;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            Cursor = Cursors.Hand;
            Margin = new Padding(4, 3, 4, 3);
            AccessibleRole = AccessibleRole.PushButton;
        }

        public override Size GetPreferredSize(Size proposedSize)
        {
            Size t = TextRenderer.MeasureText(Text, Font);
            return new Size(t.Width + 28, Math.Max(30, t.Height + 12));
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { pressed = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.ParentBack(this));
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            Color back, fore;
            if (!Enabled) { back = primary ? Color.FromArgb(176, 208, 243) : Color.FromArgb(240, 240, 243); fore = primary ? Color.White : Color.FromArgb(175, 175, 180); }
            else if (primary) { back = pressed ? Theme.AccentPressed : hover ? Theme.AccentHover : Theme.Accent; fore = Color.White; }
            else { back = pressed ? Theme.FillPressed : hover ? Theme.FillHover : Theme.Fill; fore = Theme.Text; }
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using (var path = Theme.Rounded(r, Math.Min(Height / 2f - 1, 15)))
            using (var b = new SolidBrush(back))
                g.FillPath(b, path);
            if (Focused && ShowFocusCues)
            {
                using (var path = Theme.Rounded(new RectangleF(1.5f, 1.5f, Width - 3.5f, Height - 3.5f), Math.Min(Height / 2f - 2, 14)))
                using (var p = new Pen(Color.FromArgb(120, Theme.Accent), 2))
                    g.DrawPath(p, path);
            }
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, fore,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }
    }

    // 입력 컨트롤을 감싸는 둥근 테두리 칸 (평소 연회색, 입력 중 파란색, 비활성 회색 바탕)
    class FieldBox : Panel
    {
        readonly Control inner;
        bool focused;
        const int Height0 = 30, PadLeft = 7, PadRight = 3;
        static readonly Color Border = Color.FromArgb(199, 199, 204);
        static readonly Color DisabledFill = Color.FromArgb(242, 242, 245);

        public FieldBox(Control inner)
        {
            this.inner = inner;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Margin = new Padding(4, 3, 4, 3);
            var box = inner as TextBox;
            Size = new Size(inner.Width + PadLeft + PadRight, box != null && box.Multiline ? inner.Height + 8 : Height0);
            inner.Margin = Padding.Empty;
            Controls.Add(inner);
            inner.Enter += delegate { focused = true; Invalidate(); };
            inner.Leave += delegate { focused = false; Invalidate(); };
            inner.EnabledChanged += delegate { ApplyColors(); };
            inner.TextChanged += delegate { if (!inner.Visible) Invalidate(); };
            var combo = inner as ComboBox;
            if (combo != null) combo.SelectedIndexChanged += delegate { if (!inner.Visible) Invalidate(); };
            ApplyColors();
            // 칸 아무 곳이나 눌러도 입력 컨트롤로
            MouseDown += delegate { if (inner.Enabled) inner.Focus(); };
        }

        void ApplyColors()
        {
            Color fill = inner.Enabled ? Theme.Card : DisabledFill;
            BackColor = fill;
            inner.BackColor = fill;
            // 비활성 선택 상자는 자체 회색 틀이 생기므로 숨기고 값만 직접 그림
            if (inner is ComboBox) inner.Visible = inner.Enabled;
            Invalidate();
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            inner.SetBounds(PadLeft, Math.Max(0, (Height - inner.Height) / 2), Math.Max(10, Width - PadLeft - PadRight), inner.Height);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.ParentBack(this));
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using (var path = Theme.Rounded(r, 7))
            using (var fill = new SolidBrush(BackColor))
            using (var pen = new Pen(focused ? Theme.Accent : Border, focused ? 1.6f : 1f))
            {
                g.FillPath(fill, path);
                g.DrawPath(pen, path);
            }
            if (!inner.Visible)
            {
                var textRect = new Rectangle(PadLeft + 2, 0, Width - PadLeft - 22, Height);
                TextRenderer.DrawText(g, inner.Text, Font, textRect, Theme.Secondary, BackColor,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
                float cx = Width - 13, cy = Height / 2f;
                using (var p = new Pen(Color.FromArgb(175, 175, 180), 1.5f))
                    g.DrawLines(p, new[] { new PointF(cx - 3.5f, cy - 1.5f), new PointF(cx, cy + 2), new PointF(cx + 3.5f, cy - 1.5f) });
            }
        }
    }

    // 체크박스를 토글 스위치 모양으로 그림 (CheckBox 동작은 그대로)
    class Switch : CheckBox
    {
        const int TrackW = 34, TrackH = 20;

        public Switch()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
            AutoSize = true;
        }

        // 직접 그리는 컨트롤이라 화면 낭독기/자동화에 체크 상자임을 알려 줌
        protected override AccessibleObject CreateAccessibilityInstance()
        {
            return new SwitchAccessible(this);
        }

        class SwitchAccessible : ControlAccessibleObject
        {
            readonly Switch owner;
            public SwitchAccessible(Switch owner) : base(owner) { this.owner = owner; }
            public override AccessibleRole Role { get { return AccessibleRole.CheckButton; } }
            public override string DefaultAction { get { return owner.Checked ? "선택 취소" : "선택"; } }
            public override AccessibleStates State
            {
                get
                {
                    var s = AccessibleStates.Focusable;
                    if (owner.Checked) s |= AccessibleStates.Checked;
                    if (!owner.Enabled) s |= AccessibleStates.Unavailable;
                    if (owner.Focused) s |= AccessibleStates.Focused;
                    return s;
                }
            }
            public override void DoDefaultAction() { if (owner.Enabled) owner.Checked = !owner.Checked; }
        }

        public override Size GetPreferredSize(Size proposedSize)
        {
            Size t = TextRenderer.MeasureText(Text, Font);
            return new Size(TrackW + 8 + t.Width + 2, Math.Max(TrackH + 4, t.Height + 4));
        }

        protected override void OnCheckedChanged(EventArgs e) { Invalidate(); base.OnCheckedChanged(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Color back = Theme.ParentBack(this);
            g.Clear(back);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            float y = (Height - TrackH) / 2f;
            Color track = Checked ? Theme.SwitchOn : Color.FromArgb(220, 220, 225);
            if (!Enabled) track = Color.FromArgb(120, track);
            using (var path = Theme.Rounded(new RectangleF(1, y, TrackW, TrackH), TrackH / 2f))
            using (var b = new SolidBrush(track))
                g.FillPath(b, path);
            float knob = TrackH - 4;
            float x = Checked ? 1 + TrackW - knob - 2 : 3;
            using (var shadow = new SolidBrush(Color.FromArgb(40, 0, 0, 0)))
                g.FillEllipse(shadow, x, y + 2.6f, knob, knob);
            using (var k = new SolidBrush(Color.White))
                g.FillEllipse(k, x, y + 2, knob, knob);
            var textRect = new Rectangle(TrackW + 9, 0, Width - TrackW - 9, Height);
            TextRenderer.DrawText(g, Text, Font, textRect, Enabled ? Theme.Text : Theme.Secondary, back,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.SingleLine);
        }
    }
}
