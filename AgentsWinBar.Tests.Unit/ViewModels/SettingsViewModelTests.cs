using AwesomeAssertions;
using AgentsWinBar.Models;
using AgentsWinBar.Services;
using AgentsWinBar.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AgentsWinBar.Tests.Unit.ViewModels;

[TestClass]
public class SettingsViewModelTests
{
    private readonly AppSettings _settings = AppSettings.GetDefaults();
    private readonly Mock<ISettingsService> _store = new();
    private SettingsViewModel Create()
    {
        _store.SetupGet(s => s.Settings).Returns(_settings);
        _store.Setup(s => s.SaveAsync()).Returns(Task.CompletedTask);
        var vm = new SettingsViewModel(_store.Object, NullLogger<SettingsViewModel>.Instance);
        vm.Load(); return vm;
    }

    [TestMethod]
    public async Task SaveAsync_ChangedBillingFields_PersistsAndRequestsRefresh()
    {
        _settings.ApiCreditsMonthlyUsd = 150; _settings.ApiCreditsCycleStartDay = 10;
        var vm = Create(); vm.ApiCreditsMonthlyUsd.Should().Be(150); vm.ApiCreditsCycleStartDay.Should().Be(10);
        vm.ApiCreditsMonthlyUsd = 300.25m; vm.ApiCreditsCycleStartDay = 20;
        _settings.ApiCreditsMonthlyUsd.Should().Be(150); // Edits stay local until Save.
        var saved = false; vm.SaveRequested += (_, _) => saved = true;
        await vm.SaveCommand.ExecuteAsync(null);
        _settings.ApiCreditsMonthlyUsd.Should().Be(300.25m); _settings.ApiCreditsCycleStartDay.Should().Be(20);
        _store.Verify(s => s.SaveAsync(), Times.Once); saved.Should().BeTrue();
    }

    [TestMethod]
    public async Task SaveAsync_FailedPersistence_RollsBackBillingAndExistingSettings()
    {
        var vm = Create();
        vm.ApiCreditsMonthlyUsd = 500; vm.ApiCreditsCycleStartDay = 20;
        vm.ShowCostTotals = false; vm.SelectedResetAnimationMode = ResetAnimationMode.Never;
        _store.Setup(s => s.SaveAsync()).ThrowsAsync(new IOException("disk unavailable"));
        var saved = false; vm.SaveRequested += (_, _) => saved = true;
        Func<Task> save = () => vm.SaveCommand.ExecuteAsync(null);
        await save.Should().ThrowAsync<IOException>();
        _settings.ApiCreditsMonthlyUsd.Should().Be(200); _settings.ApiCreditsCycleStartDay.Should().Be(1);
        _settings.ShowCostTotals.Should().BeTrue();
        _settings.ResetAnimationMode.Should().Be(ResetAnimationMode.WhenNotBatterySaver); saved.Should().BeFalse();
    }

    [TestMethod]
    public void Cancel_UnsavedBillingEdits_KeepsStoredValuesAndLoadDiscardsEdits()
    {
        var vm = Create(); vm.ApiCreditsMonthlyUsd = 10; vm.ApiCreditsCycleStartDay = 15;
        vm.CancelCommand.Execute(null);
        _settings.ApiCreditsMonthlyUsd.Should().Be(200); _settings.ApiCreditsCycleStartDay.Should().Be(1);
        vm.Load(); vm.ApiCreditsMonthlyUsd.Should().Be(200); vm.ApiCreditsCycleStartDay.Should().Be(1);
        _store.Verify(s => s.SaveAsync(), Times.Never);
    }

    [TestMethod]
    public async Task SaveAsync_GraphsSwitchedOff_PersistsPerProviderAndLoadsBack()
    {
        var vm = Create();
        vm.Providers.Should().OnlyContain(p => p.ShowGraphs);
        vm.Providers.Single(p => p.Id == "codex").ShowGraphs = false;
        _settings.Providers.Should().OnlyContain(p => p.ShowGraphs); // Edits stay local until Save.
        await vm.SaveCommand.ExecuteAsync(null);
        _settings.Providers.Where(p => !p.ShowGraphs).Select(p => p.Id).Should().Equal("codex");
        vm.Load();
        vm.Providers.Where(p => !p.ShowGraphs).Select(p => p.Id).Should().Equal("codex");
    }

    [TestMethod]
    public async Task SaveAsync_FailedPersistence_RollsBackGraphsChoice()
    {
        var vm = Create();
        vm.Providers.Single(p => p.Id == "claude").ShowGraphs = false;
        _store.Setup(s => s.SaveAsync()).ThrowsAsync(new IOException("disk unavailable"));
        Func<Task> save = () => vm.SaveCommand.ExecuteAsync(null);
        await save.Should().ThrowAsync<IOException>();
        _settings.Providers.Should().OnlyContain(p => p.ShowGraphs);
    }

    [TestMethod]
    public async Task SaveAsync_TaskbarDisplayPositionChanged_PersistsAndLoadsBack()
    {
        var vm = Create();
        vm.TaskbarDisplayPosition.Should().Be("auto");
        vm.TaskbarDisplayPosition = "right";
        _settings.TaskbarDisplayPosition.Should().Be("auto"); // Edits stay local until Save.
        await vm.SaveCommand.ExecuteAsync(null);
        _settings.TaskbarDisplayPosition.Should().Be("right");
        vm.Load();
        vm.TaskbarDisplayPosition.Should().Be("right");
    }

    [TestMethod]
    public async Task SaveAsync_FailedPersistence_RollsBackTaskbarDisplayPosition()
    {
        var vm = Create();
        vm.TaskbarDisplayPosition = "left";
        _store.Setup(s => s.SaveAsync()).ThrowsAsync(new IOException("disk unavailable"));
        Func<Task> save = () => vm.SaveCommand.ExecuteAsync(null);
        await save.Should().ThrowAsync<IOException>();
        _settings.TaskbarDisplayPosition.Should().Be("auto");
    }

    [TestMethod]
    [DataRow("Left")]
    [DataRow("sideways")]
    public void Load_UnknownStoredTaskbarDisplayPosition_ShowsAutomatic(string stored)
    {
        _settings.TaskbarDisplayPosition = stored;
        var vm = Create();
        vm.TaskbarDisplayPosition.Should().Be("auto");
    }

    [TestMethod]
    public async Task SaveAsync_InvalidBillingEdits_NormalizesValues()
    {
        var vm = Create(); vm.ApiCreditsMonthlyUsd = -1; vm.ApiCreditsCycleStartDay = 31;
        await vm.SaveCommand.ExecuteAsync(null);
        _settings.ApiCreditsMonthlyUsd.Should().Be(200); _settings.ApiCreditsCycleStartDay.Should().Be(1);
    }
}
