namespace Akka.Serialization.V2.Tests.Messages;

/// <summary>
/// Message that contains an ActorRef path, demonstrating how V2 serializers
/// handle ActorRef serialization by writing the path string.
/// </summary>
/// <param name="SubscriberPath">The actor ref path of the subscriber</param>
/// <param name="Topic">The topic to subscribe to</param>
public sealed record SubscribeToEvents(string SubscriberPath, string Topic);
