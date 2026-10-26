using TunerFish.Models;
using TunerFish.Services;
using TunerFish.Tests.Support;
using TunerFish.ViewModels;

namespace TunerFish.Tests;

/// <summary>How artists and albums are filed: by Jellyfin's sort names, never by display name.</summary>
[Collection(AudioEngineCollection.Name)]
public sealed class MainViewModelFilingTests : IDisposable
{
    private readonly VmHarness _h;

    public MainViewModelFilingTests(FakeJellyfinServer server) => _h = new VmHarness(server);

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task Artists_AreFiledByJellyfinSortName()
    {
        var vm = await _h.SignedInAsync();

        // "The Beatles" files as "beatles" and "The The" as "the", as Jellyfin sends them.
        // Symbols come first, then digits, then letters; Björk files as "bjork".
        Assert.Equal("+++ (Crosses) | 10cc | ABBA | The Beatles | Björk | Elvis Costello | Now That's What I Call Music 5 | The The",
            VmHarness.ArtistNames(vm));
        Assert.Equal("beatles", vm.Artists[3].SortKey);
    }

    [Fact]
    public async Task AlbumTitles_AreFiledByJellyfinSortName_FallingBackToTheTitle()
    {
        var vm = await _h.SignedInAsync();
        await VmHarness.SelectArtistAsync(vm, "ar4", 4);

        vm.AlbumSortIndex = 0;

        Assert.Equal("A Case for Song | The Juliet Letters | My Aim Is True | This Year's Model", VmHarness.AlbumTitles(vm));
        Assert.Equal("My Aim Is True", vm.Albums[2].SortTitle); // no sort name from the server
    }

    [Fact]
    public async Task YearSort_IsUnaffectedBySortNames()
    {
        var vm = await _h.SignedInAsync();
        await VmHarness.SelectArtistAsync(vm, "ar4", 4);

        vm.AlbumSortIndex = 1;

        Assert.Equal("My Aim Is True | This Year's Model | A Case for Song | The Juliet Letters", VmHarness.AlbumTitles(vm));
    }

    [Theory]
    [InlineData("Björk", "Bjork")]
    [InlineData("Éléonore Señor", "Eleonore Senor")]
    [InlineData("Straße", "Strasse")]
    [InlineData("Æon Œuvre", "AEon OEuvre")]
    [InlineData("Ørjan Łódź Đorđe Þór", "Orjan Lodz Dorde THor")]
    [InlineData("АББА", "АББА")] // non-Latin is left alone
    public void PlainLetters_DropAccents_AndSpellOutLettersThatDontDecompose(string text, string expected)
    {
        Assert.Equal(expected, SortOrder.PlainLetters(text));
    }

    [Theory]
    [InlineData("beatles", "B")]
    [InlineData("élan", "E")]
    [InlineData("øystein", "O")]
    [InlineData("10cc", "#")]
    [InlineData("+++ (crosses)", "#")]
    [InlineData("...and you will know us", "#")]
    [InlineData("абба", "#")]
    [InlineData("", "#")]
    public void GroupOf_IsTheFirstPlainLetter_OrHashForEverythingElse(string key, string expected)
    {
        Assert.Equal(expected, SortOrder.GroupOf(key));
    }

    [Theory]
    [InlineData("+++", "10cc")]     // symbols before digits
    [InlineData("1999", "abba")]    // digits before letters
    [InlineData("абба", "1999")]    // non-Latin counts as a symbol
    [InlineData("ab cd", "ab1")]    // position by position, so a space comes first
    [InlineData("ab1", "abc")]
    [InlineData("abba", "Abba Gold")] // case is ignored, and a shorter key comes first
    [InlineData("eagles", "Élan")]  // É counts as E
    [InlineData("strasse", "straße x")]
    public void Comparer_PutsSymbolsThenDigitsThenLetters(string first, string second)
    {
        Assert.True(SortOrder.Comparer.Compare(first, second) < 0, $"{first} should sort before {second}");
        Assert.True(SortOrder.Comparer.Compare(second, first) > 0, $"{second} should sort after {first}");
    }

    [Fact]
    public void Comparer_TreatsAccentsAndCaseAsTheSameLetter()
    {
        Assert.Equal(0, SortOrder.Comparer.Compare("Björk", "bjork"));
    }

    [Fact]
    public void JumpBar_HasAll27Groups_InBarOrder()
    {
        Assert.Equal("# A B C D E F G H I J K L M N O P Q R S T U V W X Y Z",
            string.Join(" ", _h.Create().JumpGroups.Select(g => g.Group)));
    }

    [Fact]
    public async Task JumpBar_GreysOutEmptyGroups_AndFollowsTheFilter()
    {
        var vm = await _h.SignedInAsync();
        Assert.Equal("# A B E N T", PresentGroups(vm));

        vm.ArtistFilter = "the";
        Assert.Equal("B T", PresentGroups(vm));

        vm.ArtistFilter = "";
        Assert.Equal("# A B E N T", PresentGroups(vm));
    }

    [Fact]
    public async Task JumpTo_AsksForTheGroupsFirstArtist_WithoutSelectingIt()
    {
        var vm = await _h.SignedInAsync();
        var asked = new List<string>();
        vm.ArtistJumpRequested += a => asked.Add(a.Name);
        var selected = vm.SelectedArtist;

        vm.JumpGroups.Single(g => g.Group == "B").JumpCommand.Execute(null);
        vm.JumpTo("#");
        vm.JumpTo("Z"); // no artists: nothing happens

        Assert.Equal("The Beatles | +++ (Crosses)", string.Join(" | ", asked));
        Assert.Same(selected, vm.SelectedArtist);
    }

    [Fact]
    public async Task FirstArtistInGroup_FollowsTheFilter()
    {
        var vm = await _h.SignedInAsync();

        vm.ArtistFilter = "bj";

        Assert.Equal("Björk", vm.FirstArtistInGroup("B")?.Name);
        Assert.Null(vm.FirstArtistInGroup("A"));
    }

    private static string PresentGroups(MainViewModel vm) =>
        string.Join(" ", vm.JumpGroups.Where(g => g.HasArtists).Select(g => g.Group));

    [Theory]
    [InlineData("Elvis Costello", "costello, elvis", "costello, elvis")]
    [InlineData("Elvis Costello", "  costello  ", "costello")]
    [InlineData("Elvis Costello", null, "Elvis Costello")]
    [InlineData("Elvis Costello", "   ", "Elvis Costello")]
    public void SortKey_IsTheSortName_OrTheNameWhenThereIsNone(string name, string? sortName, string expected)
    {
        Assert.Equal(expected, SortOrder.KeyFor(new BaseItem { Name = name, SortName = sortName }));
    }
}
