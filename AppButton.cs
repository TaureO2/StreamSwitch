using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace StreamSwitch {
    internal sealed class AppButton : Button {
        readonly Timer animation = new Timer { Interval = 16 };
        float hover; bool over, pressed;
        public static bool Motion = true;
        public AppButton() {
            DoubleBuffered = true;
            MouseEnter += delegate { over = true; animation.Start(); };
            MouseLeave += delegate { over = false; pressed = false; animation.Start(); };
            MouseDown += delegate { pressed = true; Invalidate(); };
            MouseUp += delegate { pressed = false; Invalidate(); };
            animation.Tick += delegate { float target = over ? 1 : 0; hover = Motion ? hover + (target-hover)*.22f : target; if(Math.Abs(hover-target)<.02f) { hover=target; animation.Stop(); } Invalidate(); };
        }
        protected override void Dispose(bool disposing) { if(disposing) animation.Dispose(); base.Dispose(disposing); }
        protected override void OnPaint(PaintEventArgs e) {
            var g=e.Graphics; g.Clear(Parent==null?Color.FromArgb(22,25,38):Parent.BackColor);g.SmoothingMode=SmoothingMode.AntiAlias;
            var area=new RectangleF(1,pressed?3:1,Width-3,Height-(pressed?5:3));
            bool primary=BackColor.R>90 && BackColor.B>150;
            Color a=Enabled?StudioArt.Mix(BackColor,Color.FromArgb(174,138,255),hover*.45f):Color.FromArgb(38,42,59);
            Color b=Enabled&&primary?StudioArt.Mix(Color.FromArgb(99,83,219),Color.FromArgb(134,116,246),hover):a;
            using(var path=StudioArt.Round(area,9)) {
                using(var brush=new LinearGradientBrush(area,a,b,15))g.FillPath(brush,path);
                using(var pen=new Pen(Enabled?Color.FromArgb((int)(50+hover*80),214,192,255):Color.FromArgb(55,60,80)))g.DrawPath(pen,path);
            }
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle,
                Enabled ? ForeColor : Color.FromArgb(161, 169, 190),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -4, -4));
        }
    }
}
