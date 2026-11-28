// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris

using Avalonia;

namespace SimpleScanner.Views;

/// <summary>Where a window goes so that all of it is on a screen.</summary>
public static class WindowPlacement
{
    /// <summary>
    /// The top left corner for a window wanted at <paramref name="at"/>: there, if the window fits on the screen it is on;
    /// otherwise moved as little as will bring all of it onto that screen. A window whose title bar is on no screen
    /// (the screen it was on has gone) is brought onto <paramref name="fallback"/>, the screen the window is on now.
    /// </summary>
    /// <param name="at">The top left corner wanted, in screen pixels.</param>
    /// <param name="size">The window's size with its title bar and edges, in layout units.</param>
    /// <param name="screens">Each screen's working area (without the menu bar and Dock or taskbar), in pixels, and its scaling.</param>
    /// <param name="fallback">The index of the screen to use when the window is on none of them.</param>
    public static PixelPoint Fit(PixelPoint at, (double w, double h) size, IReadOnlyList<(PixelRect Area, double Scaling)> screens, int fallback)
    {
        if (screens.Count == 0) return at;
        // The screen the window is on is the one with the most of its title bar (the top 30 units) on it.
        int best = -1;
        long most = 0;
        for (int i = 0; i < screens.Count; i++)
        {
            var (area, scaling) = screens[i];
            var top = new PixelRect(at.X, at.Y, Math.Max(1, (int)(size.w * scaling)), Math.Max(1, (int)(30 * scaling)));
            var on = area.Intersect(top);
            long amount = (long)on.Width * on.Height;
            if (amount > most)
            {
                most = amount;
                best = i;
            }
        }
        if (best < 0) best = fallback >= 0 && fallback < screens.Count ? fallback : 0;
        var (a, s) = screens[best];
        int w = (int)Math.Ceiling(size.w * s), h = (int)Math.Ceiling(size.h * s);
        // Too big for the screen: its top left corner at the screen's, where the title bar can be reached.
        int x = Math.Max(a.X, Math.Min(at.X, a.Right - w));
        int y = Math.Max(a.Y, Math.Min(at.Y, a.Bottom - h));
        return new PixelPoint(x, y);
    }
}
