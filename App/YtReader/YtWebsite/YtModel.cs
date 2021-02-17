﻿using System;
using System.Collections.Generic;

// all of this only minor midifications to https://github.com/Tyrrrz/YoutubeExplode

namespace YtReader.YtWebsite {
  public record YtVideoItem {
    /// <summary>ID of this video.</summary>
    public string Id { get; init; }

    /// <summary>Upload date of this video.</summary>
    public DateTime? UploadDate { get; init; }

    /// <summary>Title of this video.</summary>
    public string Title { get; init; }

    /// <summary>Duration of this video.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>Statistics of this video.</summary>
    public Statistics Statistics { get; init; }
  }

  public record YtVideo : YtVideoItem {
    /// <summary>Author of this video.</summary>
    public string Author { get; init; }

    /// <summary>This is the same as upload date. But sometimes there are discrepancies and added is more reliable</summary>
    public DateTime? AddedDate { get; init; }

    /// <summary>Description of this video.</summary>
    public string Description { get; init; }

    /// <summary>Search keywords of this video.</summary>
    public IReadOnlyList<string> Keywords { get; init; }

    public string ChannelId    { get; init; }
    public string ChannelTitle { get; init; }
  }

  /// <summary>User activity statistics.</summary>
  public class Statistics {
    /// <summary>View count.</summary>
    public ulong? ViewCount { get; set; }

    /// <summary>Like count.</summary>
    public ulong? LikeCount { get; set; }

    /// <summary>Dislike count.</summary>
    public ulong? DislikeCount { get; set; }

    /// <summary>Initializes an instance of <see cref="Statistics" />.</summary>
    public Statistics(ulong? viewCount, ulong? likeCount = null, ulong? dislikeCount = null) {
      ViewCount = viewCount;
      LikeCount = likeCount;
      DislikeCount = dislikeCount;
    }

    public override string ToString() => $"{ViewCount} Views";
  }

  /// <summary>Text that gets displayed at specific time during video playback, as part of a
  ///   <see cref="ClosedCaptionTrack" />.</summary>
  public class ClosedCaption {
    /// <summary>Text displayed by this caption.</summary>
    public string Text { get; }

    /// <summary>Time at which this caption starts being displayed.</summary>
    public TimeSpan? Offset { get; }

    /// <summary>Duration this caption is displayed.</summary>
    public TimeSpan? Duration { get; }

    /// <summary>Initializes an instance of <see cref="ClosedCaption" />.</summary>
    public ClosedCaption(string text, TimeSpan? offset, TimeSpan? duration) {
      Text = text;
      Offset = offset;
      Duration = duration;
    }

    public override string ToString() => Text;
  }

  /// <summary>Set of captions that get displayed during video playback.</summary>
  public class ClosedCaptionTrack {
    /// <summary>Metadata associated with this track.</summary>
    public ClosedCaptionTrackInfo Info { get; }

    /// <summary>Collection of closed captions that belong to this track.</summary>
    public IReadOnlyList<ClosedCaption> Captions { get; }

    /// <summary>Initializes an instance of <see cref="ClosedCaptionTrack" />.</summary>
    public ClosedCaptionTrack(ClosedCaptionTrackInfo info, IReadOnlyList<ClosedCaption> captions) {
      Info = info;
      Captions = captions;
    }
  }

  /// <summary>Metadata associated with a certain <see cref="ClosedCaptionTrack" />.</summary>
  public class ClosedCaptionTrackInfo {
    /// <summary>Manifest URL of the associated track.</summary>
    public string Url { get; }

    /// <summary>Language of the associated track.</summary>
    public Language Language { get; }

    /// <summary>Whether the associated track was automatically generated.</summary>
    public bool IsAutoGenerated { get; }

    /// <summary>Initializes an instance of <see cref="ClosedCaptionTrackInfo" />.</summary>
    public ClosedCaptionTrackInfo(string url, Language language, bool isAutoGenerated) {
      Url = url;
      Language = language;
      IsAutoGenerated = isAutoGenerated;
    }

    /// <inheritdoc />
    public override string ToString() => $"{Language}";
  }

  /// <summary>Language information.</summary>
  public class Language {
    /// <summary>ISO 639-1 code of this language.</summary>
    public string Code { get; }

    /// <summary>Full English name of this language.</summary>
    public string Name { get; }

    /// <summary>Initializes an instance of <see cref="Language" />.</summary>
    public Language(string code, string name) {
      Code = code;
      Name = name;
    }

    /// <inheritdoc />
    public override string ToString() => $"{Code} ({Name})";
  }
}