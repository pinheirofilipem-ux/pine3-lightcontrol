using Pine3LightControl.Core.Bluetooth;
using Pine3LightControl.Core.Lights;
using Xunit;

public class Sp110eProtocolTests
{
    [Fact]
    public void Commands_have_documented_layout()
    {
        Assert.Equal(new byte[] { 0, 0, 0, 0xAA }, Sp110e.PowerOn());
        Assert.Equal(new byte[] { 0, 0, 0, 0xAB }, Sp110e.PowerOff());
        Assert.Equal(new byte[] { 0x12, 0x34, 0x56, 0x1E }, Sp110e.Color(new RgbColor(0x12, 0x34, 0x56)));
        Assert.Equal(new byte[] { 0xFF, 0, 0, 0x2A }, Sp110e.Brightness(255));
        Assert.Equal(new byte[] { 0, 0, 0, 0x2A }, Sp110e.Brightness(-5));
        Assert.Equal(new byte[] { 0xFF, 0, 0, 0x69 }, Sp110e.White(300));
        Assert.Equal(new byte[] { 0xC6, 0, 0, 0x03 }, Sp110e.Speed(0xC6));
        Assert.Equal(new byte[] { 0x3F, 0, 0, 0x2C }, Sp110e.Mode(0x3F));
        Assert.Equal(new byte[] { 0, 0, 0, 0x06 }, Sp110e.Mode(Sp110e.ModeAuto));
        Assert.Equal(new byte[] { 0x03, 0, 0, 0x1C }, Sp110e.IcModel(3));
        Assert.Equal(new byte[] { 0x04, 0, 0, 0x3C }, Sp110e.RgbOrder(4));
        Assert.Equal(new byte[] { 0x01, 0x6A, 0, 0x2D }, Sp110e.PixelCount(362));
        Assert.Equal(new byte[] { 0, 0, 0, 0x10 }, Sp110e.GetInfo());
        Assert.Equal(0xD5, Sp110e.CheckDevice()[3]);
    }

    [Fact]
    public void Rename_encodes_length_and_ascii()
    {
        Assert.Equal(new byte[] { 8, 0, 0, 0xBB, 0x55, 0x70, 0x73, 0x74, 0x61, 0x69, 0x72, 0x73 }, Sp110e.Rename("Upstairs"));
        Assert.Equal(20, Sp110e.Rename(new string('x', 40))[0]);
    }

    [Fact]
    public void Invalid_arguments_throw()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Sp110e.PixelCount(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Sp110e.PixelCount(1025));
        Assert.Throws<ArgumentOutOfRangeException>(() => Sp110e.IcModel(27));
        Assert.Throws<ArgumentOutOfRangeException>(() => Sp110e.RgbOrder(6));
        Assert.Throws<ArgumentOutOfRangeException>(() => Sp110e.Mode(122));
    }

    [Fact]
    public void Parses_get_info_and_check_device_replies()
    {
        byte[] info = { 1, 0x79, 0x80, 0xC8, 3, 2, 0x01, 0x2C, 10, 20, 30, 40 };
        var s = Sp110e.ParseStatus(info)!;
        Assert.True(s.IsOn);
        Assert.Equal(121, s.Mode);
        Assert.Equal(0x80, s.Speed);
        Assert.Equal(200, s.Brightness);
        Assert.Equal(3, s.IcModel);
        Assert.Equal(2, s.RgbOrder);
        Assert.Equal(300, s.PixelCount);
        Assert.Equal(new RgbColor(10, 20, 30), s.Color);
        Assert.Equal(40, s.White);

        var check = new byte[] { 0x99 }.Concat(info).ToArray();
        Assert.Equal(s, Sp110e.ParseStatus(check));
        Assert.Null(Sp110e.ParseStatus(new byte[] { 1, 2, 3 }));
    }

    [Fact]
    public void Mode_names_are_indexed_by_mode()
    {
        Assert.Equal(122, Sp110e.ModeNames.Count);
        Assert.Equal("Static color", Sp110e.ModeNames[Sp110e.ModeStatic]);
        Assert.True(Sp110e.IsRgbw(24));
        Assert.False(Sp110e.IsRgbw(3));
    }
}
