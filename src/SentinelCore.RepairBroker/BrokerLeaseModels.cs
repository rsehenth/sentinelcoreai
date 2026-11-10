namespace SentinelCore.RepairBroker;

internal sealed record BrokerClaim(Guid ExecutionId, Guid AuthorizationId, DateTimeOffset ExpiresUtc);
internal sealed record BrokerLeaseState(int Version, Guid? ActiveExecution, BrokerClaim[] Recent);

