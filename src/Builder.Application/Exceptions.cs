namespace Builder.Application;

public sealed class NotFoundException(string what) : Exception($"{what} was not found.");

public sealed class ForbiddenException(string message) : Exception(message);
