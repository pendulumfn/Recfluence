﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Mutuo.Etl.Pipe;
using Newtonsoft.Json.Linq;
using Serilog;
using Serilog.Core;
using SysExtensions;
using SysExtensions.Collections;
using SysExtensions.Text;
using SysExtensions.Threading;
using YtReader.SimpleCollect;
using YtReader.Store;
using YtReader.Transcribe;
using static Mutuo.Etl.Pipe.PipeArg;
using static YtReader.Yt.CollectFromType;
using static YtReader.Yt.UpdateChannelType;
using static YtReader.Yt.CollectListPart;
using static YtReader.Yt.CollectPart;
using static YtReader.Yt.ExtraPart;

// ReSharper disable InconsistentNaming

namespace YtReader.Yt {
  public record CollectListOptions {
    public CollectListPart[]                    Parts         { get; init; }
    public ExtraPart[]                          ExtraParts    { get; init; }
    public (CollectFromType Type, string Value) CollectFrom   { get; init; }
    public string[]                             LimitChannels { get; init; }
    public TimeSpan                             StaleAgo      { get; init; }
    public JObject                              Args          { get; init; }
    public Platform[]                           Platforms     { get; init; }
    public int?                                 Limit         { get; init; }
  }

  public enum CollectFromType {
    VideoPath,
    VideoChannelView,
    ChannelPath,
    VideoChannelNamed,
    UserNamed
  }

  public enum CollectListPart {
    /// <summary>Channels explicitly listed</summary>
    [EnumMember(Value = "channel")] LChannel,
    /// <summary>Videos explicitly listed</summary>
    [EnumMember(Value = "video")] LVideo,

    /// <summary>users explicitly listed</summary>
    [EnumMember(Value = "user")] LUser,

    /// <summary>Channels found from refreshing videos</summary>
    [EnumMember(Value = "discovered-channel")] [CollectPart(Explicit = true)]
    LDiscoveredChannel,

    /// <summary>Process videos in the channels found from the list (via video's or channels) where the most recent video_extra
    ///   is UpdateBefore</summary>
    [EnumMember(Value = "channel-video")] [CollectPart(Explicit = true)]
    LChannelVideo
  }

  public record VideoListStats(string video_id, string source_id, string channel_id, DateTime? extra_updated, DateTime upload_date, DateTime updated,
    bool caption_exists, bool comment_exists, Platform platform, string media_url);

