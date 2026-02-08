namespace Akka.Serialization.V2;

/// <summary>
/// Applied to partial classes to define a serializer module.
/// The source generator will implement SerializerV2 for this class.
/// </summary>
/// <remarks>
/// Example usage:
/// <code>
/// [AkkaSerializerModule(SerializerId = 5001)]
/// public partial class UserMessageSerializer { }
/// </code>
///
/// The generator will:
/// - Implement SerializerV2 abstract members
/// - Generate Write() dispatch based on [AkkaSerializable] types
/// - Generate Read() dispatch based on manifest strings
/// - Generate field serialization code based on [AkkaField] indices
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class AkkaSerializerModuleAttribute : Attribute
{
    /// <summary>
    /// Gets the unique serializer identifier.
    /// Must be unique across all serializers in the ActorSystem.
    /// </summary>
    public required int SerializerId { get; init; }
}

/// <summary>
/// Applied to message types (classes or records) to mark them for serialization.
/// The manifest string is used for type identification during deserialization.
/// </summary>
/// <remarks>
/// Example usage:
/// <code>
/// [AkkaSerializable(Manifest = "user-created-v1")]
/// public sealed record UserCreated(
///     [property: AkkaField(0)] string UserId,
///     [property: AkkaField(1)] string Email,
///     [property: AkkaField(2)] DateTime CreatedAt);
/// </code>
///
/// Manifest naming conventions:
/// - Use kebab-case for consistency
/// - Include a version suffix (e.g., "-v1", "-v2")
/// - Breaking changes require a new manifest
/// - Extend-only changes can keep the same manifest
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
public sealed class AkkaSerializableAttribute : Attribute
{
    /// <summary>
    /// Gets the manifest string that identifies this type during deserialization.
    /// The manifest must be unique within the serializer module.
    /// </summary>
    public required string Manifest { get; init; }
}

/// <summary>
/// Applied to properties to define their serialization field index.
/// Fields are serialized in index order (0, 1, 2, ...).
/// </summary>
/// <remarks>
/// Example usage on record constructors (apply to property):
/// <code>
/// public sealed record UserCreated(
///     [property: AkkaField(0)] string UserId,
///     [property: AkkaField(1)] string Email,
///     [property: AkkaField(2)] DateTime CreatedAt);
/// </code>
///
/// Example usage on regular properties:
/// <code>
/// public class UserCreated
/// {
///     [AkkaField(0)]
///     public string UserId { get; set; }
///
///     [AkkaField(1)]
///     public string Email { get; set; }
/// }
/// </code>
///
/// Rules for version tolerance:
/// - Never reuse or reorder existing indices
/// - Add new fields with higher indices only
/// - Fields can be deprecated (stop writing) but cannot be removed from the index sequence
/// - Breaking schema changes require a new manifest
/// </remarks>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class AkkaFieldAttribute : Attribute
{
    /// <summary>
    /// Gets the zero-based field index.
    /// Fields are serialized in ascending index order.
    /// </summary>
    public int Index { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="AkkaFieldAttribute"/> class.
    /// </summary>
    /// <param name="index">The zero-based field index for serialization ordering</param>
    public AkkaFieldAttribute(int index)
    {
        Index = index;
    }
}
