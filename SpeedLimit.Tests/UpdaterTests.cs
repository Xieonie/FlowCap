using SpeedLimit;

public class UpdaterTests
{
    static string Release(string tag, string url) => $$"""
        { "tag_name": "{{tag}}", "body": "Neu: Pingtest", "draft": false, "prerelease": false,
          "assets": [
            { "name": "README.txt", "browser_download_url": "https://github.com/Xieonie/FlowCap/releases/download/{{tag}}/README.txt" },
            { "name": "FlowCap-Setup-1.2.0.exe", "browser_download_url": "{{url}}", "digest": "sha256:ABC123" } ] }
        """;

    const string Good = "https://github.com/Xieonie/FlowCap/releases/download/v1.2.0/FlowCap-Setup-1.2.0.exe";

    [Fact]
    public void NewerRelease_IsOffered()
    {
        var u = Updater.Parse(Release("v1.2.0", Good), new Version(1, 1, 0, 0));
        Assert.NotNull(u);
        Assert.Equal(new Version(1, 2, 0), u.Version);
        Assert.Equal(Good, u.Installer.ToString());
        Assert.Equal("ABC123", u.Sha256);
        Assert.Equal("Neu: Pingtest", u.Notes);
    }

    [Theory]
    [InlineData("v1.1.0")] // gleiche Version (Assembly hat 4 Stellen, Tag 3)
    [InlineData("v1.0.0")] // älter
    [InlineData("kaputt")]
    public void SameOlderOrBrokenTag_IsIgnored(string tag) =>
        Assert.Null(Updater.Parse(Release(tag, Good), new Version(1, 1, 0, 0)));

    [Fact]
    public void Prerelease_IsIgnored() =>
        Assert.Null(Updater.Parse(Release("v1.2.0", Good).Replace("\"prerelease\": false", "\"prerelease\": true"), new Version(1, 1, 0)));

    [Theory]
    [InlineData("http://github.com/Xieonie/FlowCap/releases/download/v1.2.0/FlowCap-Setup-1.2.0.exe")]
    [InlineData("https://evil.example/Xieonie/FlowCap/releases/download/v1.2.0/FlowCap-Setup-1.2.0.exe")]
    [InlineData("https://github.com/someone/FlowCap/releases/download/v1.2.0/FlowCap-Setup-1.2.0.exe")]
    public void InstallerFromElsewhere_IsRejected(string url) =>
        Assert.Null(Updater.Parse(Release("v1.2.0", url), new Version(1, 1, 0)));
}
