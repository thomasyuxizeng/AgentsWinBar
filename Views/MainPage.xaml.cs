using System.Collections.ObjectModel;
using System.Collections.Specialized;
using AgentsWinBar.Controls;
using AgentsWinBar.Helpers;
using AgentsWinBar.Models;
using AgentsWinBar.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace AgentsWinBar.Views;

/// <summary>
/// One provider card on the flyout. A refresh replaces the provider's data in the view model; the slot stays, so the card
/// updates in place instead of being recreated.
/// </summary>
public sealed class ProviderSlot : InPlaceRow
{
    public string Provider { get; set; } = string.Empty;
    /// <summary>The label column width every card shares (the widest any card needs), so all bars end at the same x.</summary>
    public double LabelWidth { get; set; }
    public UsageData? Data { get; set; }

    protected override int UpdateFromCore(InPlaceRow next)
    {
        var other = (ProviderSlot)next;
        var changed = 0;
        if (!string.Equals(Provider, other.Provider, StringComparison.Ordinal))
        {
            Provider = other.Provider;
            changed++;
            RaisePropertyChanged(nameof(Provider));
        }
        // Before the data, so a card redrawn for new data already has the width it ends up with
        if (LabelWidth != other.LabelWidth)
        {
            LabelWidth = other.LabelWidth;
            changed++;
            RaisePropertyChanged(nameof(LabelWidth));
        }
        if (!EqualityComparer<UsageData?>.Default.Equals(Data, other.Data))
        {
            Data = other.Data;
            changed++;
            RaisePropertyChanged(nameof(Data));
        }
        return changed;
    }
}

/// <summary>
/// Main page displaying provider usage data.
/// </summary>
public sealed partial class MainPage : Page
{
    public event EventHandler? SettingsRequested;
    public event EventHandler? DashboardRequested;
    public event EventHandler<UsageData>? ProviderDetailRequested;

    public MainViewModel ViewModel { get; }

    /// <summary>The cards shown: one slot per provider in the view model, in its order.</summary>
    public ObservableCollection<ProviderSlot> Slots { get; } = [];

    private volatile bool _isFlyoutVisible;
    private int _slotsDirty;
    private int _uiDirty;

    public MainPage()
    {
        InitializeComponent();
        ViewModel = App.Services.GetRequiredService<MainViewModel>();
    }

    internal void SetFlyoutVisible(bool visible)
    {
        _isFlyoutVisible = visible;
        if (!visible) return;
        if (Interlocked.Exchange(ref _slotsDirty, 0) != 0) SyncSlots();
        if (Interlocked.Exchange(ref _uiDirty, 0) != 0) UpdateUI();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ViewModel.Providers.CollectionChanged += OnProvidersChanged;
        Interlocked.Exchange(ref _slotsDirty, 0);
        Interlocked.Exchange(ref _uiDirty, 0);
        SyncSlots();
        UpdateUI();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ViewModel.Providers.CollectionChanged -= OnProvidersChanged;
    }

    private void OnProvidersChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!_isFlyoutVisible)
        {
            Volatile.Write(ref _slotsDirty, 1);
            return;
        }
        if (DispatcherQueue.HasThreadAccess) SyncSlots();
        else DispatcherQueue.TryEnqueue(SyncSlots);
    }

    private void SyncSlots()
    {
        try
        {
            var providers = ViewModel.Providers.ToList();
            // One label width for all cards: a card with a long label ("External 5h") no longer has shorter bars
            var scale = XamlRoot?.RasterizationScale ?? 1.0;
            var labelWidth = providers.Count == 0 ? 0 : providers.Max(p => ProviderCard.LabelColumnWidthFor(p, scale));
            InPlaceRow.Apply(Slots, providers.Select(p => new ProviderSlot { Provider = p.Provider, LabelWidth = labelWidth, Data = p }).ToList(),
                slot => slot.Provider);
        }
        catch (Exception ex)
        {
            App.Services.GetRequiredService<ILogger<MainPage>>().LogError(ex, "Updating the provider cards failed");
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (!_isFlyoutVisible)
        {
            Volatile.Write(ref _uiDirty, 1);
            return;
        }
        DispatcherQueue.TryEnqueue(UpdateUI);
    }

    private void UpdateUI()
    {
        // Update error panel
        if (!string.IsNullOrEmpty(ViewModel.ErrorMessage) && ViewModel.Providers.Count == 0)
        {
            ErrorPanel.Visibility = Visibility.Visible;
            ErrorMessageText.Text = ViewModel.ErrorMessage;
        }
        else
        {
            ErrorPanel.Visibility = Visibility.Collapsed;
        }

        // Update last updated text
        LastUpdatedText.Text = ViewModel.LastUpdatedText;
    }

    /// <summary>Raised after layout when the cards' total height changed (a card added, removed, or grown by new data).</summary>
    internal event EventHandler? CardsHeightChanged;

    /// <summary>True once the cards have been laid out with content (before that their height says nothing).</summary>
    internal bool CardsMeasured => ProvidersItemsControl.ActualHeight > 0;

    /// <summary>
    /// The page height in DIPs that shows every card without scrolling: the cards at their natural height (the scroll
    /// viewer measures them unconstrained), its padding, and the footer.
    /// </summary>
    internal double FullHeight() =>
        ProvidersScroll.Padding.Top + ProvidersItemsControl.ActualHeight + ProvidersScroll.Padding.Bottom + Footer.ActualHeight;

    private void OnCardsSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Height != e.PreviousSize.Height) CardsHeightChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnProviderCardLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is ProviderCard card)
        {
            // Remove existing handler to avoid duplicates, then add
            card.DetailRequested -= OnProviderDetailRequested;
            card.DetailRequested += OnProviderDetailRequested;
        }
    }

    private void OnProviderDetailRequested(object? sender, UsageData data)
    {
        ProviderDetailRequested?.Invoke(this, data);
    }

    private async void OnRefreshClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.RefreshCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            var logger = App.Services.GetRequiredService<ILogger<MainPage>>();
            logger.LogError(ex, "OnRefreshClicked failed");
        }
    }

    private void OnDashboardClicked(object sender, RoutedEventArgs e)
    {
        DashboardRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnSettingsClicked(object sender, RoutedEventArgs e)
    {
        SettingsRequested?.Invoke(this, EventArgs.Empty);
    }
}