  public record CollectList(YtCollector YtCollector, SimpleCollector SimpleCollector, IPipeCtx PipeCtx, AppCfg Cfg, Transcriber Transcriber) {
    /// <summary>Collect extra & parts from an imported list of channels and or videos. Use instead of update to process
    ///   arbitrary lists and ad-hoc fixes</summary>
    public async Task Run(CollectListOptions opts, ILogger log, CancellationToken cancel = default) {
      var parts = opts.Parts;
      var extraParts = opts.ExtraParts;

      log.Information("YtCollect - Special Collect from {CollectFrom} started", opts.CollectFrom);

      var videosProcessed = Array.Empty<VideoProcessResult>();
      if (parts.ShouldRun(LVideo)) {
        // sometimes updates fail. When re-running this, we should refresh channels that are missing videos or have a portion of captions not attempted
        // NOTE: core warehouse table must be updated (not just staging tables) to take into account previously successful loads.
        var vidChanSelect = VideoChannelSelect(opts);
        IReadOnlyCollection<VideoListStats> videos;
        using (var db = await YtCollector.Db(log)) // videos sans extra update
          videos = await VideoStats(db, vidChanSelect, opts.LimitChannels, opts.Platforms, opts.Limit);
        videosProcessed = await videos
          .Pipe(PipeCtx, b => ProcessVideos(b, extraParts, Inject<ILogger>(), Inject<CancellationToken>()), log: log, cancel: cancel)
          .Then(r => r.Select(p => p.OutState).SelectMany().ToArray());
      }

      if (opts.CollectFrom.Type == UserNamed && parts.ShouldRun(LUser)) await ProcessUsers(log, opts, cancel);

      IReadOnlyCollection<(string ChannelId, Platform Platform)> channelIds = Array.Empty<(string, Platform)>();
      if (parts.ShouldRunAny(LChannelVideo, LChannel)) {
        // channels explicitly listed in the query
        using (var db = await YtCollector.Db(log))
          channelIds = await ChannelIds(db, VideoChannelSelect(opts), opts.LimitChannels, opts.Platforms, opts.Limit);

        // channels found from processing videos
        if (parts.ShouldRun(LDiscoveredChannel))
          channelIds = channelIds.Concat(videosProcessed.Select(e => (e.ChannelId, e.Platform))).NotNull().Distinct().ToArray();

        if (channelIds.Any()) {
          var channels = await YtCollector
            .PlanAndUpdateChannelStats(new[] {PChannel, PChannelVideos}, channelIds.Select(c => c.ChannelId).ToArray(), log, cancel)
            .Then(chans => chans
              .Where(c => c.LastVideoUpdate.OlderThanOrNull(opts.StaleAgo)) // filter to channels we haven't updated video's in recently
              .Select(c => c with {Update = c.Update == Discover ? Standard : c.Update})); // revert to standard update for channels detected as discover

          if (parts.ShouldRun(LChannelVideo))
            await channels.GroupBy(c => c.Channel.Platform).BlockDo(async g => {
              var platform = g.Key;
              var collector = Collector(g.Key);
              DateTime? fromDate = new DateTime(year: 2020, month: 1, day: 1);

              if (collector is YtCollector yt)
                await g.Randomize().Pipe(PipeCtx, b => yt.ProcessChannels(b, extraParts, Inject<ILogger>(), Inject<CancellationToken>(), fromDate),
                  log: log, cancel: cancel);
              else if (collector is SimpleCollector sc)
                await g.Randomize().Pipe(PipeCtx, b =>
                    sc.SimpleCollectChannels(g.ToArray(), platform, StandardToSimpleParts(parts, extraParts), Inject<ILogger>(), Inject<CancellationToken>()),
                  log: log, cancel: cancel);
            });
        }
      }
      log.Information("YtCollect - ProcessVideos complete - {Videos} videos and {Channels} channels processed",
        videosProcessed.Length, channelIds.Count);
    }

    static StandardCollectPart[] StandardToSimpleParts(CollectListPart[] parts, ExtraPart[] extraParts) =>
      (parts ?? Enum.GetValues<CollectListPart>().ToArray())
      .Select(p => p switch {
        LChannel => StandardCollectPart.Channel,
        LVideo => (StandardCollectPart?) null, // explicit videos are done above.. This is just for the channels & channel-videos
        LChannelVideo => StandardCollectPart.ChannelVideo,
        _ => null
      }).Concat(extraParts.ShouldRun(EExtra) ? StandardCollectPart.Extra : null)
      .NotNull().ToArray();

    ICollector Collector(Platform platform) => platform switch {
      Platform.YouTube => YtCollector,
      Platform.BitChute => SimpleCollector,
      Platform.Rumble => SimpleCollector,
      _ => throw new($"platform {platform} not supported")
    };

