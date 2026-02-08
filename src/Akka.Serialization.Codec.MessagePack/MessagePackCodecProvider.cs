using System.Buffers;
using Akka.Serialization.V2;

namespace Akka.Serialization.MessagePack;

/// <summary>
/// MessagePack-based implementation of ICodecProvider.
/// Creates MessagePackCodecWriter and MessagePackCodecReader instances.
/// </summary>
/// <remarks>
/// This is the default codec provider for SerializerV2.
/// Uses MessagePack-CSharp for compact binary encoding with zero-copy buffer management.
///
/// Writers and readers are lightweight and created per serialization/deserialization operation.
/// The provider itself can be shared across all serializers in an ActorSystem.
/// </remarks>
public sealed class MessagePackCodecProvider : ICodecProvider
{
    /// <summary>
    /// Singleton instance for convenience.
    /// </summary>
    public static readonly MessagePackCodecProvider Instance = new();

    public ICodecWriter CreateWriter(IBufferWriter<byte> buffer)
    {
        return new MessagePackCodecWriter(buffer);
    }

    public ICodecReader CreateReader(ReadOnlyMemory<byte> buffer)
    {
        return new MessagePackCodecReader(buffer);
    }
}
