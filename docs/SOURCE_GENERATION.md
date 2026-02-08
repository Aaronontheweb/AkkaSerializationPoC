# Source Generation Guide

The Akka.Serialization.Generators package provides a Roslyn incremental source generator that produces `SerializerV2` implementations from annotated types — eliminating the boilerplate of hand-writing `Write`/`Read`/`Manifest` methods.

## Quick Start

1. Define a **protocol interface** — a marker interface that groups related message types:

```csharp
public interface IMyProtocol { }
```

2. Add attributes to your message types. Each type must implement the protocol interface:

```csharp
using Akka.Serialization.V2;

[AkkaSerializable(Manifest = "user-created-v1")]
public sealed record UserCreated(
    [property: AkkaField(0)] string UserId,
    [property: AkkaField(1)] string Email,
    [property: AkkaField(2)] DateTime CreatedAt) : IMyProtocol;
```

3. Create a serializer module — a partial class extending `SerializerV2<TProtocol>`:

```csharp
[AkkaSerializer(Name = "my-protocol")]
public partial class MySerializer : SerializerV2<IMyProtocol> { }
```

4. The generator produces the full `SerializerV2` implementation — `Identifier`, `Write`, `Read`, `Manifest`, and `SizeHint` — plus a `MySerializerSetup` metadata class with `BoundTypes` containing `typeof(IMyProtocol)`.

## Protocol Interface Pattern

The generic type parameter on `SerializerV2<TProtocol>` defines a **protocol scope**. Only `[AkkaSerializable]` types that implement `TProtocol` are included in that serializer. This design:

- **Supports multiple serializers per compilation** — each scoped to its own protocol interface
- **Enforces protocol grouping** — types must explicitly opt into a protocol
- **Enables interface-based registration** — `SerializerRegistry` resolves serializers by walking the type's interfaces

```csharp
// Two separate protocols in the same project — each gets its own serializer
public interface IInventoryProtocol { }
public interface IShippingProtocol { }

[AkkaSerializable(Manifest = "item-added-v1")]
public sealed record ItemAdded(...) : IInventoryProtocol;

[AkkaSerializable(Manifest = "shipment-created-v1")]
public sealed record ShipmentCreated(...) : IShippingProtocol;

[AkkaSerializer(Name = "inventory-protocol")]
public partial class InventorySerializer : SerializerV2<IInventoryProtocol> { }

[AkkaSerializer(Name = "shipping-protocol")]
public partial class ShippingSerializer : SerializerV2<IShippingProtocol> { }
```

## Attributes

### `[AkkaSerializable(Manifest = "...")]`

Marks a type for serialization. Applied to `record`, `class`, or `struct` types.

- **Manifest** (required): A stable string identifier for this type on the wire. Once published, never change it — changing a manifest is a breaking wire format change.
- Manifests should be versioned (e.g., `"user-created-v1"`) to allow future schema evolution.
- The type **must implement the protocol interface** of the serializer that will handle it.

### `[AkkaField(int index)]`

Marks a property for serialization and assigns its positional index.

- **Index** (required): Zero-based field position in the serialized array. Fields are written/read in index order.
- Applied via `[property: AkkaField(N)]` on record constructor parameters, or directly on properties.

### `[AkkaSerializer(Name = "...", SerializerId = N)]`

Marks a partial class extending `SerializerV2<TProtocol>` as the target for the generated serializer.

- **Name** (recommended): A logical name for the serializer. Hashed via FNV-1a to produce a deterministic positive `int32` serializer ID.
- **SerializerId** (optional): Explicit ID override. When non-zero, takes precedence over the FNV-1a hash of Name.
- At least one of `Name` or `SerializerId` must be provided.
- The class must be `partial` and must extend `SerializerV2<TProtocol>`.

## Serializer ID Assignment

There are two ways to assign a serializer ID:

### 1. Name-based (recommended)

Provide a `Name` string. The generator computes a deterministic positive `int32` via FNV-1a hashing:

```csharp
[AkkaSerializer(Name = "my-protocol")]  // ID computed from "my-protocol"
public partial class MySerializer : SerializerV2<IMyProtocol> { }
```

The computed ID is reported as an AKKA011 info diagnostic so you can see it at build time.

### 2. Explicit override

Provide a `SerializerId` integer directly. This takes precedence over any Name-based hash:

```csharp
[AkkaSerializer(Name = "my-protocol", SerializerId = 6001)]  // ID = 6001, ignores hash
public partial class MySerializer : SerializerV2<IMyProtocol> { }
```

Use explicit IDs for backwards compatibility with existing serializer configurations.

### FNV-1a Hash Algorithm

The hash is computed over UTF-8 bytes of the Name string, then masked to a positive 31-bit int:

```
FNV offset basis = 2166136261
FNV prime = 16777619
hash = offset_basis
for each byte in UTF8(name):
    hash = hash XOR byte
    hash = hash * prime
result = hash AND 0x7FFFFFFF  // ensure positive
```

## Field Ordering Rules

Fields are serialized **by their `[AkkaField(N)]` index**, not by declaration order. This is critical for wire compatibility:

```csharp
// These produce IDENTICAL wire format:

// Declaration order matches index order
public sealed record Msg(
    [property: AkkaField(0)] string A,
    [property: AkkaField(1)] string B);

// Declaration order differs from index order — still writes A first, then B
public sealed record Msg(
    [property: AkkaField(1)] string B,
    [property: AkkaField(0)] string A);
```

### Rules:
- Indices must start at 0 and be contiguous (0, 1, 2, ...). Gaps produce a warning (AKKA002).
- Duplicate indices on the same type produce an error (AKKA006).
- Once a field index is assigned and data is published, **never change it**.

## Adding and Removing Fields

The generated deserializer uses `fieldCount`-guarded reads, enabling forward and backward compatibility:

### Adding a new field (backward compatible)

Add the field at the next available index. Old data with fewer fields will use the type's default value:

```csharp
// V1: 2 fields
[AkkaSerializable(Manifest = "user-created-v1")]
public sealed record UserCreated(
    [property: AkkaField(0)] string UserId,
    [property: AkkaField(1)] string Email) : IMyProtocol;

// V2: 3 fields — old data still deserializes correctly
[AkkaSerializable(Manifest = "user-created-v1")]
public sealed record UserCreated(
    [property: AkkaField(0)] string UserId,
    [property: AkkaField(1)] string Email,
    [property: AkkaField(2)] DateTime CreatedAt) : IMyProtocol;  // defaults to default(DateTime) for old data
```

**Keep the same manifest** — the version suffix is for documentation, not automatic version routing.

### Removing a field

Never remove a field from the middle. Instead, stop writing it but keep it in the schema to maintain index stability. Or simply leave the field in place — unused fields have minimal overhead.

### Forward compatibility

If a newer serializer writes more fields than the reader knows about, the extra fields are automatically skipped. This means:
- V2 data (4 fields) can be read by V1 reader (expects 3 fields) — the 4th field is skipped.

## Supported Types

| C# Type | Writer/Reader Method | Notes |
|---------|---------------------|-------|
| `string` | `WriteString`/`ReadString` | Non-nullable defaults to `string.Empty` |
| `string?` | `WriteString`/`ReadString` + null check | Uses `TryReadNull()` pattern |
| `int` | `WriteInt32`/`ReadInt32` | |
| `long` | `WriteInt64`/`ReadInt64` | |
| `bool` | `WriteBool`/`ReadBool` | |
| `double` | `WriteDouble`/`ReadDouble` | |
| `decimal` | `WriteDecimal`/`ReadDecimal` | Lossless via `decimal.GetBits()` 4-int encoding |
| `DateTime` | `WriteDateTime`/`ReadDateTime` | |
| `DateTimeOffset` | `WriteDateTimeOffset`/`ReadDateTimeOffset` | Preserves offset |
| `Guid` | `WriteGuid`/`ReadGuid` | |
| `byte[]` | `WriteBytes`/`ReadBytes` | |

Unsupported types (collections, custom classes, etc.) produce a compile-time error (AKKA003).

## Diagnostics

The generator reports compile-time diagnostics:

| ID | Severity | Description |
|----|----------|-------------|
| AKKA001 | Error | `[AkkaSerializable]` type has zero `[AkkaField]` properties |
| AKKA002 | Warning | Field indices have gaps (e.g., 0, 2, 5 — missing 1) |
| AKKA003 | Error | Property has an unsupported type |
| AKKA004 | Warning | `[AkkaSerializable]` types exist but no `[AkkaSerializer]` was found |
| AKKA006 | Error | Duplicate field indices on the same type |
| AKKA007 | Error | Duplicate manifests across types in the same module |
| AKKA008 | Warning | Orphaned `[AkkaSerializable]` type — not covered by any module's protocol interface |
| AKKA009 | Error | `[AkkaSerializer]` must specify either `Name` or `SerializerId` |
| AKKA010 | Error | Serializer ID collision between multiple `[AkkaSerializer]` modules |
| AKKA011 | Info | Computed serializer ID from `Name` via FNV-1a (informational) |

