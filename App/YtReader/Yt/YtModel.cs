﻿using System;
using System.Collections.Generic;
using System.Linq;
using SysExtensions;
using SysExtensions.Collections;
using SysExtensions.Text;

// all of this only minor midifications to https://github.com/Tyrrrz/YoutubeExplode

namespace YtReader.Yt {
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

    /// <summary>Description of this video.</summary>
    public string Description { get; init; }

    /// <summary>Search keywords of this video.</summary>
    public IReadOnlyList<string> Keywords { get; init; }

    public string ChannelId    { get; init; }
    public string ChannelTitle { get; init; }
    public string Error        { get; set; }
    public string SubError     { get; set; }
  }

  /// <summary>User activity statistics.</summary>
  public record Statistics(ulong? ViewCount, ulong? LikeCount = null, ulong? DislikeCount = null) {
    public ulong? Rumbles { get; init; }
    public override string ToString() => $"{ViewCount} Views";
  }

  /// <summary>Text that gets displayed at specific time during video playback, as part of a
  ///   <see cref="ClosedCaptionTrack" />.</summary>
  public record ClosedCaption(string Text, TimeSpan? Offset, TimeSpan? Duration, string Speaker = null) {
    public override string ToString() =>
      $"{new[] {Offset.Do(o => o.HumanizeShort()), Speaker}.NotNull().ToArray().Do(d => d.Any() ? $"{d.Join(" ")}: " : "")}{Text}";
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