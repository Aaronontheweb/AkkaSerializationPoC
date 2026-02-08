namespace Akka.Serialization.V2.Tests.Messages;

/// <summary>
/// Sample message representing a user creation event.
/// </summary>
/// <param name="UserId">Unique identifier for the user</param>
/// <param name="Email">Email address</param>
/// <param name="CreatedAt">Timestamp when the user was created</param>
public sealed record UserCreated(string UserId, string Email, DateTime CreatedAt);

/// <summary>
/// Sample message representing a user update event.
/// </summary>
/// <param name="UserId">Unique identifier for the user</param>
/// <param name="NewEmail">Optional updated email address</param>
/// <param name="NewName">Optional updated name</param>
/// <param name="UpdatedAt">Timestamp when the user was updated</param>
public sealed record UserUpdated(string UserId, string? NewEmail, string? NewName, DateTime UpdatedAt);

/// <summary>
/// Sample message representing an order placement event.
/// </summary>
/// <param name="OrderId">Unique identifier for the order</param>
/// <param name="CustomerId">Identifier for the customer placing the order</param>
/// <param name="Amount">Order amount</param>
/// <param name="PlacedAt">Timestamp when the order was placed</param>
public sealed record OrderPlaced(Guid OrderId, string CustomerId, decimal Amount, DateTimeOffset PlacedAt);
