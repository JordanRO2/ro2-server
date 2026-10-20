using System.Collections.Concurrent;
using System.Reflection;

namespace RO2.Tests.Economy;

/// <summary>
/// Shallow copy of an entity's public read/write properties. The fake stores copy on the way in and on the way
/// out, like a real database does: a caller that mutates a row it read changes nothing until it writes it back,
/// and two callers that read the same row hold two different (possibly stale) objects, which is what makes a
/// lost update reproducible.
/// </summary>
internal static class EntityCopy
{
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> Props = new();

    public static T Clone<T>(T source) where T : class, new()
    {
        var copy = new T();
        foreach (var p in Props.GetOrAdd(typeof(T), t =>
                     [.. t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                         .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0)]))
            p.SetValue(copy, p.GetValue(source));
        return copy;
    }
}
