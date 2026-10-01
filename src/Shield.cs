using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace WindscribeWatchdog
{
    // The tray icon: a shield in the status colour.
    public static class Shield
    {
        public static Icon MakeIcon(Color fill, int size)
        {
            using (var bmp = new Bitmap(size, size))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                using (GraphicsPath path = Outline(size))
                using (var brush = new SolidBrush(fill))
                using (var pen = new Pen(Color.FromArgb(150, 0, 0, 0), Math.Max(1f, size / 16f)))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.FillPath(brush, path);
                    g.DrawPath(pen, path);
                }
                return Icon.FromHandle(bmp.GetHicon());
            }
        }

        public static GraphicsPath Outline(float s)
        {
            float left = s * 0.14f, right = s * 0.86f, mid = s / 2f;
            float top = s * 0.06f, shoulder = s * 0.2f, bottom = s * 0.95f;
            var path = new GraphicsPath();
            path.AddLine(left, shoulder, mid, top);
            path.AddLine(mid, top, right, shoulder);
            path.AddBezier(right, shoulder, right, s * 0.62f, s * 0.68f, s * 0.82f, mid, bottom);
            path.AddBezier(mid, bottom, s * 0.32f, s * 0.82f, left, s * 0.62f, left, shoulder);
            path.CloseFigure();
            return path;
        }
    }
}
