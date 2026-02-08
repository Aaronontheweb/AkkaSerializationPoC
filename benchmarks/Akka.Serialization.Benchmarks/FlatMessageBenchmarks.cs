using System.Buffers;
using Akka.Serialization.MessagePack;
using Akka.Serialization.V2.Tests.Messages;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;

namespace Akka.Serialization.Benchmarks;

/// <summary>
/// Benchmarks comparing legacy byte[]-based serialization vs V2 IBufferWriter-based serialization
/// for flat messages (no nesting).
///
/// Legacy pattern: ArrayBufferWriter -> ToArray() -> byte[]
/// V2 pattern: ArrayBufferWriter (no ToArray, zero-copy)
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net90)]
public class FlatMessageBenchmarks
{
    private UserCreated _message = null!;
    private UserMessageSerializer _serializer = null!;
    private ArrayBufferWriter<byte> _buffer = null!;
    private byte[] _legacyBytes = null!;
    private ReadOnlyMemory<byte> _v2Bytes;

    [GlobalSetup]
    public void Setup()
    {
        _message = new UserCreated(
            "user-12345",
            "test@example.com",
            new DateTime(2025, 1, 15, 10, 30, 0, DateTimeKind.Utc));

        _serializer = new UserMessageSerializer();
        _buffer = new ArrayBufferWriter<byte>(256);

        // Pre-serialize for deserialization benchmarks
        // Legacy: serialize and convert to byte[]
        _buffer.Clear();
        using (var writer = MessagePackCodecProvider.Instance.CreateWriter(_buffer))
        {
            _serializer.Write(writer, _message);
            writer.Flush();
        }
        _legacyBytes = _buffer.WrittenSpan.ToArray();

        // V2: keep as ReadOnlyMemory<byte>
        _buffer.Clear();
        using (var writer = MessagePackCodecProvider.Instance.CreateWriter(_buffer))
        {
            _serializer.Write(writer, _message);
            writer.Flush();
        }
        _v2Bytes = _buffer.WrittenMemory;
    }

    [IterationSetup]
    public void IterationSetup()
    {
        _buffer.Clear();
    }

    /// <summary>
    /// Legacy pattern: Serialize to ArrayBufferWriter, then call ToArray() to get byte[].
    /// This simulates the old ToBinary pattern that required allocating a byte[] copy.
    /// </summary>
    [Benchmark(Baseline = true)]
    public byte[] Legacy_Serialize()
    {
        using var writer = MessagePackCodecProvider.Instance.CreateWriter(_buffer);
        _serializer.Write(writer, _message);
        writer.Flush();

        // Legacy pattern: must convert to byte[] (extra allocation + copy)
        return _buffer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// V2 pattern: Serialize to ArrayBufferWriter without calling ToArray().
    /// The buffer can be passed directly to the transport layer without copying.
    /// </summary>
    [Benchmark]
    public ArrayBufferWriter<byte> V2_Serialize()
    {
        using var writer = MessagePackCodecProvider.Instance.CreateWriter(_buffer);
        _serializer.Write(writer, _message);
        writer.Flush();

        // V2 pattern: return the buffer directly (zero-copy)
        return _buffer;
    }

    /// <summary>
    /// Legacy pattern: Deserialize from byte[] (which may have required a copy from the network buffer).
    /// </summary>
    [Benchmark]
    public UserCreated Legacy_Deserialize()
    {
        using var reader = MessagePackCodecProvider.Instance.CreateReader(_legacyBytes);
        return (UserCreated)_serializer.Read(reader, "user-created-v1");
    }

    /// <summary>
    /// V2 pattern: Deserialize from ReadOnlyMemory&lt;byte&gt; (zero-copy read from the buffer).
    /// </summary>
    [Benchmark]
    public UserCreated V2_Deserialize()
    {
        using var reader = MessagePackCodecProvider.Instance.CreateReader(_v2Bytes);
        return (UserCreated)_serializer.Read(reader, "user-created-v1");
    }
}
