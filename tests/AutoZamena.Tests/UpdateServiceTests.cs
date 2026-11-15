using AutoZamena.App.Updates;
using Xunit;

namespace AutoZamena.Tests;

public sealed class UpdateServiceTests
{
    [Theory]
    [InlineData("v1.1.2", "1.1.1", true)]
    [InlineData("v1.1.1", "1.1.1", false)]
    [InlineData("v1.1.0", "1.1.1", false)]
    [InlineData("v1.2.0", "1.1.9", true)]
    [InlineData("v1.2.0-beta", "1.1.1", false)]
    [InlineData("../../evil", "1.1.1", false)]
    [InlineData("v1.2.0.1", "1.1.1", false)]
    public void OnlyNewStableNumericVersionsAreAccepted(string tag, string current, bool newer)
    {
        Assert.Equal(newer, UpdateService.TryParseNewVersion(tag, Version.Parse(current), out _));
    }

    [Fact]
    public void ChecksumsMatchExactFilenameAndRequireHexSha256()
    {
        string hash = new string('a', 64);
        string sums = $"{hash}  AutoZamena-Setup-win-x64.exe.bak\n{hash}  AutoZamena-Standalone-win-x64.exe\r\n";
        Assert.Null(UpdateService.FindExpectedHash(sums, "AutoZamena-Setup-win-x64.exe"));
        Assert.Equal(hash, UpdateService.FindExpectedHash(sums, "AutoZamena-Standalone-win-x64.exe"));
        Assert.Null(UpdateService.FindExpectedHash(new string('g', 64) + "  AutoZamena-Standalone-win-x64.exe", "AutoZamena-Standalone-win-x64.exe"));
    }
}
