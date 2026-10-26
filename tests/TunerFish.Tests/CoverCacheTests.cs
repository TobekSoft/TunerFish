using TunerFish.Services;
using TunerFish.Tests.Support;

namespace TunerFish.Tests;

public sealed class CoverCacheTests : IClassFixture<FakeJellyfinServer>, IDisposable
{
    private readonly FakeJellyfinServer _server;
    private readonly TempDir _dir = new();
    private readonly JellyfinClient _client = new("test-device");

    public CoverCacheTests(FakeJellyfinServer server)
    {
        _server = server;
        _client.Configure([server.Url], FakeJellyfinServer.Token, FakeJellyfinServer.UserId, FakeJellyfinServer.ServerId);
    }

    public void Dispose() => _dir.Dispose();

    private CoverCache NewCache() => new(_client, _dir.File("covers"));

    [Fact]
    public async Task DownloadsCover_IntoItsDirectory()
    {
        var file = await NewCache().GetFileAsync("al2", "tagA", 300);

        Assert.NotNull(file);
        Assert.StartsWith(_dir.File("covers"), file);
        Assert.Equal(TestMedia.CoverPng, File.ReadAllBytes(file));
    }

    [Fact]
    public async Task SecondCacheInstance_ReusesFileOnDisk()
    {
        var first = await NewCache().GetFileAsync("al2", "tagA", 300);
        var before = _server.RequestCount("/Items/al2/Images/Primary");

        var second = await NewCache().GetFileAsync("al2", "tagA", 300);

        Assert.Equal(first, second);
        Assert.Equal(before, _server.RequestCount("/Items/al2/Images/Primary"));
    }

    [Fact]
    public async Task NewTag_GivesNewFile()
    {
        var cache = NewCache();

        var a = await cache.GetFileAsync("al2", "tagA", 300);
        var b = await cache.GetFileAsync("al2", "tagB", 300);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public async Task NoTag_OrNoImageOnServer_GivesNull()
    {
        var cache = NewCache();

        Assert.Null(await cache.GetFileAsync("al1", null, 300));
        Assert.Null(await cache.GetFileAsync("al1", "missing", 300));
        Assert.Empty(Directory.GetFiles(_dir.File("covers")));
    }
}
