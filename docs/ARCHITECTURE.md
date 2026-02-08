# SerializerV2 Architecture

## Overview

The SerializerV2 system replaces Akka.NET's `byte[]`-based serialization with an `IBufferWriter<byte>`-based codec pattern. The key innovation is **zero-copy nested serialization**: envelope and inner message serializers share the same buffer writer, eliminating the 3-4 intermediate `byte[]` allocations that the current system requires for composed messages.

## Layer 1: Codec Abstraction (`ICodecWriter` / `ICodecReader`)

These interfaces abstract the wire format. The default implementation uses MessagePack, but the abstraction allows swapping to Protobuf, custom binary, etc.

```csharp
public interface ICodecWriter : IDisposable
{
    void BeginObject(int fieldCount);  // Array header

    // Primitives — called in field index order
    void WriteInt32(int value);
    void WriteInt64(long value);
    void WriteString(string? value);
    void WriteBool(bool value);
    void WriteDouble(double value);
    void WriteDateTime(DateTime value);
    void WriteDateTimeOffset(DateTimeOffset value);
    void WriteGuid(Guid value);
    void WriteBytes(ReadOnlySpan<byte> value);
    void WriteNull();

    void Flush();
}

public interface ICodecReader : IDisposable
{
    int BeginReadObject();  // Returns field count

    int ReadInt32();
    long ReadInt64();
    string? ReadString();
    bool ReadBool();
    double ReadDouble();
    DateTime ReadDateTime();
    DateTimeOffset ReadDateTimeOffset();
    Guid ReadGuid();
    byte[]? ReadBytes();

    bool TryReadNull();   // Peek + consume if null
    void SkipField();     // Skip unknown field (version tolerance)
    int Consumed { get; } // Bytes consumed so far
}
```

### Design Rationale

- **No generic `TBufferWriter` on methods** — simplifies the interface. `ICodecWriter` holds the `IBufferWriter<byte>` reference internally.
- **Sequential field writes** — fields are written in index order (array format). No field names on the wire.
- **`SkipField()` for version tolerance** — a V1 reader encountering V2 data with extra fields at the end just calls `SkipField()` for each unknown field.

## Layer 2: Codec Provider (`ICodecProvider`)

Factory that creates writers/readers. Decouples serializers from the wire format implementation.

```csharp
public interface ICodecProvider
{
    ICodecWriter CreateWriter(IBufferWriter<byte> buffer);
    ICodecReader CreateReader(ReadOnlyMemory<byte> buffer);
}
```

### MessagePack Implementation

**`MessagePackCodecWriter`**: Creates a lightweight `MessagePackWriter` ref struct per operation. Each call creates a writer, writes one value, flushes to the shared `IBufferWriter<byte>`. The buffer tracks position across calls — each new `MessagePackWriter` picks up where the previous left off.

**`MessagePackCodecReader`**: Stores `ReadOnlyMemory<byte>` and a consumed offset. Creates `MessagePackReader` per operation from `_buffer[_consumed..]`, advances `_consumed` by `reader.Consumed`.

This works because:
- `MessagePackWriter` is a lightweight ref struct — construction per-call is cheap
- MessagePack format is self-delimiting — each value knows its own length
- The actual parsing is the expensive part, not writer/reader construction

## Layer 3: SerializerV2

Base class that replaces `Serializer` / `SerializerWithStringManifest`.

```csharp
public abstract class SerializerV2
{
    public abstract int Identifier { get; }         // SerializerId
    public abstract string? Manifest(object obj);   // Type manifest

    /// Write to shared codec writer. Nested messages write to the same writer.
    public abstract void Write(ICodecWriter writer, object obj);

    /// Read from shared codec reader.
    public abstract object Read(ICodecReader reader, string manifest);

    /// Estimate serialized size for buffer pre-allocation.
    public virtual int SizeHint(object obj) => 256;
}
```

### Zero-Copy Nesting Pattern

The critical innovation. Consider a Remote envelope wrapping a DData envelope wrapping a user message:

```
Remote → DData → UserCreated
```

**Old system (3 allocations):**
1. `userSerializer.ToBinary(userCreated)` → `byte[] userBytes`
2. `ddataSerializer.ToBinary(ddataEnvelope)` → internally copies `userBytes` → `byte[] ddataBytes`
3. `remoteSerializer.ToBinary(remoteEnvelope)` → internally copies `ddataBytes` → `byte[] remoteBytes`

