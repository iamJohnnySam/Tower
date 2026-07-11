using Tower.Core.Models;
using Tower.Core.Tuya;
using Xunit;
namespace Tower.Core.Tests;

public class TuyaCategoryTests
{
    [Theory]
    [InlineData("#ff0000", "000003e803e8")]
    [InlineData("#00ff00", "007803e803e8")]
    [InlineData("#0000ff", "00f003e803e8")]
    public void Rgb_to_tuya_hsv_hex(string rgb, string expected)
        => Assert.Equal(expected, TuyaColor.HsvHexFromRgb(rgb));

    [Fact] public void Tuya_hsv_hex_back_to_rgb_primary()
        => Assert.Equal("#ff0000", TuyaColor.RgbHexFromHsvHex("000003e803e8"));

    [Fact] public void Color_round_trips_within_tolerance()
    {
        var hex = TuyaColor.HsvHexFromRgb("#3d7fb0");
        var rgb = TuyaColor.RgbHexFromHsvHex(hex);
        Assert.StartsWith("#", rgb);
        Assert.Equal(7, rgb.Length);
    }

    [Fact] public void Resolve_uses_explicit_category()
        => Assert.Equal("color_bulb", TuyaCategories.Resolve("color_bulb", TuyaDeviceType.Plug).Key);

    [Fact] public void Resolve_falls_back_to_legacy_type()
    {
        Assert.Equal("dimmable_light", TuyaCategories.Resolve(null, TuyaDeviceType.Light).Key);
        Assert.Equal("ac_remote",      TuyaCategories.Resolve(null, TuyaDeviceType.AcRemote).Key);
        Assert.Equal("switch_4",       TuyaCategories.Resolve(null, TuyaDeviceType.Switch4).Key);
        Assert.Equal("plug",           TuyaCategories.Resolve("nonsense", TuyaDeviceType.Plug).Key);
    }

    [Fact] public void Actuatable_excludes_readonly_sensors()
    {
        Assert.True(TuyaCategories.Resolve("color_bulb", TuyaDeviceType.Plug).Actuatable);
        Assert.True(TuyaCategories.Resolve("switch_2", TuyaDeviceType.Plug).Actuatable);
        Assert.False(TuyaCategories.Resolve("climate_sensor", TuyaDeviceType.Plug).Actuatable);
    }
}
