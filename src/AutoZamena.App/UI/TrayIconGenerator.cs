using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace AutoZamena.App.UI;

public enum TrayVisualState
{
    Active = 0,
    StandbyFilteredApp = 1,
    Paused = 2
}

internal static class TrayIconGenerator
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public static Icon CreateIcon(TrayVisualState state, string label = "А", bool isEnglishAccent = false, int size = 32)
    {
        Color bgPrimary = state switch
        {
            TrayVisualState.Active => isEnglishAccent
                ? Color.FromArgb(30, 116, 216)                                // Crisp royal blue for EN
                : Color.FromArgb(16, 158, 96),                                // Emerald green for RU / А
            TrayVisualState.StandbyFilteredApp => Color.FromArgb(84, 102, 122),// Muted slate (filtered app / IDE)
            _ => Color.FromArgb(180, 112, 32)                                 // Warm amber (Paused)
        };

        using var bmp = new Bitmap(size, size);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            int pad = 1;
            int rectSize = size - (pad * 2);
            using GraphicsPath path = CreateRoundedRect(new Rectangle(pad, pad, rectSize, rectSize), size / 5);
            using var brush = new SolidBrush(bgPrimary);
            g.FillPath(brush, path);

            float fontPx = label.Length >= 2 ? size * 0.40f : size * 0.50f;
            using var font = new Font("Segoe UI", fontPx, FontStyle.Bold, GraphicsUnit.Pixel);
            using var textBrush = new SolidBrush(Color.White);
            var sf = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            };
            g.DrawString(label, font, textBrush, new RectangleF(0, 0, size, size), sf);
        }

        IntPtr hIcon = bmp.GetHicon();
        try
        {
            using Icon temp = Icon.FromHandle(hIcon);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    private static GraphicsPath CreateRoundedRect(Rectangle bounds, int radius)
    {
        int d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
