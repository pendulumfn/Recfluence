﻿using System;
using System.Collections.Generic;
using System.Linq;
using Amazon.TranscribeService;
using Amazon.TranscribeService.Model;
using Humanizer;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using SysExtensions;
using SysExtensions.Collections;
using SysExtensions.Serialization;
using SysExtensions.Text;
using YtReader.Store;
using YtReader.Yt;

// ReSharper disable InconsistentNaming

namespace YtReader.Transcribe {
  public record TransRoot {
    public string       jobName   { get; init; }
    public string       accountId { get; init; }
    public TransResults results   { get; init; }

    public static JsonSerializerSettings JsonSettings() {
      var settings = JsonExtensions.DefaultSettings();
      settings.ContractResolver = new DefaultContractResolver();
      return settings;
    }
  }

  public record TransResults {
    public string             language_code  { get; set; }
    public TransText[]        transcripts    { get; init; }
    public TransSpeakerLabels speaker_labels { get; init; }
    public TransItem[]        items          { get; set; }
  }

  public record TransText {
    public string transcript { get; init; }
  }

  public record TransSpeakerLabels {
    public int                speakers { get; init; }
    public TransSegmentRoot[] segments { get; init; }
  }

  public record TransSegmentRoot : TransSegment {
    public TransSegment[] items { get; init; }
  }

  public record TransSegment : RTransPeriod {
    public string speaker_label { get; init; }
  }

  public record RTransPeriod {
    public string start_time { get; init; }
    public string end_time   { get; init; }
  }

  public record TransItem : RTransPeriod {
    public TransAlt[] alternatives { get; set; }
    public string     type         { get; set; }
  }

  public record TransAlt {
    public string confidence { get; set; }
    public string content    { get; set; }
  }

  public static class AwsTranscribe {
    public static async IAsyncEnumerable<TranscriptionJobSummary[]> Jobs(this AmazonTranscribeServiceClient client) {
      string next = null;
      do {
        var res = await client.ListTranscriptionJobsAsync(new() {NextToken = next, MaxResults = 100});
        yield return res.TranscriptionJobSummaries.ToArray();
        next = res.NextToken;
      } while (next != null);
    }

    public static (TimeSpan Start, TimeSpan End) Period(this RTransPeriod p) => (ParseTs(p.start_time), ParseTs(p.end_time));

    static TimeSpan ParseTs(string p) => p.TryParseDouble().Do(TimeSpan.FromSeconds);

    static bool IsPunctuation(this TransItem item) => item.type == "punctuation";
    static string Content(this TransItem item) => item.alternatives.FirstOrDefault()?.content;

    /// <summary>iterate backwards from an index</summary>
    static IEnumerable<T> PreviousItems<T>(IList<T> items, int i) {
      while (i > 0 && items.Count >= i) {
        i--;
        yield return items[i];
      }
    }

    /// <summary>Converts the AWS caption into our format. Splits transcript at sentence and speaker boundaries, or 1min</summary>
    public static VideoCaption AwsToVideoCaption(this TransRoot res, string videoId, string channelId, Platform platform) {
      var speakerByStart = res.results.speaker_labels?.segments
        .SelectMany(s => s.items.Select(i => (s.speaker_label, i.start_time)))
        .ToMultiValueDictionary(s => s.start_time, s => s.speaker_label) ?? new();
      (int i, TransItem startItem) group = (0, default);

      var captions = res.results.items.Select((item, i) => {
        var (groupStart, _) = group.startItem?.Period() ?? default;
        var speaker = item.start_time.Do(s => speakerByStart.TryGet(s)?.FirstOrDefault());
        var prevWord = PreviousItems(res.results.items, i).FirstOrDefault(c => !c.IsPunctuation());
        var prev = PreviousItems(res.results.items, i).FirstOrDefault();
        var lastSpeaker = prevWord?.start_time.Do(s => speakerByStart.TryGet(s)?.FirstOrDefault());
        var span = item.Period().Start - groupStart;
        if (prev?.Content() == "." && span > 15.Seconds()
          || speaker.HasValue() && lastSpeaker.HasValue() && speaker != lastSpeaker
          || groupStart != default && span > 1.Minutes())
          group = (group.i + 1, item);
        return (group, item, speaker);
      }).GroupBy(c => c.group).Select(g => {
        var speaker = g.Select(c => c.speaker).NotNull().FirstOrDefault();
        var startTime = g.Select(c => c.item.Period().Start).NotNull().FirstOrDefault();
        var endTime = g.Select(c => c.item.Period().End).NotNull().LastOrDefault();
        return new ClosedCaption(g.Join("", c => (c.item.IsPunctuation() ? "" : " ") + c.item.alternatives.First().content).Trim(),
          startTime, endTime - startTime, speaker);
      }).ToArray();

      var caption = new VideoCaption {
        Updated = DateTime.UtcNow,
        VideoId = videoId,
        ChannelId = channelId,
        Info = new(url: null, new(res.results?.language_code, name: null), isAutoGenerated: true),
        Captions = captions,
        Platform = platform
      };
      return caption;
    }
  }
}