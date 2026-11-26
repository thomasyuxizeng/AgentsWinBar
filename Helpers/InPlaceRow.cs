using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq.Expressions;
using System.Reflection;

namespace AgentsWinBar.Helpers;

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
    private static readonly ConcurrentDictionary<Type, Func<InPlaceRow, InPlaceRow, int>> CompiledUpdaters = new();
    private static readonly ConcurrentDictionary<string, PropertyChangedEventArgs> CachedEventArgs = new(StringComparer.Ordinal);
    private static readonly MethodInfo RaiseMethod = typeof(InPlaceRow).GetMethod(nameof(RaisePropertyChanged), BindingFlags.Instance | BindingFlags.NonPublic)!;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Copies every public read-write property from <paramref name="next"/> (same type), raising PropertyChanged for each
    /// value that changed, in declaration order with <see cref="UpdateLastAttribute"/> properties last. Returns the number
    /// of changed properties.
    /// </summary>
    public int UpdateFrom(InPlaceRow next)
    {
        if (next.GetType() != GetType()) throw new ArgumentException("Rows must have the same type.", nameof(next));
        return UpdateFromCore(next);
    }

    /// <summary>
    /// Typed subclasses override this for reflection-free, allocation-free property copying; other subclasses use a
    /// once-compiled expression delegate per type.
    /// </summary>
    protected virtual int UpdateFromCore(InPlaceRow next) =>
        CompiledUpdaters.GetOrAdd(GetType(), BuildUpdater)(this, next);

    protected void RaisePropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, CachedEventArgs.GetOrAdd(propertyName, static name => new PropertyChangedEventArgs(name)));

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

    private static Func<InPlaceRow, InPlaceRow, int> BuildUpdater(Type type)
    {
        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0 && p.GetCustomAttribute<KeepOnUpdateAttribute>() == null)
            .OrderBy(p => p.GetCustomAttribute<UpdateLastAttribute>() != null)
            .ThenBy(p => p.MetadataToken)
            .ToArray();

        var targetParam = Expression.Parameter(typeof(InPlaceRow), "target");
        var sourceParam = Expression.Parameter(typeof(InPlaceRow), "source");
        var typedTarget = Expression.Variable(type, "t");
        var typedSource = Expression.Variable(type, "s");
        var changed = Expression.Variable(typeof(int), "changed");

        var variables = new List<ParameterExpression> { typedTarget, typedSource, changed };
        var body = new List<Expression>
        {
            Expression.Assign(typedTarget, Expression.Convert(targetParam, type)),
            Expression.Assign(typedSource, Expression.Convert(sourceParam, type)),
            Expression.Assign(changed, Expression.Constant(0))
        };

        foreach (var prop in properties)
        {
            var propType = prop.PropertyType;
            var nextVal = Expression.Variable(propType, "v");
            variables.Add(nextVal);

            var comparerType = typeof(EqualityComparer<>).MakeGenericType(propType);
            var defaultComparer = Expression.Property(null, comparerType.GetProperty(nameof(EqualityComparer<object>.Default))!);
            var equalsMethod = comparerType.GetMethod(nameof(EqualityComparer<object>.Equals), [propType, propType])!;

            var targetProp = Expression.Property(typedTarget, prop);
            var sourceProp = Expression.Property(typedSource, prop);

            body.Add(Expression.Assign(nextVal, sourceProp));
            var areEqual = Expression.Call(defaultComparer, equalsMethod, targetProp, nextVal);
            var updateBlock = Expression.Block(
                Expression.Assign(targetProp, nextVal),
                Expression.PostIncrementAssign(changed),
                Expression.Call(targetParam, RaiseMethod, Expression.Constant(prop.Name)));
            body.Add(Expression.IfThen(Expression.Not(areEqual), updateBlock));
        }

        body.Add(changed);
        var block = Expression.Block(variables, body);
        return Expression.Lambda<Func<InPlaceRow, InPlaceRow, int>>(block, targetParam, sourceParam).Compile();
    }
}