**V2 system (1 buffer):**
```csharp
// Single ArrayBufferWriter<byte> for everything
var buffer = new ArrayBufferWriter<byte>();
var writer = codecs.CreateWriter(buffer);

// Remote envelope writes its fields...
writer.BeginObject(4);
writer.WriteString(recipientPath);
writer.WriteString(senderPath);
writer.WriteInt32(innerSerializerId);
writer.WriteString(innerManifest);

// ...then delegates to DData serializer (SAME writer)
ddataSerializer.Write(writer, ddataEnvelope);
    // DData writes its fields...
    // writer.BeginObject(3);
    // writer.WriteString(key);
    // writer.WriteInt64(version);
    // ...then delegates to user serializer (SAME writer)
    // userSerializer.Write(writer, userCreated);
    //     // writer.BeginObject(3);
    //     // writer.WriteString(userId);
    //     // writer.WriteString(email);
    //     // writer.WriteDateTime(createdAt);

writer.Flush();
// buffer.WrittenSpan contains EVERYTHING — one contiguous block
```

On the wire, this produces nested MessagePack arrays:
```
[recipientPath, senderPath, serializerId, manifest, [key, version, serializerId, manifest, [userId, email, createdAt]]]
```

Each `BeginObject(N)` is a MessagePack array header. Nesting is natural in MessagePack — arrays can contain arrays.

## Wire Format

The `(SerializerId, Manifest, Payload)` contract is preserved:

```
┌─────────────────────────────┐
│ SerializerId (int)          │  ← Identifies which SerializerV2 to use
├─────────────────────────────┤
│ Manifest (string)           │  ← Type hint for polymorphic deserialization
├─────────────────────────────┤
│ Payload                     │  ← MessagePack-encoded fields (array format)
│  [field0, field1, ...]      │
│  (may contain nested arrays │
│   for composed messages)    │
└─────────────────────────────┘
```

## Backwards Compatibility

### SerializerV2Adapter

Wraps any legacy `Serializer` / `SerializerWithStringManifest` to work in the V2 system:

```csharp
public sealed class SerializerV2Adapter : SerializerV2
{
    private readonly Serializer _legacy;

    public override void Write(ICodecWriter writer, object obj)
    {
        byte[] bytes = _legacy.ToBinary(obj);
        writer.WriteBytes(bytes);  // Write legacy bytes as binary blob
    }

    public override object Read(ICodecReader reader, string manifest)
    {
        byte[]? bytes = reader.ReadBytes();
        return _legacy is SerializerWithStringManifest sm
            ? sm.FromBinary(bytes!, manifest)
            : _legacy.FromBinary(bytes!, typeof(object));
    }
}
```

Legacy serializers can coexist with V2 serializers in the same registry. An envelope can wrap a legacy-serialized message — the adapter writes the legacy bytes as a binary blob at the nesting point. On read, the adapter reads the blob and delegates to the legacy deserializer.

## Akka.NET Internal Type Codecs

Built-in codecs for serializing Akka.NET types as fields within messages:

- **ActorRef** — serialize as actor path string (`akka://system/user/myActor`), resolve on deserialization via `ActorSystem.Provider.ResolveActorRef(path)`. Requires ActorSystem reference on the serializer.
- **Props** — serialize type name + deploy config. Complex; may use legacy adapter initially.
- **StreamRef** — serialize underlying stream ref metadata (SourceRef/SinkRef).

## Version Tolerance

The array-based format supports extend-only design:

- **Adding fields**: New fields added at higher indices. Old readers call `SkipField()` for unknown trailing fields.
- **Reading old data**: New readers check `fieldCount` from `BeginReadObject()`. Missing fields get default values.
- **Breaking changes**: Require a new manifest string (e.g., `"user-created-v2"`).

```csharp
// V1: 3 fields
writer.BeginObject(3);
writer.WriteString(userId);
writer.WriteString(email);
writer.WriteDateTime(createdAt);

// V2: 4 fields (added displayName)
writer.BeginObject(4);
writer.WriteString(userId);
writer.WriteString(email);
writer.WriteDateTime(createdAt);
writer.WriteString(displayName);  // New field at index 3

// V1 reader handles V2 data:
int fieldCount = reader.BeginReadObject();  // returns 4
var userId = reader.ReadString();
var email = reader.ReadString();
var createdAt = reader.ReadDateTime();
for (int i = 3; i < fieldCount; i++) reader.SkipField();  // Skips displayName
```
