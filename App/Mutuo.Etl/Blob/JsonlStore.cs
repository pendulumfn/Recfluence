using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Serilog;
using SysExtensions.Collections;
using SysExtensions.Serialization;
using SysExtensions.Text;
using SysExtensions.Threading;

namespace Mutuo.Etl.Blob {
  public interface IJsonlStore {
    public static readonly JsonSerializerSettings JCfg = new() {
      NullValueHandling = NullValueHandling.Ignore,
      DefaultValueHandling = DefaultValueHandling.Include,
      Formatting = Formatting.None,
      Converters = {
        new StringEnumConverter()
      }
    };
    SPath            Path  { get; }
    ISimpleFileStore Store { get; }

    /// <summary>returns the latest file (either in landing or staging) within the given partition</summary>
    /// <param name="partition"></param>
    /// <returns></returns>
    Task<StoreFileMd> LatestFile(SPath path = null);

    IAsyncEnumerable<IReadOnlyCollection<StoreFileMd>> Files(SPath path, bool allDirectories = false);
  }

  /// <summary>Read/write to storage for an append-only immutable collection of items sored as jsonl</summary>
  public class JsonlStore<T> : IJsonlStore {
    readonly Func<T, string> GetPartition;
    readonly Func<T, string> GetTs;

    readonly ILogger Log;
    readonly int     Parallel;
    readonly string  Version;

    /// <summary></summary>
    /// <param name="getTs">A function to get a timestamp for this file. This must always be greater for new records using an
    ///   invariant string comparer</param>
    public JsonlStore(ISimpleFileStore store, SPath path, Func<T, string> getTs,
      ILogger log, string version = "", Func<T, string> getPartition = null, int parallel = 8) {
      Store = store;
      Path = path;
      GetTs = getTs;
      Log = log;
      GetPartition = getPartition;
      Parallel = parallel;
      Version = version;
    }

    public ISimpleFileStore Store { get; }

    public SPath Path { get; }

    /// <summary>Returns the most recent file within this path (any child directories)</summary>
    public async Task<StoreFileMd> LatestFile(SPath path = null) {
      var files = await Files(path, allDirectories: true).SelectManyList();
      var latest = files.OrderByDescending(f => StoreFileMd.GetTs(f.Path)).FirstOrDefault();
      return latest;
    }

    public IAsyncEnumerable<IReadOnlyCollection<StoreFileMd>> Files(SPath path, bool allDirectories = false) =>
      Store.Files(FilePath(path), allDirectories);

    string Partition(T item) => GetPartition?.Invoke(item);

    /// <summary>The land path for a given partition is where files are first put before being optimised. Default -
    ///   [Path]/[Partition], LandAndStage - [Path]/land/[partition]</summary>
    SPath FilePath(string partition = null) => partition.NullOrEmpty() ? Path : Path.Add(partition);

    public Task Append(T item, ILogger log = null) => Append(item.InArray(), log);

    public async Task Append(IReadOnlyCollection<T> items, ILogger log = null) {
      log ??= Log;
      if (items.None()) return;
      await items.GroupBy(Partition).BlockDo(async g => {
        var ts = g.Max(GetTs);
        var path = JsonlStoreExtensions.FilePath(FilePath(g.Key), ts, Version);
        using var memStream = await g.ToJsonlGzStream(IJsonlStore.JCfg);
        await Store.Save(path, memStream, log).WithDuration();
      }, Parallel);
    }

    public async IAsyncEnumerable<IReadOnlyCollection<T>> Items(string partition = null) {
      await foreach (var dir in Files(partition, allDirectories: true))
      await foreach (var item in dir.BlockMap(f => LoadJsonl(f.Path), Parallel, capacity: 10))
        yield return item;
    }

    async Task<IReadOnlyCollection<T>> LoadJsonl(SPath path) {
      await using var stream = await Store.Load(path);
      return stream.LoadJsonlGz<T>(IJsonlStore.JCfg);
    }
  }

  public record StoreFileMd {
    public StoreFileMd() { }

    public StoreFileMd(SPath path, string ts, DateTime modified, long? bytes, string version = null) {
      Path = path;
      Ts = ts;
      Modified = modified;
      Bytes = bytes;
      Version = version;
    }

    public SPath    Path     { get; set; }
    public string   Ts       { get; set; }
    public DateTime Modified { get; set; }
    public string   Version  { get; set; }
    public long?    Bytes    { get; set; }

    public static StoreFileMd FromFileItem(FileListItem file) {
      var tokens = file.Path.Name.Split(".");
      var ts = tokens.FirstOrDefault();
      var version = tokens.Length >= 4 ? tokens[1] : null;
      return new(file.Path, ts, file.Modified?.UtcDateTime ?? DateTime.MinValue, file.Bytes, version);
    }

    public static string GetTs(SPath path) => path.Name.Split(".").FirstOrDefault();
  }
}