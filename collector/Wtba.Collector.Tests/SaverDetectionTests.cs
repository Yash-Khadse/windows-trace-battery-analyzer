using Wtba.Collector;

namespace Wtba.Collector.Tests;

/// <summary>
/// Energy saver detection. On the owner's Windows 11 25H2 machine the documented API (SystemStatusFlag, WinRT
/// EnergySaverStatus) stayed at "off" while the toggle was on; only HKLM\...\Control\Power\EnergySaverState changed
/// (1 = on, 2 = off). ResolveSaver must treat either signal as "on".
/// </summary>
public class SaverDetectionTests
{
    [Theory]
    [InlineData(0, 1, true)]    // observed: flag 0, registry 1 (Energy saver on)
    [InlineData(0, 2, false)]   // observed: flag 0, registry 2 (Energy saver off)
    [InlineData(1, 2, true)]    // documented flag set, registry says off: documented flag wins
    [InlineData(1, null, true)]
    [InlineData(0, null, false)] // registry value missing, flag clear
    [InlineData(-1, null, false)] // GetSystemPowerStatus unavailable (-1) must not read as bit 0 set
    [InlineData(-1, 1, true)]
    [InlineData(0, 0, false)]    // unknown registry values are not "on"
    [InlineData(0, 3, false)]
    public void ResolveSaver_UsesEitherSignal(int flag, int? reg, bool expected) =>
        Assert.Equal(expected, SystemStatus.ResolveSaver(flag, reg));

    [Fact]
    public void ReadEnergySaverState_NeverThrows()
    {
        var v = SystemStatus.ReadEnergySaverState(); // real read-only registry read; value depends on the machine
        Assert.True(v is null or >= 0);
    }
}
