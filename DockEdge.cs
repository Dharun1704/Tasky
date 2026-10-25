using System.Windows;

namespace Tasky;

/// <summary>The screen edge the dock sits against.</summary>
internal enum DockEdge { Bottom, Top, Left, Right }

/// <summary>
/// Layout rules for each edge. "Along" is the direction the icons run in; "across" is the dock's
/// thickness, pointing away from the screen edge (the way icons grow when they magnify).
/// </summary>
internal static class DockEdges
{
    public static DockEdge Parse(string? s) => Enum.TryParse<DockEdge>(s, true, out var e) ? e : DockEdge.Bottom;

    public static bool IsVertical(this DockEdge e) => e is DockEdge.Left or DockEdge.Right;

    /// <summary>The SHAppBarMessage ABE_* value.</summary>
    public static uint AppBarEdge(this DockEdge e) => e switch
    {
        DockEdge.Left => 0, DockEdge.Top => 1, DockEdge.Right => 2, _ => 3,
    };

    /// <summary>Alignment that holds an element against the screen edge (centred along the dock).</summary>
    public static HorizontalAlignment EdgeH(this DockEdge e) => e switch
    {
        DockEdge.Left => HorizontalAlignment.Left, DockEdge.Right => HorizontalAlignment.Right, _ => HorizontalAlignment.Center,
    };

    public static VerticalAlignment EdgeV(this DockEdge e) => e switch
    {
        DockEdge.Top => VerticalAlignment.Top, DockEdge.Bottom => VerticalAlignment.Bottom, _ => VerticalAlignment.Center,
    };

    /// <summary>
    /// A margin given as it would be for a bottom dock: <paramref name="alongStart"/>/<paramref name="alongEnd"/>
    /// before and after along the dock, <paramref name="edge"/> on the screen-edge side and
    /// <paramref name="inner"/> on the side facing the screen.
    /// </summary>
    public static Thickness Margin(this DockEdge e, double alongStart, double alongEnd, double edge, double inner = 0) => e switch
    {
        DockEdge.Top => new Thickness(alongStart, edge, alongEnd, inner),
        DockEdge.Left => new Thickness(edge, alongStart, inner, alongEnd),
        DockEdge.Right => new Thickness(inner, alongStart, edge, alongEnd),
        _ => new Thickness(alongStart, inner, alongEnd, edge),
    };

    /// <summary>The coordinate along the dock.</summary>
    public static double Along(this DockEdge e, Point p) => e.IsVertical() ? p.Y : p.X;

    public static double AlongLength(this DockEdge e, FrameworkElement el) => e.IsVertical() ? el.ActualHeight : el.ActualWidth;

    /// <summary>+1 or -1: the direction (on the across axis) that points away from the screen edge.</summary>
    public static int Inward(this DockEdge e) => e is DockEdge.Bottom or DockEdge.Right ? -1 : 1;

    /// <summary>Sets an element's alignment so it hugs the screen edge inside the dock's row.</summary>
    public static void HugEdge(this DockEdge e, FrameworkElement el)
    {
        if (e.IsVertical())
        {
            el.HorizontalAlignment = e.EdgeH();
            el.VerticalAlignment = VerticalAlignment.Stretch;
        }
        else
        {
            el.VerticalAlignment = e.EdgeV();
            el.HorizontalAlignment = HorizontalAlignment.Stretch;
        }
    }
}
