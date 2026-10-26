using TunerFish.Models;

namespace TunerFish.Services;

/// <summary>
/// The user's own marks on an artist or album, kept in Jellyfin's user data (never the library):
/// the heart is a perennial favourite; thumbs up is a like, what you're into now; thumbs down
/// hides it from browsing. Thumbs up and down are one field (Likes), so a liked thing can't be
/// hidden and hiding clears the like. Tracks are never marked.
/// </summary>
public readonly record struct Marks(bool Favourite = false, bool? Likes = null)
{
    public static readonly Marks None = new();

    public bool Liked => Likes == true;
    public bool Hidden => Likes == false;

    public Marks WithFavourite(bool on) => this with { Favourite = on };
    public Marks WithLiked(bool on) => this with { Likes = on ? true : Liked ? null : Likes };
    public Marks WithHidden(bool on) => this with { Likes = on ? false : Hidden ? null : Likes };

    public static Marks From(BaseItem item) =>
        item.UserData is { } d ? new Marks(d.IsFavorite, d.Likes) : None;
}

/// <summary>An artist or album, which can carry the user's marks.</summary>
public interface IMarked
{
    Marks Marks { get; }
}
