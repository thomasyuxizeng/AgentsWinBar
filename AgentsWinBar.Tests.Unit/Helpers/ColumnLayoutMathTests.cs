using AwesomeAssertions;
using AgentsWinBar.Helpers;

namespace AgentsWinBar.Tests.Unit.Helpers;

[TestClass]
public class ColumnLayoutMathTests
{
    [TestMethod]
    [DataRow(1300d, 3)]   // (1300 + 16) / 396 = 3.3
    [DataRow(792d, 2)]    // exactly two: 380 + 16 + 380 + 16 = 792 fits 2
    [DataRow(775d, 1)]    // one DIP short of two columns (776)
    [DataRow(379d, 1)]    // narrower than one item still gets one column
    public void Columns_WhenWidthGiven_FitsColumnsOfAtLeastTheMinimumWidth(double width, int expected) =>
        ColumnLayoutMath.Columns(width, 380, 16, 10).Should().Be(expected);

    [TestMethod]
    public void Columns_WhenMoreRoomThanItems_UsesOneColumnPerItem() =>
        ColumnLayoutMath.Columns(5000, 380, 16, 3).Should().Be(3);

    [TestMethod]
    [DataRow(0d)]
    [DataRow(-50d)]
    [DataRow(double.NaN)]
    [DataRow(double.PositiveInfinity)]
    [DataRow(double.NegativeInfinity)]
    public void Columns_WhenWidthUnusable_ReturnsOne(double width) =>
        ColumnLayoutMath.Columns(width, 380, 16, 10).Should().Be(1);

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(-3)]
    public void Columns_WhenAtMostOneItem_ReturnsOne(int items) =>
        ColumnLayoutMath.Columns(1300, 380, 16, items).Should().Be(1);

    [TestMethod]
    [DataRow(0d, 0d)]
    [DataRow(double.NaN, double.NaN)]
    [DataRow(-10d, -10d)]
    [DataRow(double.PositiveInfinity, 0d)]
    public void Columns_WhenMinimumOrSpacingUnusable_StaysWithinOneToItemCount(double minItemWidth, double spacing) =>
        ColumnLayoutMath.Columns(1300, minItemWidth, spacing, 7).Should().BeInRange(1, 7);

    [TestMethod]
    public void ColumnWidth_WhenColumnsShareWidth_SubtractsTheGaps() =>
        ColumnLayoutMath.ColumnWidth(1300, 3, 16).Should().BeApproximately((1300 - 32) / 3.0, 1e-9);

    [TestMethod]
    [DataRow(0d, 2)]
    [DataRow(-1d, 2)]
    [DataRow(double.NaN, 2)]
    [DataRow(double.PositiveInfinity, 2)]
    [DataRow(10d, 5)]      // gaps wider than the width
    [DataRow(300d, 0)]     // no columns is treated as one
    [DataRow(300d, -4)]
    public void ColumnWidth_WhenInputsUnusable_IsFiniteAndNotNegative(double width, int columns)
    {
        var result = ColumnLayoutMath.ColumnWidth(width, columns, 16);
        double.IsFinite(result).Should().BeTrue();
        result.Should().BeGreaterThanOrEqualTo(0);
    }
}
