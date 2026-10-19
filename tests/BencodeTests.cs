using System.Text;

namespace Dlt.Tests;

public class BencodeTests
{
    [Fact]
    public void DecodesIntegers()
    {
        Assert.Equal(42L, Bencode.Decode("i42e"u8));
        Assert.Equal(-7L, Bencode.Decode("i-7e"u8));
        Assert.Equal(0L, Bencode.Decode("i0e"u8));
    }

    [Fact]
    public void DecodesStringsAsBytes()
    {
        var s = Assert.IsType<byte[]>(Bencode.Decode("3:abc"u8));
        Assert.Equal("abc"u8.ToArray(), s);
        Assert.Empty(Assert.IsType<byte[]>(Bencode.Decode("0:"u8)));
    }

    [Fact]
    public void DecodesLists()
    {
        var list = Assert.IsType<List<object>>(Bencode.Decode("li1e1:xli2eee"u8));
        Assert.Equal(3, list.Count);
        Assert.Equal(1L, list[0]);
        Assert.Equal("x"u8.ToArray(), list[1]);
        Assert.Equal(2L, Assert.IsType<List<object>>(list[2]).Single());
    }

    [Fact]
    public void DecodesDictionariesWithLatin1Keys()
    {
        var data = Ben.Bytes("d1:ai1e2:", new byte[] { 0xFF, 0x00 }, "3:xyze");
        var dict = Bencode.DecodeDictionary(data);
        Assert.Equal(1L, Bencode.GetInt(dict, "a"));
        Assert.Equal("xyz", Bencode.GetString(dict, "ÿ\u0000"));
    }

    [Fact]
    public void AccessorsReturnNullOnMissingOrWrongType()
    {
        var dict = Bencode.DecodeDictionary("d1:a1:b1:ni5ee"u8);
        Assert.Null(Bencode.GetInt(dict, "a"));
        Assert.Null(Bencode.GetString(dict, "n"));
        Assert.Null(Bencode.GetInt(dict, "missing"));
        Assert.Equal("b", Bencode.GetString(dict, "a"));
        Assert.Equal(5L, Bencode.GetInt(dict, "n"));
    }

    [Fact]
    public void DecodeDictionaryRejectsOtherTopLevelTypes()
        => Assert.Throws<FormatException>(() => Bencode.DecodeDictionary("li1ee"u8));

    [Theory]
    [InlineData("")]
    [InlineData("i42")]
    [InlineData("5:ab")]
    [InlineData("x")]
    [InlineData("l")]
    [InlineData("d1:a")]
    [InlineData("di1ei2ee")]
    public void RejectsMalformedInput(string text)
        => Assert.Throws<FormatException>(() => Bencode.Decode(Encoding.ASCII.GetBytes(text)));
}