    [Pipe]
    public async Task<VideoProcessResult[]> ProcessVideos(IReadOnlyCollection<VideoListStats> videos, ExtraPart[] parts, ILogger log,
      CancellationToken cancel) {
      log ??= Logger.None;
      const int batchSize = 1000;
      var plans = new VideoExtraPlans();

      foreach (var v in videos)
        plans.SetForUpdate(new() {
          VideoId = v.video_id, ChannelId = v.channel_id, Platform = v.platform, SourceId = v.source_id,
          ExtraUpdated = v.extra_updated, Updated = v.updated, UploadDate = v.upload_date
        });

      if (parts.ShouldRun(EExtra))
        plans.SetPart(videos.Where(v => v.extra_updated.OlderThanOrNull(Cfg.Collect.RefreshExtraDebounce)).Select(v => v.video_id), EExtra);
      if (parts.ShouldRun(ECaption))
        plans.SetPart(videos.Where(v => v.channel_id != null && !v.caption_exists && v.video_id != null).Select(v => v.video_id), ECaption);
      if (parts.ShouldRun(EComment))
        plans.SetPart(videos.Where(v => !v.comment_exists).Select(v => v.video_id), EComment);

      // process planned extra parts (except transcribe)
      var extraPartsRes = await videos.GroupBy(v => v.platform).BlockMap(async g => {
        var platform = g.Key;
        var collector = Collector(platform);
        var extra = await plans
          .Where(p => p.Parts.Any(e => e.In(EExtra, EComment, ERec, ECaption)))
          .Batch(batchSize)
          .BlockMap(async (planBatch, batchNum) => {
            var (extras, _, comments, captions) = await collector.SaveExtraAndParts(platform, c: null, parts, log, new(planBatch));
            log.Information("ProcessVideos - saved extra {Videos} extras, {Captions} captions {Comments} comments. Progress {AllVideos}/{TotalVideos}"
              , extras.Length, captions.Length, comments.Length, batchNum * batchSize + extras.Length, plans.Count);
            var captionVideoIds = captions.NotNull().Select(c => c.VideoId).ToHashSet();
            return extras.Select(e => (e, captionLoaded: captionVideoIds.Contains(e.VideoId), parts)).ToArray();
          }, Cfg.Collect.ParallelChannels, cancel: cancel)
          .SelectManyList();
        return extra.Select(e => new VideoProcessResult(e.e.VideoId, e.e.ChannelId, platform, e.captionLoaded, e.parts)).ToArray();
      }).SelectMany().ToArrayAsync();

      // process transcriptions. Supplemental to SaveExtraAndParts because this depends on if we can get captions from the source
      if (parts.ShouldRun(ETranscribe)) {
        var keyedRes = extraPartsRes.KeyBy(v => v.VideoId);
        var videosToTranscribe = videos.Where(v => v.media_url.HasValue() && !v.caption_exists)
          .Select(v => (v, res: keyedRes[v.video_id]))
          .Where(v => v.res?.captionLoaded != true).ToArray();
        /*foreach (var v in videosToTranscribe)
          transcribeRes.Add(v.res with {Parts = v.res.Parts.Concat(ETranscribe).ToArray()});*/
        log.Information("ProcessVideos - starting transcription of {Videos} videos", videosToTranscribe.Length);
        await Transcriber.TranscribeVideos(videosToTranscribe.Select(v => new VideoToTranscribe {
          video_id = v.v.video_id,
          channel_id = v.v.channel_id,
          platform = v.v.platform,
          media_url = v.v.media_url,
          source_id = v.v.source_id
        }).ToAsyncEnumerable(), log);
      }

      return extraPartsRes.ToArray();
    }

    async Task ProcessUsers(ILogger log, CollectListOptions opts, CancellationToken cancel = default) {
      var userSelect = UserSelect(opts);
      var sql = $@"with q as ({userSelect.Sql})
select user_id
from q
where not exists (select * from user u where u.user_id = q.user_id)
";
      IReadOnlyCollection<string> users;
      using (var db = await YtCollector.Db(log))
        users = await db.Db.Query<string>("CollectList - users", sql, userSelect.Args);

      var sw = Stopwatch.StartNew();
      var total = await users.Pipe(PipeCtx,
          b => YtCollector.CollectUserChannels(b, Inject<ILogger>(), Inject<CancellationToken>()), log: log, cancel: cancel)
        .Then(r => r.Sum(i => i.OutState));
      log.Information("Collect - completed scraping all user channels {Total} in {Duration}", total, sw.Elapsed.HumanizeShort());
    }

