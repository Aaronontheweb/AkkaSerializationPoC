using Akka.Serialization.V2;

namespace Akka.Serialization.V2.Tests.Messages;

/// <summary>
/// Annotated version of UserCreated showing what user code looks like with source generation attributes.
/// This demonstrates the attribute-driven approach for Phase 4-5 source generation.
/// </summary>
/// <remarks>
/// Compare this to the hand-written UserMessageSerializer - the source generator will
/// produce equivalent serialization code based on these attributes.
/// </remarks>
[AkkaSerializable(Manifest = "user-created-v1")]
public sealed record UserCreatedAnnotated(
    [property: AkkaField(0)] string UserId,
    [property: AkkaField(1)] string Email,
    [property: AkkaField(2)] DateTime CreatedAt);

/// <summary>
/// Annotated version of UserUpdated showing nullable field handling.
/// </summary>
/// <remarks>
/// Nullable fields (NewEmail?, NewName?) are automatically handled by the generator:
/// - During serialization: null writes WriteNull(), non-null writes the value
/// - During deserialization: TryReadNull() checks for null before reading
/// </remarks>
[AkkaSerializable(Manifest = "user-updated-v1")]
public sealed record UserUpdatedAnnotated(
    [property: AkkaField(0)] string UserId,
    [property: AkkaField(1)] string? NewEmail,
    [property: AkkaField(2)] string? NewName,
    [property: AkkaField(3)] DateTime UpdatedAt);

/// <summary>
/// Annotated version of OrderPlaced showing diverse field types.
/// </summary>
/// <remarks>
/// Demonstrates:
/// - Guid field (OrderId)
/// - string field (CustomerId)
/// - decimal field (Amount) - maps to WriteDouble/ReadDouble
/// - DateTimeOffset field (PlacedAt)
/// </remarks>
[AkkaSerializable(Manifest = "order-placed-v1")]
public sealed record OrderPlacedAnnotated(
    [property: AkkaField(0)] Guid OrderId,
    [property: AkkaField(1)] string CustomerId,
    [property: AkkaField(2)] decimal Amount,
    [property: AkkaField(3)] DateTimeOffset PlacedAt);

/// <summary>
/// Example serializer module that would be generated for the annotated messages.
/// In Phase 5, the source generator will produce the implementation for this partial class.
/// </summary>
/// <remarks>
/// User writes:
/// <code>
/// [AkkaSerializerModule(SerializerId = 6001)]
/// public partial class AnnotatedMessageSerializer { }
/// </code>
///
/// Generator produces:
/// - Identifier property returning 6001
/// - Manifest() method routing based on [AkkaSerializable] types
/// - Write() method dispatching to type-specific writers
/// - Read() method dispatching based on manifest string
/// - WriteXxx/ReadXxx methods for each [AkkaSerializable] type
/// - Field serialization in [AkkaField] index order
/// </remarks>
[AkkaSerializerModule(SerializerId = 6001)]
public partial class AnnotatedMessageSerializer
{
    // Source generator will implement SerializerV2 members here in Phase 5
    // For now, this is just a placeholder showing the intended usage pattern
}
