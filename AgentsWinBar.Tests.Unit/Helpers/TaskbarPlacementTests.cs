using AgentsWinBar.Helpers;
using AwesomeAssertions;

namespace AgentsWinBar.Tests.Unit.Helpers;

/// <summary>
/// Where the taskbar strip goes. Rectangles are physical pixels as measured on a 2256x1504 screen at 150% (taskbar
/// 1432 to 1504, tray from 1764, centered Start 698 to 765), plus a 1920x1080 Windows 10 screen at 100%.
/// </summary>
[TestClass]
public class TaskbarPlacementTests
{
    private const double Scale150 = 1.5;
    private const int Windows11Build = 26300;
    private const int Windows10Build = 19045;

    private static readonly ScreenRect Taskbar = new(0, 1432, 2256, 1504);
    private static readonly ScreenRect Tray = new(1764, 1432, 2256, 1504);
    private static readonly ScreenRect CenteredStart = new(698, 1432, 765, 1504);
    private static readonly ScreenRect LeftStart = new(12, 1432, 79, 1504);

    // Three providers: 3 x 116 + 2 x 24 + 36 padding + 2 slack = 434 DIP, 651 px at 150%
    private const int StripWidth = 651;
    private const int RightEndX = 1764 - StripWidth - 6;

    [TestMethod]
    public void Decide_LeftAlignedWindows11_GoesRightNextToTray()
    {
        var placement = TaskbarPlacement.Decide(Taskbar, LeftStart, Tray, 0, Windows11Build, StripWidth, Scale150, "auto");

        placement.Should().Be(new TaskbarStripPlacement(TaskbarStripSide.Right, RightEndX));
    }

    [TestMethod]
    public void Decide_CenteredWindows11_GoesLeft()
    {
        var placement = TaskbarPlacement.Decide(Taskbar, CenteredStart, Tray, 1, Windows11Build, StripWidth, Scale150, "auto");

        placement.Should().Be(new TaskbarStripPlacement(TaskbarStripSide.Left, Taskbar.Left));
    }

    [TestMethod]
    public void Decide_Windows10WithoutStartRect_GoesRight()
    {
        var taskbar = new ScreenRect(0, 1040, 1920, 1080);
        var tray = new ScreenRect(1700, 1040, 1920, 1080);

        var placement = TaskbarPlacement.Decide(taskbar, null, tray, null, Windows10Build, 434, 1.0, "auto");

        placement.Should().Be(new TaskbarStripPlacement(TaskbarStripSide.Right, 1700 - 434 - 4));
    }

    [TestMethod]
    public void Decide_Windows11WithoutStartRectAndLeftAlignment_GoesRight()
    {
        var placement = TaskbarPlacement.Decide(Taskbar, null, Tray, 0, Windows11Build, StripWidth, Scale150, "auto");

        placement.Side.Should().Be(TaskbarStripSide.Right);
        placement.X.Should().Be(RightEndX);
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(null)]
    public void Decide_Windows11WithoutStartRectAndCenteredOrUnsetAlignment_GoesLeft(int? taskbarAlignment)
    {
        var placement = TaskbarPlacement.Decide(Taskbar, null, Tray, taskbarAlignment, Windows11Build, StripWidth, Scale150, "auto");

        placement.Should().Be(new TaskbarStripPlacement(TaskbarStripSide.Left, Taskbar.Left));
    }

    [TestMethod]
    public void Decide_StartRectAndAlignmentDisagree_StartRectDecides()
    {
        // The registry says left, but Start is drawn in the middle: the rectangle wins
        var placement = TaskbarPlacement.Decide(Taskbar, CenteredStart, Tray, 0, Windows11Build, StripWidth, Scale150, "auto");

        placement.Side.Should().Be(TaskbarStripSide.Left);
    }

    [TestMethod]
    public void Decide_ManualLeft_GoesLeftEvenWhenStartIsAtLeftEnd()
    {
        var placement = TaskbarPlacement.Decide(Taskbar, LeftStart, Tray, 0, Windows11Build, StripWidth, Scale150, "left");

        placement.Should().Be(new TaskbarStripPlacement(TaskbarStripSide.Left, Taskbar.Left));
    }

    [TestMethod]
    public void Decide_ManualRight_GoesRightOnCenteredTaskbar()
    {
        var placement = TaskbarPlacement.Decide(Taskbar, CenteredStart, Tray, 1, Windows11Build, StripWidth, Scale150, "right");

        placement.Should().Be(new TaskbarStripPlacement(TaskbarStripSide.Right, RightEndX));
    }

