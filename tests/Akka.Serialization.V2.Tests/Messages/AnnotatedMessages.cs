using Akka.Serialization.V2;

namespace Akka.Serialization.V2.Tests.Messages;

/// <summary>
/// Marker interface for the annotated message protocol.
/// All [AkkaSerializable] types that belong to the AnnotatedMessageSerializer
/// must implement this interface. This is how the source generator scopes
/// which types belong to which serializer.
/// </summary>
public interface IAnnotatedProtocol { }

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
    [property: AkkaField(2)] DateTime CreatedAt) : IAnnotatedProtocol;

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
    [property: AkkaField(3)] DateTime UpdatedAt) : IAnnotatedProtocol;

/// <summary>
/// Annotated version of OrderPlaced showing diverse field types.
/// </summary>
/// <remarks>
/// Demonstrates:
/// - Guid field (OrderId)
/// - string field (CustomerId)
/// - decimal field (Amount) - maps to WriteDecimal/ReadDecimal
/// - DateTimeOffset field (PlacedAt)
/// </remarks>
[AkkaSerializable(Manifest = "order-placed-v1")]
public sealed record OrderPlacedAnnotated(
    [property: AkkaField(0)] Guid OrderId,
    [property: AkkaField(1)] string CustomerId,
    [property: AkkaField(2)] decimal Amount,
    [property: AkkaField(3)] DateTimeOffset PlacedAt) : IAnnotatedProtocol;

/// <summary>
/// Example serializer module for the annotated messages.
/// Uses the new SerializerV2&lt;TProtocol&gt; pattern where the generic type parameter
/// scopes which [AkkaSerializable] types are included.
/// </summary>
/// <remarks>
/// The source generator will:
/// - Implement SerializerV2 abstract members (Identifier, Manifest, Write, Read, SizeHint)
/// - Only include types that implement IAnnotatedProtocol
/// - Generate a Setup class with BoundTypes containing typeof(IAnnotatedProtocol)
///
/// The SerializerId is explicitly set to 6001 to maintain backwards compatibility
/// with existing test data. For new serializers, prefer using Name alone.
/// </remarks>
[AkkaSerializer(Name = "annotated-protocol", SerializerId = 6001)]
public partial class AnnotatedMessageSerializer : SerializerV2<IAnnotatedProtocol>
{
    // Source generator will implement SerializerV2 members here
}
