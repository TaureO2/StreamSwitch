using System.Drawing;
using System.Windows.Forms;

namespace StreamSwitch {
    internal sealed class AppButton : Button {
        protected override void OnPaint(PaintEventArgs e) {
            e.Graphics.Clear(Enabled ? BackColor : Color.FromArgb(43, 47, 64));
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle,
                Enabled ? ForeColor : Color.FromArgb(161, 169, 190),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -4, -4));
        }
    }
}
