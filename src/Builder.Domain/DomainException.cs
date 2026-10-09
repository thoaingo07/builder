namespace Builder.Domain;

/// <summary>A business rule was violated; surfaced to callers as 400/409.</summary>
public sealed class DomainException(string message) : Exception(message);
