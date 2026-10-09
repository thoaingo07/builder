namespace Builder.Application;

public sealed class NotFoundException(string what) : Exception($"{what} was not found.");

public sealed class ForbiddenException(string message) : Exception(message);

/// <summary>A git host, Azure DevOps or another upstream service failed or refused (surfaced as 502).</summary>
public sealed class ExternalServiceException(string message) : Exception(message);
