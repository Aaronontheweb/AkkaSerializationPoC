using System.Collections.Concurrent;

namespace Akka.Serialization.V2;

/// <summary>
/// Registry that maps types to serializers and serializer IDs to serializers.
/// Provides routing for serialization and deserialization operations.
/// </summary>
/// <remarks>
/// The registry maintains two indexes:
/// 1. SerializerId → SerializerV2 (for deserialization by ID)
/// 2. Type → SerializerV2 (for serialization type lookup)
///
/// Thread-safe for concurrent reads and writes.
/// Typically initialized once at ActorSystem startup.
/// </remarks>
public sealed class SerializerRegistry
{
    private readonly ConcurrentDictionary<int, SerializerV2> _byId = new();
    private readonly ConcurrentDictionary<Type, SerializerV2> _byType = new();
    private readonly Akka.Actor.ExtendedActorSystem? _system;

    /// <summary>
    /// Creates a registry without an ActorSystem reference.
    /// Serializers will not be able to resolve ActorRefs.
    /// </summary>
    public SerializerRegistry() { }

    /// <summary>
    /// Creates a registry with an ActorSystem reference.
    /// Registered serializers will have their System property set,
    /// enabling ActorRef resolution during serialization/deserialization.
    /// </summary>
    /// <param name="system">The ActorSystem to associate with serializers</param>
    public SerializerRegistry(Akka.Actor.ExtendedActorSystem system)
    {
        _system = system ?? throw new ArgumentNullException(nameof(system));
    }

    /// <summary>
    /// Registers a serializer for the given types.
    /// The serializer will be indexed by its Identifier and by each of the specified types.
    /// </summary>
    /// <param name="serializer">The serializer to register</param>
    /// <param name="types">The types this serializer can handle</param>
    /// <exception cref="ArgumentException">Thrown if a serializer with the same ID is already registered</exception>
    public void Register(SerializerV2 serializer, params Type[] types)
    {
        if (!_byId.TryAdd(serializer.Identifier, serializer))
        {
            throw new ArgumentException(
                $"Serializer with ID {serializer.Identifier} is already registered",
                nameof(serializer));
        }

        // Set the system reference if available
        if (_system != null)
        {
            serializer.System = _system;
        }

        foreach (var type in types)
        {
            if (_byType.TryGetValue(type, out var existing) && !ReferenceEquals(existing, serializer))
            {
                throw new ArgumentException(
                    $"Type {type} is already registered to serializer ID {existing.Identifier}, " +
                    $"cannot register to serializer ID {serializer.Identifier}",
                    nameof(types));
            }
            _byType[type] = serializer;
        }
    }

    /// <summary>
    /// Gets the serializer registered for the specified ID.
    /// </summary>
    /// <param name="serializerId">The serializer ID to lookup</param>
    /// <returns>The registered serializer</returns>
    /// <exception cref="KeyNotFoundException">Thrown if no serializer is registered for this ID</exception>
    public SerializerV2 GetById(int serializerId)
    {
        if (_byId.TryGetValue(serializerId, out var serializer))
        {
            return serializer;
        }

        throw new KeyNotFoundException($"No serializer registered for ID {serializerId}");
    }

    /// <summary>
    /// Finds the serializer registered for the specified type.
    /// Searches for exact type match first, then walks up the inheritance hierarchy.
    /// </summary>
    /// <param name="type">The type to find a serializer for</param>
    /// <returns>The registered serializer, or null if no match found</returns>
    public SerializerV2? FindFor(Type type)
    {
        // Try exact match first
        if (_byType.TryGetValue(type, out var serializer))
        {
            return serializer;
        }

        // Walk up inheritance hierarchy
        var current = type.BaseType;
        while (current != null)
        {
            if (_byType.TryGetValue(current, out serializer))
            {
                // Cache for future lookups
                _byType[type] = serializer;
                return serializer;
            }
            current = current.BaseType;
        }

        // Check interfaces
        foreach (var iface in type.GetInterfaces())
        {
            if (_byType.TryGetValue(iface, out serializer))
            {
                // Cache for future lookups
                _byType[type] = serializer;
                return serializer;
            }
        }

        return null;
    }

    /// <summary>
    /// Attempts to find the serializer registered for the specified object.
    /// </summary>
    /// <param name="obj">The object to find a serializer for</param>
    /// <returns>The registered serializer, or null if no match found</returns>
    public SerializerV2? FindFor(object obj)
    {
        ArgumentNullException.ThrowIfNull(obj);
        return FindFor(obj.GetType());
    }
}
