namespace TunerFish.Models;

// Minimal shapes of the Jellyfin API responses we use.
// Jellyfin sends PascalCase JSON; deserialization is case-insensitive.

public sealed class AuthResult
{
    public string AccessToken { get; set; } = "";
    public UserDto User { get; set; } = new();
    public string? ServerId { get; set; }
}

/// <summary>/System/Info/Public: answers without signing in. Id identifies the server whatever address it's reached by.</summary>
public sealed class PublicSystemInfo
{
    public string Id { get; set; } = "";
    public string ServerName { get; set; } = "";
}

public sealed class UserDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}

public sealed class ItemsResult
{
    public List<BaseItem> Items { get; set; } = new();
    public int TotalRecordCount { get; set; }
}

public sealed class BaseItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? SortName { get; set; }
    public int? ProductionYear { get; set; }
    public DateTime? PremiereDate { get; set; }
    public int? IndexNumber { get; set; }        // track number
    public int? ParentIndexNumber { get; set; }  // disc number
    public long? RunTimeTicks { get; set; }      // 1 tick = 100 ns
    public List<string>? Artists { get; set; }
    public string? AlbumArtist { get; set; }
    public string? Album { get; set; }
    public string? AlbumId { get; set; }
    public List<NameIdPair>? AlbumArtists { get; set; }
    public List<string>? Genres { get; set; }
    public string? Path { get; set; }
    public Dictionary<string, string>? ImageTags { get; set; }
    public List<MediaSource>? MediaSources { get; set; }
    public int? ChildCount { get; set; }          // an album's tracks, with fields=ChildCount
    public UserItemData? UserData { get; set; }   // the signed-in user's own data, with enableUserData=true
}

public sealed class NameIdPair
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}

/// <summary>The user's own data on an item: the heart, the thumbs (Likes: true, false or neither) and plays.</summary>
public sealed class UserItemData
{
    public bool IsFavorite { get; set; }
    public bool? Likes { get; set; }
    public int PlayCount { get; set; }
    public DateTime? LastPlayedDate { get; set; }
}

public sealed class MediaSource
{
    public string? Container { get; set; }
    public int? Bitrate { get; set; }
    public long? Size { get; set; }
    public List<MediaStream>? MediaStreams { get; set; }
}

public sealed class MediaStream
{
    public string? Type { get; set; }
    public string? Codec { get; set; }
    public int? SampleRate { get; set; }
    public int? BitDepth { get; set; }
    public int? Channels { get; set; }
}
