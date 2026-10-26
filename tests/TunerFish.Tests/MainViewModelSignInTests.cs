using TunerFish.Services;
using TunerFish.Tests.Support;

namespace TunerFish.Tests;

[Collection(AudioEngineCollection.Name)]
public sealed class MainViewModelSignInTests : IDisposable
{
    private readonly FakeJellyfinServer _server;
    private readonly VmHarness _h;

    public MainViewModelSignInTests(FakeJellyfinServer server)
    {
        _server = server;
        _h = new VmHarness(server);
    }

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task FirstRun_ShowsLogin()
    {
        var vm = _h.Create();
        await vm.StartAsync();

        Assert.False(vm.IsLoggedIn);
    }

    [Fact]
    public async Task WrongPassword_ShowsMessageAndStaysLoggedOut()
    {
        var vm = _h.Create();
        await vm.StartAsync();
        vm.ServerUrl = $"127.0.0.1:{_server.Port}/";
        vm.UserName = FakeJellyfinServer.UserName;
        vm.Password = "nope";

        vm.LoginCommand.Execute(null);

        await Wait.UntilAsync(() => vm.Status.Contains("Wrong"), "the wrong-password message", state: () => VmHarness.Describe(vm));
        Assert.False(vm.IsLoggedIn);
    }

    [Fact]
    public async Task Login_LoadsArtists_NormalisesUrl_AndNeverStoresPassword()
    {
        var vm = _h.Create();
        await vm.StartAsync();
        vm.ServerUrl = $"127.0.0.1:{_server.Port}/";
        vm.UserName = FakeJellyfinServer.UserName;
        vm.Password = FakeJellyfinServer.Password;

        vm.LoginCommand.Execute(null);

        await Wait.UntilAsync(() => vm.IsLoggedIn && vm.Artists.Count == FakeJellyfinServer.ArtistCount, "login to load the album artists", state: () => VmHarness.Describe(vm));
        Assert.Equal(_server.Url, vm.ServerUrl);
        Assert.Equal("", vm.Password);
        Assert.DoesNotContain($"\"{FakeJellyfinServer.Password}\"", _h.SettingsText);
    }

    [Fact]
    public async Task Restart_SignsInWithSavedToken_AndRestoresLastArtist()
    {
        var first = await _h.SignedInAsync();
        await VmHarness.SelectArtistAsync(first, "ar2", 1);
        first.SaveSettings();
        first.Dispose();

        var vm = _h.Create();
        await vm.StartAsync();

        Assert.True(vm.IsLoggedIn);
        Assert.Equal(FakeJellyfinServer.ArtistCount, vm.Artists.Count);
        Assert.Equal("ar2", vm.SelectedArtist?.Id);
        await Wait.UntilAsync(() => vm.Albums.Count == 1, "the restored artist's albums", state: () => VmHarness.Describe(vm));
    }

    [Fact]
    public async Task SignOut_ClearsLists_AndRemovesTokenFromDisk()
    {
        var vm = await _h.SignedInAsync();
        Assert.Contains(FakeJellyfinServer.Token, _h.SettingsText);

        vm.SignOutCommand.Execute(null);

        Assert.False(vm.IsLoggedIn);
        Assert.Empty(vm.Artists);
        Assert.DoesNotContain(FakeJellyfinServer.Token, _h.SettingsText);
    }

    [Fact]
    public async Task TheLog_SaysWhoSignedInWhere_AndSigningOut_ButNeverTheTokenOrPassword()
    {
        var vm = await _h.SignedInAsync();
        vm.SignOutCommand.Execute(null);

        Assert.Contains($"Server: Signed in as {FakeJellyfinServer.UserName} at {_server.Url}", _h.Logged);
        Assert.Equal("Server: Signed out", _h.Logged.Last());
        Assert.DoesNotContain(_h.Logged, l => l.Contains(FakeJellyfinServer.Token) || l.Contains(FakeJellyfinServer.Password));
    }

