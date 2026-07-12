namespace TCMPlus.Domain.Models;

public sealed record SessionCatalogEntry(Guid Id, string ShiftName, DateTimeOffset CreatedAt, DateTimeOffset LastOpenedAt, string FilePath, bool IsLegacy = false);