    [TestMethod]
    [DataRow("auto")]
    [DataRow("right")]
    public void Decide_MissingTray_FallsBackToLeft(string setting)
    {
        var placement = TaskbarPlacement.Decide(Taskbar, LeftStart, null, 0, Windows11Build, StripWidth, Scale150, setting);

        placement.Should().Be(new TaskbarStripPlacement(TaskbarStripSide.Left, Taskbar.Left));
    }

    [TestMethod]
    public void Decide_EmptyTrayRect_FallsBackToLeft()
    {
        var empty = new ScreenRect(0, 0, 0, 0);

        var placement = TaskbarPlacement.Decide(Taskbar, LeftStart, empty, 0, Windows11Build, StripWidth, Scale150, "right");

        placement.Side.Should().Be(TaskbarStripSide.Left);
    }

    [TestMethod]
    [DataRow(1700)]   // would end up at 58, over Start (12 to 79)
    [DataRow(1800)]   // would start at -42, left of the taskbar
    public void Decide_StripTooWideForGapLeftAligned_FallsBackToLeft(int stripWidth)
    {
        var placement = TaskbarPlacement.Decide(Taskbar, LeftStart, Tray, 0, Windows11Build, stripWidth, Scale150, "auto");

        placement.Should().Be(new TaskbarStripPlacement(TaskbarStripSide.Left, Taskbar.Left));
    }

    [TestMethod]
    public void Decide_ManualRightOverlappingCenteredStart_FallsBackToLeft()
    {
        // 1764 - 1000 - 6 = 758, which is inside the Start button (698 to 765)
        var placement = TaskbarPlacement.Decide(Taskbar, CenteredStart, Tray, 1, Windows11Build, 1000, Scale150, "right");

        placement.Side.Should().Be(TaskbarStripSide.Left);
    }

    [TestMethod]
    public void Decide_StripEndingRightAtStartsEdge_GoesRight()
    {
        // 1764 - 993 - 6 = 765: touches the Start button's right edge without covering it
        var placement = TaskbarPlacement.Decide(Taskbar, CenteredStart, Tray, 1, Windows11Build, 993, Scale150, "right");

        placement.Should().Be(new TaskbarStripPlacement(TaskbarStripSide.Right, 765));
    }

    [TestMethod]
    public void Decide_At150PercentScale_LeavesSixPixelGapBeforeTray()
    {
        var placement = TaskbarPlacement.Decide(Taskbar, LeftStart, Tray, 0, Windows11Build, StripWidth, Scale150, "right");

        (Tray.Left - (placement.X + StripWidth)).Should().Be(6);
    }

    [TestMethod]
    [DataRow(1.0, 4)]
    [DataRow(1.25, 5)]
    [DataRow(1.5, 6)]
    [DataRow(2.0, 8)]
    [DataRow(0.0, 4)]
    [DataRow(double.NaN, 4)]
    public void TrayGap_Scale_ReturnsFourDipInPixels(double scale, int expected)
    {
        TaskbarPlacement.TrayGap(scale).Should().Be(expected);
    }

    [TestMethod]
    [DataRow("Right")]
    [DataRow("RIGHT")]
    [DataRow("middle")]
    [DataRow("")]
    [DataRow(null)]
    public void Decide_UnknownSettingOnCenteredTaskbar_ActsAsAuto(string? setting)
    {
        var placement = TaskbarPlacement.Decide(Taskbar, CenteredStart, Tray, 1, Windows11Build, StripWidth, Scale150, setting);

        placement.Side.Should().Be(TaskbarStripSide.Left);
    }

    [TestMethod]
    [DataRow(130, true)]    // 130 px from the left end, within two Start widths (134)
    [DataRow(134, true)]
    [DataRow(135, false)]
    [DataRow(698, false)]
    public void IsStartAtLeftEnd_StartRect_ComparesWithTwoStartWidths(int startLeft, bool expected)
    {
        var start = new ScreenRect(startLeft, 1432, startLeft + 67, 1504);

        TaskbarPlacement.IsStartAtLeftEnd(Taskbar, start, null, Windows11Build).Should().Be(expected);
    }

    [TestMethod]
    public void IsStartAtLeftEnd_TaskbarNotAtScreenOrigin_MeasuresFromTaskbarLeftEdge()
    {
        var taskbar = new ScreenRect(-1920, 1040, 0, 1080);
        var start = new ScreenRect(-1900, 1040, -1852, 1080);

        TaskbarPlacement.IsStartAtLeftEnd(taskbar, start, 1, Windows11Build).Should().BeTrue();
    }
}