    [Fact]
    public async Task ExpiredToken_ReturnsToLoginWithMessage()
    {
        var store = new SettingsStore(_h.SettingsPath);
        var s = store.Load();
        s.ServerUrl = _server.Url;
        s.AccessToken = "stale";
        s.UserId = FakeJellyfinServer.UserId;
        store.Save(s);

        var vm = _h.Create();
        await vm.StartAsync();

        Assert.False(vm.IsLoggedIn);
        Assert.Contains("expired", vm.Status);
    }

    // ---- One server, several addresses ----

    private void SaveSession(IEnumerable<string> addresses, string? serverId = FakeJellyfinServer.ServerId)
    {
        var store = new SettingsStore(_h.SettingsPath);
        var s = store.Load();
        s.ServerUrls = addresses.ToList();
        s.ServerUrl = s.ServerUrls.FirstOrDefault();
        s.ServerId = serverId;
        s.AccessToken = FakeJellyfinServer.Token;
        s.UserId = FakeJellyfinServer.UserId;
        store.Save(s);
    }

    [Fact]
    public async Task Restart_WithTheHomeAddressDown_SignsInThroughTheNextOne()
    {
        SaveSession([FakeJellyfinServer.DeadUrl(), _server.Url]);

        var vm = _h.Create();
        await vm.StartAsync();

        Assert.True(vm.IsLoggedIn);
        Assert.Equal(FakeJellyfinServer.ArtistCount, vm.Artists.Count);
        Assert.Equal(2, vm.ServerAddresses.Count);
        Assert.Contains($"Server: Using {_server.Url}", _h.Logged);
    }

    [Fact]
    public async Task OldSettings_WithOneServerUrlAndNoServerId_StillSignIn_AndLearnTheId()
    {
        var store = new SettingsStore(_h.SettingsPath);
        var s = store.Load();
        s.ServerUrl = _server.Url; // as written before there could be several addresses
        s.AccessToken = FakeJellyfinServer.Token;
        s.UserId = FakeJellyfinServer.UserId;
        store.Save(s);

        var vm = _h.Create();
        await vm.StartAsync();

        Assert.True(vm.IsLoggedIn);
        Assert.Equal(_server.Url, vm.ServerAddresses.Single().Address);
        Assert.Equal(FakeJellyfinServer.ServerId, new SettingsStore(_h.SettingsPath).Load().ServerId);
    }

    [Fact]
    public async Task SigningInAgain_ToTheSameServer_KeepsItsOtherAddresses()
    {
        using var away = FakeJellyfinServer.WithId(FakeJellyfinServer.ServerId);
        var first = await _h.SignedInAsync();
        first.NewServerAddress = away.Url;
        first.AddServerAddress();
        first.SignOutCommand.Execute(null);
        first.Dispose();

        var vm = await _h.SignedInAsync(); // through the login form again

        Assert.Equal(new[] { _server.Url, away.Url }, vm.ServerAddresses.Select(a => a.Address));
    }

    [Fact]
    public async Task ServerAddresses_CanBeAddedMovedAndRemoved_AreSaved_AndShowWhetherTheyAnswer()
    {
        var vm = await _h.SignedInAsync();
        var dead = FakeJellyfinServer.DeadUrl();

        vm.NewServerAddress = dead.Replace("http://", "") + "/";
        vm.AddServerAddress();
        Assert.Equal(new[] { _server.Url, dead }, vm.ServerAddresses.Select(a => a.Address));
        Assert.Equal("", vm.NewServerAddress);

        await vm.CheckServerAddressesAsync();
        Assert.Equal("Reachable · in use", vm.ServerAddresses[0].StatusText);
        Assert.Equal("Not reachable", vm.ServerAddresses[1].StatusText);

        vm.MoveServerAddressUp(dead);
        Assert.Equal(new[] { dead, _server.Url }, vm.ServerAddresses.Select(a => a.Address));
        var saved = new SettingsStore(_h.SettingsPath).Load();
        Assert.Equal(new[] { dead, _server.Url }, saved.ServerUrls);
        Assert.Equal(dead, saved.ServerUrl); // kept equal to the first, for older settings readers

        vm.RemoveServerAddress(dead);
        vm.RemoveServerAddress(_server.Url); // the last one can't go
        Assert.Equal(_server.Url, vm.ServerAddresses.Single().Address);
    }
}
