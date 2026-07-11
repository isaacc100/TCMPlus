using System.Security.Cryptography;
using TCMPlus.Domain.Models;
using TCMPlus.Domain.Services;

namespace TCMPlus.Infrastructure.Services;

public sealed class ShiftPinService : IShiftPinService
{
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const int Iterations = 210_000;

    public bool IsValidFormat(string pin) => pin.Length == 6 && pin.All(char.IsAsciiDigit);

    public TcSessionSettings CreateSettings(string pin)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pin);
        if (!IsValidFormat(pin))
        {
            throw new ArgumentException("The shift PIN must contain exactly six digits.", nameof(pin));
        }

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(pin, salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        return new TcSessionSettings(null, Convert.ToBase64String(salt), Convert.ToBase64String(hash));
    }

    public bool Verify(string pin, TcSessionSettings settings)
    {
        if (!IsValidFormat(pin) || !settings.HasShiftPin)
        {
            return false;
        }

        var salt = Convert.FromBase64String(settings.PinSalt!);
        var expected = Convert.FromBase64String(settings.PinHash!);
        var actual = Rfc2898DeriveBytes.Pbkdf2(pin, salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
