using System.Buffers;
using Akka.Actor;
using Akka.Serialization.MessagePack;
using Akka.Serialization.V2.Tests.Messages;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;

namespace Akka.Serialization.Benchmarks;

/// <summary>
/// Benchmarks comparing the default Akka.NET serializer (Newtonsoft.Json) vs V2 MessagePack
/// for flat messages (no nesting).
///
/// This represents the real migration path: users moving from the default Newtonsoft.Json
/// serializer to the new V2 codec-based system.
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
    private string _newtonsoftManifest = null!;

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

        // Legacy setup - get Akka.NET's built-in Newtonsoft.Json serializer
        _actorSystem = ActorSystem.Create("benchmark-system");
        _newtonsoftSerializer = ((ExtendedActorSystem)_actorSystem).Serialization
            .FindSerializerForType(typeof(UserCreated));

        // Pre-serialize for deserialization benchmarks
        _newtonsoftBytes = _newtonsoftSerializer.ToBinary(_message);

        if (_newtonsoftSerializer is Akka.Serialization.SerializerWithStringManifest sm)
            _newtonsoftManifest = sm.Manifest(_message);
        else
            _newtonsoftManifest = _message.GetType().FullName!;

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
    /// V2 MessagePack serialization: write to pre-allocated buffer, no byte[] copy.
    /// </summary>
    [Benchmark]
    public ArrayBufferWriter<byte> V2_Serialize()
    {
        var writer = MessagePackCodecProvider.Instance.CreateWriter(_buffer);
        _v2Serializer.Write(writer, _message);
        return _buffer;
    }

    /// <summary>
    /// Default Akka.NET deserialization: Newtonsoft.Json FromBinary() from byte[].
    /// </summary>
    [Benchmark]
    public object NewtonsoftJson_Deserialize()
    {
        return _newtonsoftSerializer.FromBinary(_newtonsoftBytes, typeof(UserCreated));
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
