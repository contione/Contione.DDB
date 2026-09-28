namespace DynamoDb.Repository;

/// <summary>The stored item did not satisfy a conditional write; no changes were applied.</summary>
public sealed class DynamoDbConditionFailedException(Exception innerException)
    : InvalidOperationException("The DynamoDB write condition was not satisfied.", innerException);
