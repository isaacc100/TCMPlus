namespace TCMPlus.Domain.Models;

public sealed record SessionDescriptor(
    Guid Id,
    DateTimeOffset StartedAt,
    string DirectoryPath,
    string DatabasePath);
