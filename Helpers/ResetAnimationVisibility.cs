namespace CodexBarWin.Helpers;

/// <summary>
/// Remembers usage-window resets that were detected while nobody could see the bars (the session was locked), so each
/// bar plays its reset animation once when the screen is visible again instead of animating on a locked screen where
/// it would be missed. The 15-second age limit of <see cref="BarResetAnimationPolicy"/> is then counted from the moment
/// the screen became visible, not from the detection.
/// </summary>
public sealed class ResetAnimationVisibility
{
    private const int MaxEntries = 32;

    public static ResetAnimationVisibility Shared { get; } = new();

    private readonly object _gate = new();
    private readonly Dictionary<long, DateTime?> _unseen = [];
    private readonly List<long> _order = [];
    private readonly List<WeakListener> _weakListeners = [];

    private sealed class WeakListener(WeakReference<object> target, Action<object> callback)
    {
        public WeakReference<object> Target { get; } = target;
        public Action<object> Callback { get; } = callback;
    }

    private sealed class WeakSubscription(ResetAnimationVisibility owner, WeakListener listener) : IDisposable
    {
        private ResetAnimationVisibility? _owner = owner;
        private WeakListener? _listener = listener;

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            var listener = Interlocked.Exchange(ref _listener, null);
            if (owner != null && listener != null) owner.UnsubscribeWeak(listener);
        }
    }

    /// <summary>Raised (on whatever thread called <see cref="NoteVisible"/>) when pending resets can play again.</summary>
    public event EventHandler? Resumed;

    internal int WeakListenerCount
    {
        get { lock (_gate) return _weakListeners.Count; }
    }

    /// <summary>
    /// Subscribes <paramref name="target"/> weakly to <see cref="Resumed"/> so a control whose <c>Unloaded</c>
    /// event never fires (e.g. inside a closed WinUI window) can still be garbage-collected.
    /// Pass a non-capturing <paramref name="onResumed"/> delegate.
    /// </summary>
    public IDisposable SubscribeResumed<TTarget>(TTarget target, Action<TTarget> onResumed) where TTarget : class
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(onResumed);
        var listener = new WeakListener(new WeakReference<object>(target), obj => onResumed((TTarget)obj));
        lock (_gate) _weakListeners.Add(listener);
        return new WeakSubscription(this, listener);
    }

    private void UnsubscribeWeak(WeakListener listener)
    {
        lock (_gate) _weakListeners.Remove(listener);
    }

    /// <summary>A reset detected at <paramref name="detectedUtc"/> while the screen could not be seen.</summary>
    public void NoteUnseen(DateTime detectedUtc)
    {
        var key = detectedUtc.ToUniversalTime().Ticks;
        lock (_gate)
        {
            if (_unseen.ContainsKey(key)) return;
            _unseen[key] = null;
            _order.Add(key);
            if (_order.Count > MaxEntries)
            {
                _unseen.Remove(_order[0]);
                _order.RemoveAt(0);
            }
        }
    }

    /// <summary>
    /// The screen can be seen again: every reset noted since it was last visible is now measured from
    /// <paramref name="nowUtc"/>. Returns false (and raises nothing) when there was nothing pending.
    /// </summary>
    public bool NoteVisible(DateTime nowUtc)
    {
        List<(object Target, Action<object> Callback)>? weakTargets = null;
        var changed = false;
        lock (_gate)
        {
            foreach (var key in _order)
            {
                if (_unseen[key] != null) continue;
                _unseen[key] = nowUtc.ToUniversalTime();
                changed = true;
            }

            if (changed && _weakListeners.Count > 0)
            {
                for (var i = _weakListeners.Count - 1; i >= 0; i--)
                {
                    if (_weakListeners[i].Target.TryGetTarget(out var target))
                    {
                        weakTargets ??= [];
                        weakTargets.Add((target, _weakListeners[i].Callback));
                    }
                    else
                    {
                        _weakListeners.RemoveAt(i);
                    }
                }
            }
        }

        if (changed)
        {
            Resumed?.Invoke(this, EventArgs.Empty);
            if (weakTargets != null)
            {
                for (var i = weakTargets.Count - 1; i >= 0; i--)
                    weakTargets[i].Callback(weakTargets[i].Target);
            }
        }
        return changed;
    }

    /// <summary>
    /// When the screen became visible after the reset detected at <paramref name="detectedUtc"/> was noted as unseen;
    /// null for a reset that was seen live, or one whose screen has not come back yet.
    /// </summary>
    public DateTime? VisibleSince(DateTime detectedUtc)
    {
        lock (_gate)
        {
            return _unseen.TryGetValue(detectedUtc.ToUniversalTime().Ticks, out var since) ? since : null;
        }
    }
}
