using System.Windows;
using System.Windows.Media;
using SwiftGet.Models;

namespace SwiftGet.Views;

/// <summary>Shows which parts of the file each connection has fetched.</summary>
public sealed class SegmentBar : FrameworkElement
{
    public DownloadItem? Item { get; set; }

    Brush Res(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        var clip = new RectangleGeometry(new Rect(0, 0, w, h), h / 2, h / 2);
        dc.PushClip(clip);
        dc.DrawRectangle(Res("Track", Brushes.LightGray), null, new Rect(0, 0, w, h));

        var done = Res("Accent", Brushes.RoyalBlue);
        var running = Res("Success", Brushes.SeaGreen);
        if (Item != null)
        {
            long total = Item.TotalSize;
            if (Item.Status == DownloadStatus.Completed)
            {
                dc.DrawRectangle(Res("Success", Brushes.SeaGreen), null, new Rect(0, 0, w, h));
            }
            else if (total > 0)
            {
                foreach (var s in Item.SnapshotSegments())
                {
                    double x = s.Start * w / total;
                    double width = s.Downloaded * w / total;
                    dc.DrawRectangle(done, null, new Rect(x, 0, Math.Max(0, width), h));
                    if (s.Active)
                        dc.DrawRectangle(running, null, new Rect(Math.Min(w - 3, x + width), 0, 3, h));
                }
            }
        }
        dc.Pop();
    }
}
