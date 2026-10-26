using TunerFish.Services;

namespace TunerFish.Tests;

/// <summary>Mirrors the Android app's MarksTest case for case.</summary>
public sealed class MarksTests
{
    private sealed record Item(string Id, Marks Marks) : IMarked;

    [Fact]
    public void TheHeart_IsIndependentOfTheThumbs()
    {
        var m = Marks.None.WithFavourite(true).WithLiked(true);
        Assert.True(m.Favourite && m.Liked);
        Assert.True(m.WithFavourite(false).Liked);
    }

    [Fact]
    public void LikedAndHidden_AreOneDial()
    {
        var liked = Marks.None.WithLiked(true);
        var hidden = liked.WithHidden(true);
        Assert.True(hidden.Hidden);
        Assert.False(hidden.Liked); // hiding clears the like
        Assert.True(hidden.WithLiked(true).Liked); // and a like brings it back out of hiding
        Assert.Equal(Marks.None, liked.WithLiked(false));
        Assert.Equal(Marks.None, hidden.WithHidden(false));
    }

    [Fact]
    public void TurningOffOneEnd_LeavesTheOtherAlone()
    {
        Assert.True(Marks.None.WithHidden(true).WithLiked(false).Hidden);
        Assert.True(Marks.None.WithLiked(true).WithHidden(false).Liked);
    }

    [Fact]
    public void Marks_ComeFromTheUsersData()
    {
        Assert.Equal(new Marks(true, false), Marks.From(new Models.BaseItem { UserData = new Models.UserItemData { IsFavorite = true, Likes = false } }));
        Assert.Equal(Marks.None, Marks.From(new Models.BaseItem()));
    }

    [Fact]
    public void HiddenThings_AreLeftOut_UnlessShown()
    {
        var all = new[] { new Item("a1", Marks.None), new Item("a2", new Marks(Likes: false)) };
        Assert.Equal(new[] { "a1" }, LibraryViews.Visible(all, showHidden: false).Select(x => x.Id));
        Assert.Equal(new[] { "a1", "a2" }, LibraryViews.Visible(all, showHidden: true).Select(x => x.Id));
    }
}
