using System.Buffers;

namespace Akka.Serialization.V2;

/// <summary>
/// Factory for creating codec writers and readers.
/// Decouples serializers from the underlying wire format implementation.
/// </summary>
/// <remarks>
/// Different implementations can provide different wire formats:
/// - MessagePackCodecProvider: Uses MessagePack-CSharp for compact binary encoding
/// - ProtobufCodecProvider: Could use Protocol Buffers
/// - JsonCodecProvider: Could use UTF-8 JSON for debugging/interop
///
/// The provider is typically registered once per ActorSystem and reused across all serializers.
/// Writers and readers are lightweight and created per serialization/deserialization operation.
/// </remarks>
public interface ICodecProvider
{
    /// <summary>
    /// Creates a new codec writer that writes to the specified buffer.
    /// The writer maintains a reference to the buffer and writes data directly to it.
    /// Multiple write operations advance the buffer's position.
    /// </summary>
    /// <param name="buffer">The buffer to write encoded data to</param>
    /// <returns>A new codec writer instance</returns>
    ICodecWriter CreateWriter(IBufferWriter<byte> buffer);

    /// <summary>
    /// Creates a new codec reader that reads from the specified buffer.
    /// The reader maintains an internal position offset and advances it as data is consumed.
    /// </summary>
    /// <param name="buffer">The buffer containing encoded data to read</param>
    /// <returns>A new codec reader instance</returns>
    ICodecReader CreateReader(ReadOnlyMemory<byte> buffer);
}
