using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;
namespace StreamSwitch {
    internal static class StudioArt {
        public static GraphicsPath Round(RectangleF r,float radius) {
            var p=new GraphicsPath();float d=radius*2;
            p.AddArc(r.X,r.Y,d,d,180,90);p.AddArc(r.Right-d,r.Y,d,d,270,90);p.AddArc(r.Right-d,r.Bottom-d,d,d,0,90);p.AddArc(r.X,r.Bottom-d,d,d,90,90);p.CloseFigure();return p;
        }
        public static Color Mix(Color a,Color b,float t){return Color.FromArgb((int)(a.R+(b.R-a.R)*t),(int)(a.G+(b.G-a.G)*t),(int)(a.B+(b.B-a.B)*t));}
        public static void Mark(Graphics g,Rectangle r) {
            g.SmoothingMode=SmoothingMode.AntiAlias;
            using(var p=Round(r,r.Width*.25f))using(var b=new LinearGradientBrush(r,Color.FromArgb(151,93,255),Color.FromArgb(58,201,228),45))g.FillPath(b,p);
            using(var pen=new Pen(Color.FromArgb(110,255,255,255),r.Width*.045f))g.DrawArc(pen,r.X+r.Width*.15f,r.Y+r.Height*.15f,r.Width*.7f,r.Height*.7f,215,270);
            using(var b=new SolidBrush(Color.White))g.FillPolygon(b,new[]{new PointF(r.X+r.Width*.41f,r.Y+r.Height*.29f),new PointF(r.X+r.Width*.72f,r.Y+r.Height*.5f),new PointF(r.X+r.Width*.41f,r.Y+r.Height*.71f)});
        }
        public static void SaveIcon(string path) {
            using(var stream=File.Create(path))using(var w=new BinaryWriter(stream)){
                int[] sizes={16,32,48,64,128,256};byte[][] pngs=new byte[sizes.Length][];
                for(int i=0;i<sizes.Length;i++)using(var bmp=new Bitmap(sizes[i],sizes[i])){
                    using(var g=Graphics.FromImage(bmp))Mark(g,new Rectangle(0,0,sizes[i]-1,sizes[i]-1));
                    using(var memory=new MemoryStream()){bmp.Save(memory,ImageFormat.Png);pngs[i]=memory.ToArray();}
                }
                w.Write((short)0);w.Write((short)1);w.Write((short)sizes.Length);int offset=6+16*sizes.Length;
                for(int i=0;i<sizes.Length;i++){w.Write((byte)(sizes[i]==256?0:sizes[i]));w.Write((byte)(sizes[i]==256?0:sizes[i]));w.Write((byte)0);w.Write((byte)0);w.Write((short)1);w.Write((short)32);w.Write(pngs[i].Length);w.Write(offset);offset+=pngs[i].Length;}
                foreach(var png in pngs)w.Write(png);
            }
        }
    }
    internal sealed class BrandMark:Control {
        public BrandMark(){DoubleBuffered=true;}
        protected override void OnPaint(PaintEventArgs e){StudioArt.Mark(e.Graphics,new Rectangle(0,0,Width-1,Height-1));}
    }
    internal sealed class StudioLogoPicker:Control {
        public string Selected="Twitch";
        public event EventHandler SelectionChanged;
        readonly string[] choices={"Twitch","Kick","Ninguno"};
        public StudioLogoPicker(){DoubleBuffered=true;Dock=DockStyle.Fill;TabStop=true;Cursor=Cursors.Hand;AccessibleName="Logo pequeño: Twitch, Kick o Ninguno";}
        void SelectIndex(int i){Selected=choices[Math.Max(0,Math.Min(2,i))];Invalidate();if(SelectionChanged!=null)SelectionChanged(this,EventArgs.Empty);}
        protected override void OnMouseDown(MouseEventArgs e){Focus();SelectIndex(e.X*3/Math.Max(1,Width));base.OnMouseDown(e);}
        protected override bool IsInputKey(Keys key){return key==Keys.Left||key==Keys.Right||base.IsInputKey(key);}
        protected override void OnKeyDown(KeyEventArgs e){int i=Array.IndexOf(choices,Selected);if(e.KeyCode==Keys.Left)SelectIndex(i-1);if(e.KeyCode==Keys.Right)SelectIndex(i+1);base.OnKeyDown(e);}
        protected override void OnPaint(PaintEventArgs e){var g=e.Graphics;g.Clear(Color.FromArgb(22,25,38));g.SmoothingMode=SmoothingMode.AntiAlias;
            for(int i=0;i<3;i++){var r=new Rectangle(i*Width/3+1,1,Width/3-4,Height-3);bool active=Selected==choices[i];
                using(var p=StudioArt.Round(r,7))using(var b=new SolidBrush(active?Color.FromArgb(82,58,139):Color.FromArgb(31,35,51)))g.FillPath(b,p);
                TextRenderer.DrawText(g,choices[i],Font,r,active?Color.White:Color.FromArgb(165,175,196),TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter);
            }
            if(Focused)ControlPaint.DrawFocusRectangle(g,Rectangle.Inflate(ClientRectangle,-2,-2));
        }
    }
    internal class StudioCard:Panel {
        public Color Surface=Color.FromArgb(22,25,38);
        public StudioCard(){DoubleBuffered=true;BackColor=Color.FromArgb(12,14,23);}
        protected override void OnPaint(PaintEventArgs e){base.OnPaint(e);e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
            using(var p=StudioArt.Round(new RectangleF(1,1,Width-3,Height-3),18)){
                using(var b=new SolidBrush(Surface))e.Graphics.FillPath(b,p);using(var pen=new Pen(Color.FromArgb(46,49,68)))e.Graphics.DrawPath(pen,p);
            }
        }
    }
    internal sealed class StudioField:Panel {
        readonly TextBox input;
        public StudioField(TextBox text){input=text;Dock=DockStyle.Fill;Margin=new Padding(0,0,0,7);Padding=new Padding(12,8,12,6);BackColor=Color.FromArgb(29,33,49);DoubleBuffered=true;
            input.BorderStyle=BorderStyle.None;input.BackColor=BackColor;input.Margin=Padding.Empty;input.Dock=DockStyle.Fill;Controls.Add(input);input.Enter+=delegate{Invalidate();};input.Leave+=delegate{Invalidate();};}
        protected override void OnPaint(PaintEventArgs e){base.OnPaint(e);e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
            using(var p=StudioArt.Round(new RectangleF(.5f,.5f,Width-2,Height-2),8))using(var pen=new Pen(input.Focused?Color.FromArgb(167,128,255):Color.FromArgb(50,56,77),input.Focused?2:1))e.Graphics.DrawPath(pen,p);}
    }
    internal sealed class StudioHero:Control {
        readonly Timer animation=new Timer{Interval=33};float phase;public bool Motion=true;public bool Live;
        public StudioHero(){DoubleBuffered=true;Dock=DockStyle.Fill;BackColor=Color.FromArgb(12,14,23);animation.Tick+=delegate{if(Motion&&Visible&&FindForm()!=null&&FindForm().WindowState!=FormWindowState.Minimized){phase+=.012f;Invalidate();}};animation.Start();}
        protected override void Dispose(bool disposing){if(disposing)animation.Dispose();base.Dispose(disposing);}
        protected override void OnPaint(PaintEventArgs e){
            var g=e.Graphics;g.SmoothingMode=SmoothingMode.AntiAlias;
            using(var path=StudioArt.Round(new RectangleF(0,0,Width-1,Height-1),20)){
                g.SetClip(path);using(var brush=new LinearGradientBrush(ClientRectangle,Color.FromArgb(61,38,107),Color.FromArgb(20,62,84),30))g.FillPath(brush,path);
                float cx=Width-88,cy=Height/2f;
                for(int i=0;i<3;i++){float size=90+i*37+(float)Math.Sin(phase+i)*8;using(var pen=new Pen(Color.FromArgb(42,200,170,255),1))g.DrawEllipse(pen,cx-size/2,cy-size/2,size,size);}
                for(int i=0;i<8;i++){double a=phase+i*Math.PI/4;using(var b=new SolidBrush(Color.FromArgb(90,183,215,255)))g.FillEllipse(b,cx+(float)Math.Cos(a)*78,cy+(float)Math.Sin(a)*65,3,3);}
                StudioArt.Mark(g,new Rectangle((int)cx-31,(int)cy-31,62,62));
                using(var f=new Font("Segoe UI",9,FontStyle.Bold))using(var b=new SolidBrush(Color.FromArgb(185,166,249)))g.DrawString("STREAMSWITCH / STUDIO",f,b,24,20);
                using(var f=new Font("Segoe UI",23,FontStyle.Bold))using(var b=new SolidBrush(Color.White))g.DrawString("Hazlo tuyo.",f,b,22,46);
                using(var f=new Font("Segoe UI",10))using(var b=new SolidBrush(Color.FromArgb(201,201,225)))g.DrawString("Tu imagen. Tu estilo. Tu presencia.",f,b,24,96);
                using(var b=new SolidBrush(Live?Color.FromArgb(101,231,188):Color.FromArgb(191,172,249)))g.FillEllipse(b,25,132,6,6);
                using(var f=new Font("Segoe UI",8,FontStyle.Bold))using(var b=new SolidBrush(Color.FromArgb(201,201,225)))g.DrawString(Live?"PRESENCIA ENVIADA":"LISTO PARA CREAR",f,b,38,128);g.ResetClip();
            }
        }
    }
}
