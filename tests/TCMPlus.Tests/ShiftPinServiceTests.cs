using TCMPlus.Infrastructure.Services;

namespace TCMPlus.Tests;

public sealed class ShiftPinServiceTests
{
    private readonly ShiftPinService _service = new();

    [Theory]
    [InlineData("123456")]
    [InlineData("000000")]
    public void Accepts_six_digit_PINs(string pin)
    {
        Assert.True(_service.IsValidFormat(pin));
    }

    [Theory]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("12AB56")]
    public void Rejects_invalid_PINs(string pin)
    {
        Assert.False(_service.IsValidFormat(pin));
    }

    [Fact]
    public void Hashes_and_verifies_a_PIN_without_storing_plaintext()
    {
        var settings = _service.CreateSettings("123456");

        Assert.True(settings.HasShiftPin);
        Assert.NotEqual("123456", settings.PinHash);
        Assert.True(_service.Verify("123456", settings));
        Assert.False(_service.Verify("654321", settings));
    }
}
