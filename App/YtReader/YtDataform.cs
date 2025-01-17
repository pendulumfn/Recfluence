﻿using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mutuo.Etl.Pipe;
using Serilog;
using SysExtensions.Collections;
using SysExtensions.Serialization;
using SysExtensions.Text;
using SysExtensions.Threading;
using YtReader.Db;

namespace YtReader {
  public class DataformCfg {
    [Required]
    public ContainerCfg Container { get; set; } = new() {
      Cores = 2,
      Mem = 3,
      ImageName = "dataform",
      Exe = ""
    };
  }

  public class YtDataform {
    readonly ContainerLauncher Containers;
    readonly DataformCfg       Cfg;
    readonly SnowflakeCfg      SfCfg;
    readonly SeqCfg            SeqCfg;

    public YtDataform(ContainerLauncher containers, DataformCfg cfg, SnowflakeCfg sfCfg, SeqCfg seqCfg) {
      Containers = containers;
      Cfg = cfg;
      SfCfg = sfCfg;
      SeqCfg = seqCfg;
    }

    public async Task Update(ILogger log, bool fullLoad, string[] tables, bool includeDeps, CancellationToken cancel) {
      var sfCfg = SfCfg.JsonClone();
      sfCfg.Db = sfCfg.DbName(); // serialize the environment specific db name

      var args = new[] {
        fullLoad ? " --full-refresh " : null,
        includeDeps ? "--include-deps" : null,
        tables?.Any() == true ? $"{tables.Join(" ", t => $"--actions {t.ToUpperInvariant()}")}" : "--tags standard"
      }.NotNull().ToArray();

      var env = new (string name, string value)[] {
        ("SNOWFLAKE_JSON", sfCfg.ToJson()),
        ("REPO", "https://github.com/markledwich2/YouTubeNetworks_Dataform.git"),
        ("BRANCH", "master"),
        ("DATAFORM_RUN_ARGS", args.Join(" ")),
        ("SEQ", SeqCfg.SeqUrl.ToString())
      };

      log.Information("Dataform - launching container to update {Db}. dataform {Args}", sfCfg.Db, args);
      const string containerName = "dataform";
      var fullName = Cfg.Container.FullContainerImageName("latest");
      var dur = await Containers.RunContainer(containerName, fullName, env, log: log, cancel: cancel).WithDuration();
      log.Information("Dataform - container completed in {Duration}", dur.HumanizeShort());
    }
  }
}