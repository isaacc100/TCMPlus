namespace TCMPlus.Domain.Models;

public sealed record Station(
    Guid Id,
    string Name,
    string Type,
    double GridX,
    double GridY,
    double GridWidth,
    double GridHeight);
