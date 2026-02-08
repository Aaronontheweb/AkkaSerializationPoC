using System.Buffers;
using Akka.Actor;
using Akka.Serialization.MessagePack;
using Akka.Serialization.V2.Tests.Messages;
using Akka.Util.Internal;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using MessagePack;

namespace Akka.Serialization.Benchmarks;

/// <summary>
/// Benchmarks comparing four serialization approaches for flat messages (no nesting):
///
/// 1. Newtonsoft.Json (Akka.NET default) — what most users have today
/// 2. V1 MessagePack (ToBinary() -> byte[]) — same wire format as V2, but legacy API shape
/// 3. MsgPackSerializer (Akka.NET's built-in MessagePack) — existing Akka.NET MessagePack serializer
/// 4. V2 MessagePack (ICodecWriter on shared buffer) — new API
///
/// Comparing V2 vs Newtonsoft.Json shows the full migration benefit.
/// Comparing V2 vs V1 MessagePack isolates the API shape improvement (IBufferWriter vs byte[]).
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net90)]
public class FlatMessageBenchmarks
{
    private UserCreated _message = null!;
    private UserMessageSerializer _v2Serializer = null!;
    private ArrayBufferWriter<byte> _buffer = null!;

    // Legacy Newtonsoft.Json (Akka.NET default)
    private ActorSystem _actorSystem = null!;
    private Akka.Serialization.Serializer _newtonsoftSerializer = null!;
    private byte[] _newtonsoftBytes = null!;

    // V1-style MessagePack (ToBinary() -> byte[] pattern)
    private V1MessagePackUserSerializer _v1Serializer = null!;
    private byte[] _v1Bytes = null!;

    // Akka.NET MsgPackSerializer (for comparison with V1/V2)
    private MsgPackSerializer _msgPackSerializer = null!;
    private byte[] _msgPackBytes = null!;

    // V2 pre-serialized
    private byte[] _v2Bytes = null!;

    [GlobalSetup]
    public void Setup()
    {
        _message = new UserCreated(
            "user-12345",
            "test@example.com",
            new DateTime(2025, 1, 15, 10, 30, 0, DateTimeKind.Utc));

        // V2 setup
        _v2Serializer = new UserMessageSerializer();
        _buffer = new ArrayBufferWriter<byte>(256);

        // Legacy Newtonsoft.Json setup
        _actorSystem = ActorSystem.Create("benchmark-system");
        _newtonsoftSerializer = ((ExtendedActorSystem)_actorSystem).Serialization
            .FindSerializerForType(typeof(UserCreated));

        // V1 MessagePack setup (same wire format, legacy API shape)
        _v1Serializer = new V1MessagePackUserSerializer();

        // MsgPackSerializer setup (Akka.NET's built-in MessagePack serializer)
        _msgPackSerializer = new MsgPackSerializer(_actorSystem.AsInstanceOf<ExtendedActorSystem>());

        // Pre-serialize for deserialization benchmarks
        _newtonsoftBytes = _newtonsoftSerializer.ToBinary(_message);
        _v1Bytes = _v1Serializer.ToBinary(_message);
        _msgPackBytes = _msgPackSerializer.ToBinary(_message);

        var v2Buffer = new ArrayBufferWriter<byte>(256);
        var writer = MessagePackCodecProvider.Instance.CreateWriter(v2Buffer);
        _v2Serializer.Write(writer, _message);
        _v2Bytes = v2Buffer.WrittenSpan.ToArray();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _actorSystem?.Terminate().Wait(TimeSpan.FromSeconds(5));
    }

    [IterationSetup]
    public void IterationSetup()
    {
        _buffer.Clear();
    }

    // =====================================================================
    // Serialization
    // =====================================================================

    /// <summary>
    /// Default Akka.NET serialization: Newtonsoft.Json ToBinary() -> byte[].
    /// This is what most Akka.NET users have today.
    /// </summary>
    [Benchmark(Baseline = true)]
    public byte[] NewtonsoftJson_Serialize()
    {
        return _newtonsoftSerializer.ToBinary(_message);
    }

    /// <summary>
    /// V1-style MessagePack: same wire format as V2, but using legacy ToBinary() -> byte[] API.
    /// Isolates the wire format improvement from the API shape improvement.
    /// </summary>
    [Benchmark]
    public byte[] V1MessagePack_Serialize()
    {
        return _v1Serializer.ToBinary(_message);
    }

    /// <summary>
    /// MsgPackSerializer: Akka.NET's built-in MessagePack serializer.
    /// </summary>
    [Benchmark]
    public byte[] MsgPackSerializer_Serialize()
    {
        return _msgPackSerializer.ToBinary(_message);
    }

    /// <summary>
    /// V2 MessagePack serialization: write to pre-allocated buffer, no byte[] copy.
    /// </summary>
    [Benchmark]
    public ArrayBufferWriter<byte> V2_Serialize()
    {
        var writer = MessagePackCodecProvider.Instance.CreateWriter(_buffer);
        _v2Serializer.Write(writer, _message);
        return _buffer;
    }

    // =====================================================================
    // Deserialization
    // =====================================================================

    /// <summary>
    /// Default Akka.NET deserialization: Newtonsoft.Json FromBinary() from byte[].
    /// </summary>
    [Benchmark]
    public object NewtonsoftJson_Deserialize()
    {
        return _newtonsoftSerializer.FromBinary(_newtonsoftBytes, typeof(UserCreated));
    }

    /// <summary>
    /// V1-style MessagePack deserialization from byte[].
    /// </summary>
    [Benchmark]
    public UserCreated V1MessagePack_Deserialize()
    {
        return _v1Serializer.FromBinary(_v1Bytes);
    }

    /// <summary>
    /// MsgPackSerializer: Akka.NET's built-in MessagePack deserializer.
    /// </summary>
    [Benchmark]
    public object MsgPackSerializer_Deserialize()
    {
        return _msgPackSerializer.FromBinary(_msgPackBytes, typeof(UserCreated));
    }

    /// <summary>
    /// V2 MessagePack deserialization from pre-serialized bytes.
    /// </summary>
    [Benchmark]
    public UserCreated V2_Deserialize()
    {
        var reader = MessagePackCodecProvider.Instance.CreateReader(_v2Bytes);
        return (UserCreated)_v2Serializer.Read(reader, "user-created-v1");
    }
}

/// <summary>
/// V1-style serializer using MessagePack but returning byte[] (legacy ToBinary() API shape).
/// Produces the same wire format as the V2 serializer — same MessagePack array layout.
/// This lets us isolate the API overhead (byte[] alloc + copy) from the wire format choice.
/// </summary>
internal sealed class V1MessagePackUserSerializer
{
    public byte[] ToBinary(UserCreated msg)
    {
        var buffer = new ArrayBufferWriter<byte>(128);
        var mpWriter = new MessagePackWriter(buffer);
        mpWriter.WriteArrayHeader(3);
        mpWriter.Write(msg.UserId);
        mpWriter.Write(msg.Email);
        mpWriter.Write(msg.CreatedAt.ToBinary());
        mpWriter.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    public UserCreated FromBinary(byte[] bytes)
    {
        var reader = new MessagePackReader(bytes);
        reader.ReadArrayHeader();
        var userId = reader.ReadString()!;
        var email = reader.ReadString()!;
        var createdAt = DateTime.FromBinary(reader.ReadInt64());
        return new UserCreated(userId, email, createdAt);
    }
}
