using System.Drawing.Drawing2D;
using System.Numerics;

namespace ZatCollectibles;

internal sealed class Overlay : Form
{
    internal Camera? Camera = null;
    internal Item[] Items = [];
    internal bool Gold = true, Bottles = true;
    internal bool English = false;
    private readonly Font labelFont = new("Segoe UI", 10, FontStyle.Bold);
    internal int Drawn { get; private set; }
    internal Overlay()
    {
        FormBorderStyle=FormBorderStyle.None;
        BackColor=Color.Magenta;
        TransparencyKey=Color.Magenta;
        ShowInTaskbar=false;
        TopMost=true;
        DoubleBuffered=true;
        StartPosition=FormStartPosition.Manual;
    }
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get { var cp=base.CreateParams; cp.ExStyle|=0x08000000|0x00080000|0x00000020|0x00000080; return cp; }
    }
    protected override void WndProc(ref Message m)
    {
        if(m.Msg==0x84) { m.Result=(nint)(-1); return; }
        if(m.Msg==0x21) { m.Result=(nint)3; return; }
        base.WndProc(ref m);
    }
    internal void Follow(nint window)
    {
        if(!Native.GetClientRect(window,out var r)) { Hide(); return; }
        var p=new Native.Point();
        if(!Native.ClientToScreen(window,ref p)) { Hide(); return; }
        int w=r.Right-r.Left,h=r.Bottom-r.Top;
        if(w<100||h<100) {Hide();return;}
        Native.SetWindowPos(Handle,(nint)(-1),p.X,p.Y,w,h,0x10|0x40);
        if(!Visible)Show();
    }
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // TransparencyKey windows can retain pixels from the previous frame when
        // an item disappears. Paint the entire key colour on every frame so a
        // removed marker is actually erased from the layered window.
        e.Graphics.Clear(TransparencyKey);
    }
    internal static PointF? Project(Vector3 p, Camera c, int width, int height)
    {
        var v=Vector3.Transform(p,c.View);
        if(!float.IsFinite(v.Z)||v.Z<.05f)return null;
        // Asura's camera Y axis points down in this build.
        float x=width*.5f*(1+v.X*c.ScaleX/v.Z), y=height*.5f*(1+v.Y*c.ScaleY/v.Z);
        return float.IsFinite(x)&&float.IsFinite(y)?new PointF(x,y):null;
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(TransparencyKey);
        Drawn=0;
        if(Camera is not {} camera)return;
        e.Graphics.SmoothingMode=SmoothingMode.None;
        foreach(var item in Items)
        {
            bool gold=item.Kind=="Золото";
            if(gold&&!Gold||!gold&&!Bottles)continue;
            var points=new List<PointF>();
            for(int i=0;i<8;i++)
            {
                var local=new Vector3(item.Bounds[i&1],item.Bounds[2+((i>>1)&1)],item.Bounds[4+((i>>2)&1)]);
                var projected=Project(Vector3.Transform(local,item.World),camera,Width,Height);
                if(projected is {} point)points.Add(point);
            }
            if(points.Count!=8)continue;
            var left=points.Min(p=>p.X);var right=points.Max(p=>p.X);
            var top=points.Min(p=>p.Y);var bottom=points.Max(p=>p.Y);
            var cx=(left+right)/2;var cy=(top+bottom)/2;
            var size=Math.Max(14,Math.Max(right-left,bottom-top)+6);
            if(size>Math.Max(Width,Height)*2||cx+size/2<0||cx-size/2>Width||cy+size/2<0||cy-size/2>Height)continue;
            var rect=new RectangleF(cx-size/2,cy-size/2,size,size);
            var color=gold?Color.FromArgb(255,209,70):Color.FromArgb(255,90,100);
            using var outline=new Pen(Color.Black,4);
            using var pen=new Pen(color,2);
            e.Graphics.DrawRectangle(outline,rect.X,rect.Y,rect.Width,rect.Height);
            e.Graphics.DrawRectangle(pen,rect.X,rect.Y,rect.Width,rect.Height);
            var name=English?(gold?"Gold bar":"Blood bottle"):item.Kind;
            var distance=Vector3.Distance(camera.Position,item.Position).ToString("0.0",English?System.Globalization.CultureInfo.InvariantCulture:System.Globalization.CultureInfo.GetCultureInfo("ru-RU"));
            var text=$"{name} · {distance} {(English?"m":"м")}";
            var textSize=e.Graphics.MeasureString(text,labelFont);
            var label=new RectangleF(Math.Clamp(cx-textSize.Width/2,0,Math.Max(0,Width-textSize.Width)),Math.Clamp(rect.Bottom+3,0,Math.Max(0,Height-textSize.Height)),textSize.Width,textSize.Height);
            e.Graphics.FillRectangle(Brushes.Black,label);
            using var brush=new SolidBrush(color);
            e.Graphics.DrawString(text,labelFont,brush,label.Location);
            Drawn++;
        }
    }
    protected override void Dispose(bool disposing) { if(disposing)labelFont.Dispose();base.Dispose(disposing); }
}
