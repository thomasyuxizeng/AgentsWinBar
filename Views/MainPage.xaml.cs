using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CodexBarWin.Controls;
using CodexBarWin.Helpers;
using CodexBarWin.Models;
using CodexBarWin.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace CodexBarWin.Views;

/// <summary>
/// One provider card on the flyout. A refresh replaces the provider's data in the view model; the slot stays, so the card
/// updates in place instead of being recreated.
/// </summary>
public sealed class ProviderSlot : InPlaceRow
{
    public string Provider { get; set; } = string.Empty;
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
            InPlaceRow.Apply(Slots, ViewModel.Providers.Select(p => new ProviderSlot { Provider = p.Provider, Data = p }).ToList(),
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
