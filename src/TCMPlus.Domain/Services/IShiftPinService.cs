using TCMPlus.Domain.Models;

namespace TCMPlus.Domain.Services;

public interface IShiftPinService
{
    bool IsValidFormat(string pin);
    TcSessionSettings CreateSettings(string pin);
    bool Verify(string pin, TcSessionSettings settings);
}
