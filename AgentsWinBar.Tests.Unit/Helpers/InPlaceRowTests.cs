using System.Collections.ObjectModel;
using System.Collections.Specialized;
using AwesomeAssertions;
using AgentsWinBar.Helpers;

namespace AgentsWinBar.Tests.Unit.Helpers;

[TestClass]
public class InPlaceRowTests
{
    private sealed class Row : InPlaceRow
    {
        public string Name { get; set; } = string.Empty;
        [UpdateLast] public DateTime? Stamp { get; set; }
        public double Percent { get; set; }
        [KeepOnUpdate] public object? Icon { get; set; }
        public string Computed => Name + "|first";
    }

    private static List<string> Raised(Row row)
    {
        var names = new List<string>();
        row.PropertyChanged += (_, e) => names.Add(e.PropertyName!);
        return names;
    }

    [TestMethod]
    public void UpdateFrom_WhenValuesChange_CopiesThemAndRaisesStampLast()
    {
        var row = new Row { Name = "Claude", Percent = 10 };
        var raised = Raised(row);
        var stamp = new DateTime(2026, 11, 25, 9, 0, 0, DateTimeKind.Utc);

        var changed = row.UpdateFrom(new Row { Name = "Claude", Percent = 20, Stamp = stamp });

        changed.Should().Be(2);
        row.Percent.Should().Be(20);
        row.Stamp.Should().Be(stamp);
        raised.Should().Equal(nameof(Row.Percent), nameof(Row.Stamp));
    }

    [TestMethod]
    public void UpdateFrom_WhenNothingChanged_RaisesNothing()
    {
        var row = new Row { Name = "Codex", Percent = 5 };
        var raised = Raised(row);

        row.UpdateFrom(new Row { Name = "Codex", Percent = 5 }).Should().Be(0);

        raised.Should().BeEmpty();
    }

    [TestMethod]
    public void UpdateFrom_WhenPropertyKeptOnUpdate_LeavesItAlone()
    {
        var icon = new object();
        var row = new Row { Name = "Gemini", Icon = icon };

        row.UpdateFrom(new Row { Name = "Gemini", Icon = new object() });

        row.Icon.Should().BeSameAs(icon);
    }

    [TestMethod]
    public void Apply_WhenSameKeysInSameOrder_UpdatesExistingRowsWithoutCollectionChange()
    {
        var first = new Row { Name = "Claude", Percent = 1 };
        var second = new Row { Name = "Codex", Percent = 2 };
        var target = new ObservableCollection<Row> { first, second };
        var events = new List<NotifyCollectionChangedAction>();
        target.CollectionChanged += (_, e) => events.Add(e.Action);

        var inPlace = InPlaceRow.Apply(target, [new Row { Name = "Claude", Percent = 3 }, new Row { Name = "Codex", Percent = 4 }], r => r.Name);

        inPlace.Should().BeTrue();
        events.Should().BeEmpty();
        target.Should().Equal(first, second);
        target.Select(r => r.Percent).Should().Equal(3, 4);
    }

    [TestMethod]
    [DataRow("Codex,Claude")]
    [DataRow("Claude")]
    [DataRow("Claude,Codex,Gemini")]
    public void Apply_WhenRowsDiffer_ReplacesContents(string names)
    {
        var target = new ObservableCollection<Row> { new() { Name = "Claude" }, new() { Name = "Codex" } };
        var next = names.Split(',').Select(n => new Row { Name = n }).ToList();

        var inPlace = InPlaceRow.Apply(target, next, r => r.Name);

        inPlace.Should().BeFalse();
        target.Should().Equal(next);
    }

    [TestMethod]
    public void Apply_WhenTargetEmpty_AddsRows()
    {
        var target = new ObservableCollection<Row>();
        var next = new List<Row> { new() { Name = "Claude" } };

        InPlaceRow.Apply(target, next, r => r.Name).Should().BeFalse();

        target.Should().Equal(next);
    }

    [TestMethod]
    public void UpdateFrom_WhenDifferentType_Throws()
    {
        var act = () => new Row().UpdateFrom(new OtherRow());

        act.Should().Throw<ArgumentException>();
    }

    private sealed class OtherRow : InPlaceRow;

    [TestMethod]
    public void UpdateFrom_WhenNanValueUnchanged_DoesNotRaisePropertyChanged()
    {
        var row = new Row { Name = "Claude", Percent = double.NaN };
        var raised = Raised(row);

        var changed = row.UpdateFrom(new Row { Name = "Claude", Percent = double.NaN });

        changed.Should().Be(0);
        raised.Should().BeEmpty();
    }

    [TestMethod]
    public void UpdateFrom_WhenSubclassOverridesUpdateFromCore_UsesOverrideAndRaisesCachedArgs()
    {
        var row = new TypedOverrideRow { Name = "Claude", Value = 1 };
        var raised = new List<System.ComponentModel.PropertyChangedEventArgs>();
        row.PropertyChanged += (_, e) => raised.Add(e);

        var changed = row.UpdateFrom(new TypedOverrideRow { Name = "Claude", Value = 2 });
        row.UpdateFrom(new TypedOverrideRow { Name = "Claude", Value = 3 });

        changed.Should().Be(1);
        row.Value.Should().Be(3);
        raised.Should().HaveCount(2);
        ReferenceEquals(raised[0], raised[1]).Should().BeTrue();
    }

    private sealed class TypedOverrideRow : InPlaceRow
    {
        public string Name { get; set; } = string.Empty;
        public int Value { get; set; }

        protected override int UpdateFromCore(InPlaceRow next)
        {
            var other = (TypedOverrideRow)next;
            var changed = 0;
            if (!string.Equals(Name, other.Name, StringComparison.Ordinal)) { Name = other.Name; changed++; RaisePropertyChanged(nameof(Name)); }
            if (Value != other.Value) { Value = other.Value; changed++; RaisePropertyChanged(nameof(Value)); }
            return changed;
        }
    }
}
