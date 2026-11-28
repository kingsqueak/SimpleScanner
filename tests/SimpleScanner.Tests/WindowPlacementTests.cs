// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris

using Avalonia;
using SimpleScanner.Views;

namespace SimpleScanner.Tests;

public class WindowPlacementTests
{
    // A 5K screen at 2× (2560 × 1440 points): menu bar 50 px, Dock 150 px.
    private static readonly (PixelRect, double) Main = (new PixelRect(0, 50, 5120, 2680), 2);
    // A second screen to its right at 1×.
    private static readonly (PixelRect, double) Right = (new PixelRect(5120, 0, 1920, 1080), 1);

    [Fact]
    public void A_window_wholly_on_the_screen_stays_where_it_was()
    {
        var at = new PixelPoint(1945, 57);
        Assert.Equal(at, WindowPlacement.Fit(at, (583, 287), [Main], 0));
    }

    [Fact]
    public void A_window_partly_below_the_screen_is_moved_up_just_enough()
    {
        // The full window (850 × 829 with its title bar) where the small one's corner was, low on the screen.
        var place = WindowPlacement.Fit(new PixelPoint(1945, 2000), (850, 829), [Main], 0);
        Assert.Equal(new PixelPoint(1945, 50 + 2680 - 1658), place);
    }

    [Fact]
    public void A_window_above_the_screen_comes_down_below_the_menu_bar()
    {
        Assert.Equal(new PixelPoint(0, 50), WindowPlacement.Fit(new PixelPoint(0, 0), (850, 829), [Main], 0));
        Assert.Equal(new PixelPoint(100, 50), WindowPlacement.Fit(new PixelPoint(100, -900), (850, 829), [Main], 0));
    }

    [Fact]
    public void A_window_past_the_right_edge_is_brought_back()
    {
        Assert.Equal(new PixelPoint(5120 - 1700, 400), WindowPlacement.Fit(new PixelPoint(4800, 400), (850, 829), [Main], 0));
    }

    [Fact]
    public void A_window_on_the_second_screen_stays_there_at_that_screens_scaling()
    {
        var at = new PixelPoint(5500, 100);
        Assert.Equal(at, WindowPlacement.Fit(at, (520, 290), [Main, Right], 0));
        // Too low on it: moved up on that screen, in its own pixels.
        Assert.Equal(new PixelPoint(5500, 1080 - 829), WindowPlacement.Fit(new PixelPoint(5500, 900), (850, 829), [Main, Right], 0));
    }

    [Fact]
    public void A_window_on_a_screen_that_has_gone_is_brought_onto_the_one_it_is_on_now()
    {
        // Saved on the right-hand screen, which is no longer there.
        var place = WindowPlacement.Fit(new PixelPoint(5500, 100), (520, 290), [Main], 0);
        Assert.Equal(new PixelPoint(5120 - 1040, 100), place);
    }

    [Fact]
    public void A_window_bigger_than_the_screen_has_its_title_bar_at_the_top_left()
    {
        Assert.Equal(new PixelPoint(5120, 0), WindowPlacement.Fit(new PixelPoint(5300, 300), (2500, 1500), [Main, Right], 0));
    }
}
