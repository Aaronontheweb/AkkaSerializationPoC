namespace Akka.Serialization.V2.Tests.Messages;

/// <summary>
/// Envelope message representing a remote actor message.
/// Wraps an inner message with routing metadata.
/// </summary>
/// <param name="RecipientPath">Actor path of the message recipient</param>
/// <param name="SenderPath">Actor path of the message sender</param>
/// <param name="Message">The inner message being sent</param>
public sealed record RemoteEnvelope(string RecipientPath, string SenderPath, object Message);

/// <summary>
/// Envelope message representing a distributed data update.
/// Wraps an inner data payload with versioning metadata.
/// </summary>
/// <param name="Key">The distributed data key</param>
/// <param name="Version">The version number of this data update</param>
/// <param name="Data">The inner data payload</param>
public sealed record DDataEnvelope(string Key, long Version, object Data);