## Generated Code

For a module `MySerializer` with protocol `IMyProtocol` and types `UserCreated` and `OrderPlaced`, the generator produces:

### Serializer class

```csharp
// No base class in the generated partial — user's partial already has : SerializerV2<IMyProtocol>
public partial class MySerializer
{
    public override int Identifier => 1234567;  // FNV-1a hash of Name, or explicit SerializerId

    public override string? Manifest(object obj) => obj switch
    {
        UserCreated => "user-created-v1",
        OrderPlaced => "order-placed-v1",
        _ => throw new ArgumentException(...)
    };

    public override void Write(ICodecWriter writer, object obj) { /* switch dispatch */ }
    public override object Read(ICodecReader reader, string manifest) { /* manifest dispatch */ }

    // Per-type methods with fieldCount guards for backward compat
    private void WriteUserCreated(ICodecWriter writer, UserCreated msg) { ... }
    private UserCreated ReadUserCreated(ICodecReader reader) { ... }
}
```

### Setup class

Always generated alongside the serializer:

```csharp
public sealed class MySerializerSetup
{
    public static readonly MySerializerSetup Instance = new();
    public Type SerializerType => typeof(MySerializer);
    // BoundTypes contains the protocol interface, NOT individual concrete types
    public static Type[] BoundTypes => new Type[] { typeof(IMyProtocol) };
}
```

## Registration

### Manual registration with SerializerRegistry

```csharp
var registry = new SerializerRegistry();
var serializer = new MySerializer();
// Register with the protocol interface — registry walks interfaces to resolve concrete types
registry.Register(serializer, MySerializerSetup.BoundTypes);
```

### Multiple serializers

```csharp
var registry = new SerializerRegistry();
registry.Register(new InventorySerializer(), InventorySerializerSetup.BoundTypes);
registry.Register(new ShippingSerializer(), ShippingSerializerSetup.BoundTypes);

// Each type resolves to its protocol's serializer
registry.FindFor(new ItemAdded(...));       // → InventorySerializer
registry.FindFor(new ShipmentCreated(...)); // → ShippingSerializer
```

### With ActorSystem (for ActorRef resolution)

```csharp
var system = ActorSystem.Create("my-system");
var registry = new SerializerRegistry((ExtendedActorSystem)system);
var serializer = new MySerializer();
registry.Register(serializer, MySerializerSetup.BoundTypes);
// serializer.System is now set, enabling ActorRef path resolution
```

## Migration from `[AkkaSerializerModule]`

If you previously used the `[AkkaSerializerModule]` pattern:

### Before (old pattern)

```csharp
[AkkaSerializerModule(SerializerId = 5001)]
public partial class MySerializer { }

// All [AkkaSerializable] types in the compilation were included
[AkkaSerializable(Manifest = "user-created-v1")]
public sealed record UserCreated(...);
```

### After (new pattern)

```csharp
// 1. Define a protocol interface
public interface IMyProtocol { }

// 2. Message types implement the protocol
[AkkaSerializable(Manifest = "user-created-v1")]
public sealed record UserCreated(...) : IMyProtocol;

// 3. Serializer extends SerializerV2<TProtocol> and uses [AkkaSerializer]
[AkkaSerializer(Name = "my-protocol", SerializerId = 5001)]  // keep old ID for compat
public partial class MySerializer : SerializerV2<IMyProtocol> { }

// 4. Register with protocol interface instead of individual types
registry.Register(serializer, typeof(IMyProtocol));
// instead of: registry.Register(serializer, typeof(UserCreated), typeof(OrderPlaced), ...);
```

## Nested Types

The generator handles types nested inside other classes:

```csharp
public class MyActor
{
    [AkkaSerializable(Manifest = "create-user-v1")]
    public sealed record CreateUser(
        [property: AkkaField(0)] string Name) : IMyProtocol;
}
```

The generated code uses the fully qualified name `MyActor.CreateUser` in all references.
