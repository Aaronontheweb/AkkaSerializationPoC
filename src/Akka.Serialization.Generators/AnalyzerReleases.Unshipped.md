### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
AKKA001 | Akka.Serialization | Error | AkkaSerializable type has zero AkkaField properties
AKKA002 | Akka.Serialization | Warning | Field indices have gaps
AKKA003 | Akka.Serialization | Error | Unsupported property type
AKKA004 | Akka.Serialization | Warning | AkkaSerializable types exist but no AkkaSerializer found
AKKA006 | Akka.Serialization | Error | Duplicate field indices on same type
AKKA007 | Akka.Serialization | Error | Duplicate manifests within same module
AKKA008 | Akka.Serialization | Warning | Orphaned serializable type (not covered by any protocol)
AKKA009 | Akka.Serialization | Error | AkkaSerializer must specify either Name or SerializerId
AKKA010 | Akka.Serialization | Error | Serializer ID collision between modules
AKKA011 | Akka.Serialization | Info | Computed serializer ID from Name via FNV-1a
