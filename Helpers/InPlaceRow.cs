using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Reflection;

namespace CodexBarWin.Helpers;

/// <summary>Copied after every other property by <see cref="InPlaceRow.UpdateFrom"/> (a bar's reset stamp must come after its window).</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class UpdateLastAttribute : Attribute;

/// <summary>Never copied by <see cref="InPlaceRow.UpdateFrom"/>: it depends only on the row's key (e.g. the provider icon).</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class KeepOnUpdateAttribute : Attribute;

/// <summary>
/// A row of a list the UI recomputes from scratch (the taskbar strip, the overlay, the flyout's provider cards). When the
/// recomputed list has the same rows in the same order, <see cref="Apply{T}"/> copies the new values into the rows already
/// shown, so their controls are updated instead of recreated; any other change replaces the list.
/// </summary>
public abstract class InPlaceRow : INotifyPropertyChanged
{
    private static readonly Dictionary<Type, PropertyInfo[]> CopiedProperties = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Copies every public read-write property from <paramref name="next"/> (same type), raising PropertyChanged for each
    /// value that changed, in declaration order with <see cref="UpdateLastAttribute"/> properties last. Returns the number
    /// of changed properties.
    /// </summary>
    public int UpdateFrom(InPlaceRow next)
    {
        if (next.GetType() != GetType()) throw new ArgumentException("Rows must have the same type.", nameof(next));
        var changed = 0;
        foreach (var property in PropertiesOf(GetType()))
        {
            var value = property.GetValue(next);
            if (Equals(property.GetValue(this), value)) continue;
            property.SetValue(this, value);
            changed++;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property.Name));
        }
        return changed;
    }

    /// <summary>
    /// Makes <paramref name="target"/> show <paramref name="next"/>: in place when both have the same keys in the same order,
    /// otherwise by replacing the contents. Returns true when the rows were updated in place.
    /// </summary>
    public static bool Apply<T>(ObservableCollection<T> target, IReadOnlyList<T> next, Func<T, string> key) where T : InPlaceRow
    {
        if (SameKeys(target, next, key))
        {
            for (var i = 0; i < next.Count; i++) target[i].UpdateFrom(next[i]);
            return true;
        }

        target.Clear();
        foreach (var row in next) target.Add(row);
        return false;
    }

    internal static bool SameKeys<T>(IReadOnlyList<T> current, IReadOnlyList<T> next, Func<T, string> key)
    {
        if (current.Count != next.Count || current.Count == 0) return false;
        for (var i = 0; i < current.Count; i++)
        {
            if (!string.Equals(key(current[i]), key(next[i]), StringComparison.Ordinal)) return false;
        }
        return true;
    }

    private static PropertyInfo[] PropertiesOf(Type type)
    {
        lock (CopiedProperties)
        {
            if (CopiedProperties.TryGetValue(type, out var cached)) return cached;
            var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0 && p.GetCustomAttribute<KeepOnUpdateAttribute>() == null)
                .OrderBy(p => p.GetCustomAttribute<UpdateLastAttribute>() != null)
                .ThenBy(p => p.MetadataToken)
                .ToArray();
            CopiedProperties[type] = properties;
            return properties;
        }
    }
}