    /// <summary>returns a query with results in the schema video_id::string, channel_id::string</summary>
    static (string Sql, JObject Args) VideoChannelSelect(CollectListOptions opts) {
      var (type, value) = opts.CollectFrom;
      var select = type switch {
        ChannelPath => ($"select null video_id, $1::string channel_id from @public.yt_data/{value} (file_format => tsv)", null),
        VideoPath =>
          ($"select $1::string video_id, $2::string channel_id  from @public.yt_data/{value} (file_format => tsv)", null),
        VideoChannelView => ($"select video_id, channel_id from {value}", null),
        VideoChannelNamed => CollectListSql.NamedQuery(value, opts.Args),
        _ => throw new($"VideoChannelSelect - CollectFrom {opts.CollectFrom} not supported")
      };
      return select;
    }

    /// <summary>returns a query with results in the schema user_id::string</summary>
    static (string Sql, JObject Args) UserSelect(CollectListOptions opts) => opts.CollectFrom.Type switch {
      UserNamed => CollectListSql.NamedQuery(opts.CollectFrom.Value, opts.Args),
      _ => throw new($"UserSelect - CollectFrom {opts.CollectFrom} not supported")
    };

    static async Task<IReadOnlyCollection<(string ChannelId, Platform Platform)>> ChannelIds(YtCollectDbCtx db, (string Sql, JObject Args) select,
      string[] channels = null,
      Platform[] platforms = null, int? limit = null) {
      var chans = await db.Db.Query<(string ChannelId, Platform Platform)>("distinct channels", $@"
with raw_channels as (
  select distinct channel_id, platform from ({select.Sql})
  where channel_id is not null
  {limit.Do(l => $"limit {l}")}
)
, s as (
  select r.channel_id, coalesce(r.platform, c.platform) platform from raw_channels r
  left join channel_latest c on c.channel_id = r.channel_id
)
select * from s
{CommonWhereStatements(channels, platforms).Do(w => $"where {w.Join(" and ")}")}
", ToDapperArgs(select.Args));
      return chans;
    }

    /// <summary>Find videos from the given select that are missing one of the required parts</summary>
    static Task<IReadOnlyCollection<VideoListStats>> VideoStats(YtCollectDbCtx db, (string Sql, dynamic Args) select,
      string[] channels = null, Platform[] platforms = null, int? limit = null) =>
      db.Db.Query<VideoListStats>("collect list videos", @$"
with raw_vids as ({select.Sql})
, s as (
  select r.video_id
       , v.source_id
       , coalesce(r.channel_id, v.channel_id) channel_id
       , v.extra_updated
        , v.upload_date
        , v.updated
       , exists(select * from caption s where s.video_id=r.video_id) caption_exists
       , exists(select * from comment t where t.video_id=r.video_id) comment_exists
       , v.platform
      , e.media_url
  from raw_vids r
      left join video_latest v on v.video_id=r.video_id
      left join video_extra e on e.video_id=r.video_id
  where r.video_id is not null
  {limit.Do(l => $"limit {l}")}
)
select * from s
{CommonWhereStatements(channels, platforms).Do(w => $"where {w.Join(" and ")}")}
", ToDapperArgs(select.Args));

    static string[] CommonWhereStatements(string[] channels, Platform[] platforms) {
      var whereStatements = new[] {
        channels.Do(c => $"channel_id in ({c.SqlList()})"),
        platforms.Do(p => $"platform in ({p.SqlList()})")
      }.NotNull().ToArray();
      return whereStatements;
    }

    static DynamicParameters ToDapperArgs(JObject args) {
      if (args == null) return null;
      var kvp = args.Properties().Select(p => new KeyValuePair<string, object>(p.Name, ((JValue) p.Value).Value));
      var p = new DynamicParameters(kvp);
      return p;
    }

    public record VideoProcessResult(string VideoId, string ChannelId, Platform Platform, bool captionLoaded, ExtraPart[] Parts);
  }
}