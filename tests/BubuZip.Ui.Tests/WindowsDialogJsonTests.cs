using System.Text.Json;
using BubuZip.Desktop;
using Xunit;

namespace BubuZip.Ui.Tests;

public sealed class WindowsDialogJsonTests
{
    [Fact]
    public void GeneratedPickerMetadataPreservesUnicodeSpacesBackslashesAndLiteralShellCharacters()
    {
        const string json = """
            ["C:\\Users\\测试 文稿\\压缩 包.zip","\\\\server\\共享 文件夹\\a & $(literal).7z"]
            """;

        var result = JsonSerializer.Deserialize(json, WindowsDialogJsonContext.Default.StringArray);

        Assert.Equal(new[] { @"C:\Users\测试 文稿\压缩 包.zip", @"\\server\共享 文件夹\a & $(literal).7z" }, result);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    public void EmptyPickerResponsesRetainTheEmptySelectionContract(string json)
    {
        var result = JsonSerializer.Deserialize(json, WindowsDialogJsonContext.Default.StringArray) ?? [];

        Assert.Empty(result);
    }

    [Theory]
    [InlineData("{\"path\":\"archive.zip\"}")]
    [InlineData("[123]")]
    public void UnexpectedPickerResponseShapesAreRejected(string json)
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(json, WindowsDialogJsonContext.Default.StringArray));
    }
}
