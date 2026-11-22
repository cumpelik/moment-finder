using System.Diagnostics;
using System.Globalization;
using MomentFinder;
using MomentFinder.Analysis;
using MomentFinder.Buckets;
using MomentFinder.Library;

// The library without the UI: for scripting, for a server without a display, and for testing the pipeline.
// The data folder is the same as the application's unless MOMENTFINDER_DATA says otherwise.

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    Console.WriteLine("""
        momentfinder <command>

          sources                          list sources
          add <folder> [name]              add a folder as a source
          remove <source id>               remove a source from the library (files stay)
          index [--no-scan]                scan sources, make thumbnails, analyze (until done)
          stats                            what is in the library
          search <query...>                e.g. search lyžař 2023, search video moře
          rank <text> <image...>           similarity of images to a text (model check)
          models [download]                state of the AI models / download them
          persons                          named people
          clusters                         unnamed groups of faces
          name <cluster> <name>            name an unnamed group
          confirm <detection> <name>       say who is on one detection (face or body)
          reject <detection> <name>        say who is not
          match                            run the identity matching now
          detections <file name part>      what was found on an item
          cloud list                       clouds and mirrors
          cloud add-folder <name> <folder> [--password p]
          cloud add-s3 <name> <bucket> <access key> <secret> [--endpoint url] [--region r] [--provider p] [--password p]
          cloud add-onedrive <name> [--password p]     (opens the browser to sign in)
          cloud add-gdrive <name> [--password p]       (opens the browser to sign in)
          cloud folders <cloud id>         folders in a cloud
          cloud source <cloud id> <folder> add a cloud folder as a source
          cloud restore <cloud id> <folder> <local folder>
          cloud import-index <cloud id>    names, people and buckets from the cloud's index backup
          mirror add <source id> <cloud id> [--encrypt]   back a folder up to a cloud
          mirror run [mirror id]           compare folders with the cloud: move, upload, note deleted ones
          mirror gone <mirror id>          files deleted from the folder that are still in the cloud
          mirror delete-gone <mirror id>   delete them in the cloud too
          mirror bring-back <mirror id>    copy them back into the folder
          mirror to-cloud <mirror id>      the folder is cloned to the cloud (a source of its own), the local one stays as it is
          source home <source id> <folder> a folder in the cloud gets a home here, backed up from now on
          backup cloud <cloud id>          choose the cloud the library itself is kept in (0: none)
          backup run                       the library (index, search data, thumbnails, clouds) into that cloud
          recovery show                    the recovery string: brings the library back on any computer
          recovery restore <string>        bring a library back (the newest backup the string leads to)
          bucket list
          bucket create <name>
          bucket add <name> <query...>     add search results to a bucket
          bucket export <name> <folder> [--no-numbers]
        """);
    return 0;
}

var paths = new AppPaths();
using var libraryLock = LibraryLock.TryAcquire(paths.DataDir);
if (libraryLock == null)
{
    Console.Error.WriteLine("MomentFinder is running with this library: close it first (one program at a time works on a library).");
    return 1;
}
using var host = new LibraryHost(paths);
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
var ct = cts.Token;

switch (args[0])
{
    case "sources":
        foreach (var s in host.Repo.ListSources())
            Console.WriteLine($"{s.Id,4}  {s.Name,-20} {s.Remote ?? s.Root}{(s.ReadOnly ? $"  (backup of {s.HomeName ?? "another computer"}, only for looking)" : "")}");
        return 0;

    case "add" when args.Length >= 2:
    {
        var root = Path.GetFullPath(args[1]);
        if (!Directory.Exists(root)) return Fail($"Folder not found: {root}");
        var name = args.Length >= 3 ? args[2] : new DirectoryInfo(root).Name;
        var s = host.Repo.AddSource(SourceKinds.Local, root, name);
        Console.WriteLine($"Added source {s.Id}: {root}. Run 'momentfinder index'.");
        return 0;
    }

    case "remove" when args.Length >= 2:
        host.Repo.RemoveSource(long.Parse(args[1], CultureInfo.InvariantCulture));
        return 0;

    case "index":
    {
        if (!host.TryLoadSceneModel())
            Console.WriteLine("Search model not downloaded ('momentfinder models download'), only thumbnails will be made.");
        else
            Console.WriteLine($"Search model on {host.SceneModel!.Device}");
        if (host.TryLoadPeopleModel())
            Console.WriteLine($"People model on {host.PeopleModel!.Device}");
        var sw = Stopwatch.StartNew();
        var last = "";
        host.Indexer.StatusChanged += st =>
        {
            var line = st.Total > 0 ? $"{st.Message}: {st.Done}/{st.Total}" : st.Message;
            if (line == last) return;
            last = line;
            Console.Write($"\r{line,-70}");
        };
        await host.Indexer.RunUntilIdleAsync(scan: !args.Contains("--no-scan"), ct);
        Console.WriteLine();
        PrintStats();
        Console.WriteLine($"Done in {sw.Elapsed:hh\\:mm\\:ss}");
        return 0;
    }

    case "stats":
        PrintStats();
        return 0;

    case "search" when args.Length >= 2:
    {
        host.TryLoadSceneModel();
        var sw = Stopwatch.StartNew();
        var hits = host.Search.Search(string.Join(' ', args.Skip(1)), new MediaFilter());
        foreach (var h in hits.Take(50))
        {
            var at = h.Item.IsVideo && h.TimeMs > 0 ? $" @{TimeSpan.FromMilliseconds(h.TimeMs):mm\\:ss}" : "";
            Console.WriteLine($"{h.Score,6:0.000}  {h.Item.Taken:yyyy-MM-dd}  {h.Item.FullPath}{at}");
        }
        Console.WriteLine($"{hits.Count} results in {sw.ElapsedMilliseconds} ms");
        return 0;
    }

    case "rank" when args.Length >= 3:
    {
        if (!host.TryLoadSceneModel()) return Fail("Search model not downloaded");
        var text = host.SceneModel!.EmbedText(args[1]);
        var files = args.Skip(2).ToList();
        var vectors = host.SceneModel.EmbedImages(files);
        foreach (var (file, score) in files.Zip(vectors.Select(v => System.Numerics.Tensors.TensorPrimitives.Dot(v, text)))
                     .OrderByDescending(x => x.Second))
            Console.WriteLine($"{score,7:0.0000}  {file}");
        return 0;
    }

    case "models":
    {
        foreach (var m in ModelCatalog.All)
        {
            if (args.Length >= 2 && args[1] == "download")
            {
                await host.Models.DownloadAsync(m, new Progress<(long Done, long Total)>(p =>
                    Console.Write($"\r{m.Title}: {p.Done / 1048576} / {p.Total / 1048576} MB   ")), ct);
                Console.WriteLine();
            }
            Console.WriteLine($"{m.Title} [{m.License}]: {(host.Models.IsInstalled(m) ? "installed" : $"not downloaded ({m.TotalSize / 1048576} MB)")}");
        }
        return 0;
    }

    case "persons":
        foreach (var p in host.People.ListPersons(true))
            Console.WriteLine($"{p.Id,4}  {p.Name,-25} {p.MediaCount} items{(p.Hidden ? " (hidden)" : "")}");
        Console.WriteLine($"{host.People.SuggestionCount()} suggestions to review");
        return 0;

    case "clusters":
        foreach (var c in host.People.UnnamedClusters())
        {
            var files = host.Repo.Get(host.People.Detections(cluster: c.Cluster).Select(d => d.MediaId)).Values.Select(m => m.FileName).Distinct();
            Console.WriteLine($"{c.Cluster,6}  {c.Media} items: {string.Join(", ", files.Take(8))}");
        }
        return 0;

    case "name" when args.Length >= 3:
    {
        var person = host.People.FindOrCreatePerson(string.Join(' ', args.Skip(2)));
        host.People.Confirm(host.People.ClusterDetectionIds(long.Parse(args[1], CultureInfo.InvariantCulture)), person.Id);
        PrintMatch(host.Identity.Recompute());
        return 0;
    }

    case "confirm" or "reject" when args.Length >= 3:
    {
        var person = host.People.FindOrCreatePerson(string.Join(' ', args.Skip(2)));
        var id = long.Parse(args[1], CultureInfo.InvariantCulture);
        if (args[0] == "confirm") host.People.Confirm([id], person.Id);
        else host.People.Reject([id], person.Id);
        PrintMatch(host.Identity.Recompute());
        return 0;
    }

    case "match":
        host.Events.Rebuild();
        PrintMatch(host.Identity.Recompute());
        return 0;

    case "detections" when args.Length >= 2:
    {
        var persons = host.People.ListPersons(true).ToDictionary(p => p.Id, p => p.Name);
        foreach (var item in host.Repo.Query(new MediaFilter()).Where(m => m.FileName.Contains(args[1], StringComparison.OrdinalIgnoreCase)))
        {
            Console.WriteLine(item.FileName);
            foreach (var d in host.People.DetectionsOfMedia(item.Id))
                Console.WriteLine($"  {d.Id,6} {d.Kind,-4} t{d.TimeMs,6} q{d.Quality:0.00} track {d.Track?.ToString() ?? "-",5} link {d.LinkedId?.ToString() ?? "-",5} [{d.X:0.00} {d.Y:0.00} {d.W:0.00} {d.H:0.00}] " +
                                  $"{(d.PersonId is { } p ? persons[p] : "?"),-15} {d.AssignSource} {d.Confidence:0.00}" +
                                  (d.SuggestPersonId is { } s ? $"  maybe {persons[s]} {d.SuggestScore:0.00}" : "") +
                                  (d.Cluster is { } cl ? $"  cluster {cl}" : ""));
        }
        return 0;
    }

    case "people" when args.Length >= 2:
    {
        var host2 = host;
        var pm = new MomentFinder.Analysis.People.PeopleModel(host2.Models.DirOf(ModelCatalog.Faces), host2.Models.DirOf(ModelCatalog.Persons), host2.Models.DirOf(ModelCatalog.Reid));
        Console.WriteLine($"People model on {pm.Device}");
        var outDir = args.Length >= 3 ? args[2] : null;
        foreach (var file in Directory.Exists(args[1]) ? Directory.GetFiles(args[1]).Order().ToArray() : [args[1]])
        {
            var img = MomentFinder.Media.RgbImage.Load(file, 1600);
            var sw = Stopwatch.StartNew();
            var found = pm.Analyze(img);
            Console.WriteLine($"{Path.GetFileName(file)} {img.Width}x{img.Height} {sw.ElapsedMilliseconds} ms: " +
                string.Join(", ", found.Select(d => $"{d.Kind} {d.Box.W:0}x{d.Box.H:0} s{d.Score:0.00} q{d.Quality:0.00}")));
            if (outDir != null)
                for (var i = 0; i < found.Count; i++)
                    found[i].Crop!.SaveJpeg(Path.Combine(outDir, $"{Path.GetFileNameWithoutExtension(file)}_{i}_{found[i].Kind}.jpg"));
        }
        return 0;
    }

    case "facesim" when args.Length >= 2:
    {
        // Best face per file, then the similarity of files named alike (same prefix before the last _) vs others.
        var pm = new MomentFinder.Analysis.People.PeopleModel(host.Models.DirOf(ModelCatalog.Faces), host.Models.DirOf(ModelCatalog.Persons), host.Models.DirOf(ModelCatalog.Reid));
        var faces = new List<(string Name, float[] E)>();
        foreach (var file in Directory.GetFiles(args[1]).Order())
        {
            var best = pm.Analyze(MomentFinder.Media.RgbImage.Load(file, 1600)).Where(d => d.Kind == MomentFinder.Analysis.DetectionKind.Face)
                .OrderByDescending(d => d.Box.W * d.Quality).FirstOrDefault();
            if (best != null) faces.Add((Path.GetFileNameWithoutExtension(file), best.Embedding));
        }
        string Who(string n) => n[..n.LastIndexOf('_')];
        var same = new List<float>(); var other = new List<float>();
        for (var i = 0; i < faces.Count; i++)
        for (var j = i + 1; j < faces.Count; j++)
        {
            var sim = System.Numerics.Tensors.TensorPrimitives.Dot(faces[i].E, faces[j].E);
            (Who(faces[i].Name) == Who(faces[j].Name) ? same : other).Add(sim);
        }
        Console.WriteLine($"faces {faces.Count}; same person: n={same.Count} min {same.Min():0.00} median {same.Order().ElementAt(same.Count / 2):0.00}; " +
                          $"different: n={other.Count} max {other.Max():0.00} median {other.Order().ElementAt(other.Count / 2):0.00}");
        foreach (var t in new[] { 0.3f, 0.35f, 0.4f, 0.45f, 0.5f, 0.55f })
            Console.WriteLine($"  threshold {t:0.00}: same above {same.Count(x => x >= t) * 100 / same.Count}%, different above {other.Count(x => x >= t) * 100.0 / other.Count:0.0}%");
        return 0;
    }

    case "modelinfo" when args.Length >= 2:
    {
        using var session = new Microsoft.ML.OnnxRuntime.InferenceSession(args[1]);
        foreach (var (name, meta) in session.InputMetadata)
            Console.WriteLine($"in  {name} {meta.ElementDataType} [{string.Join(',', meta.Dimensions)}]");
        foreach (var (name, meta) in session.OutputMetadata)
            Console.WriteLine($"out {name} {meta.ElementDataType} [{string.Join(',', meta.Dimensions)}]");
        return 0;
    }

    case "backup" when args.Length >= 3 && args[1] == "cloud":
    {
        var chosen = host.Cloud.Clouds().FirstOrDefault(c => c.Id.ToString() == args[2]);
        if (chosen == null && args[2] != "0") return Fail("No such cloud (see cloud list)");
        host.Cloud.BackupCloud = chosen;
        Console.WriteLine(chosen == null ? "The library is not backed up" : $"The library is kept in {chosen.Name}");
        return 0;
    }

    case "backup" when args.Length >= 2 && args[1] == "run":
    {
        if (host.Cloud.BackupCloud is not { } cloud) return Fail("Choose the cloud for the library first (backup cloud <cloud id>)");
        if (host.Db.GetSetting(MomentFinder.Sync.LibrarySync.SharedSetting) == "1")
            return Fail("The library is shared: the shared library in the cloud is its backup");
        try
        {
            await host.Cloud.Backup.RunAsync(cloud, full: true, new Progress<string>(what => Console.Write($"\r  {what,-50}")), ct);
        }
        catch (MomentFinder.Cloud.RcloneException e)
        {
            return Fail(e.Message);
        }
        Console.WriteLine($"\rThe library is backed up to {cloud.Name}" + new string(' ', 30));
        return 0;
    }

    case "recovery" when args.Length >= 2 && args[1] == "show":
    {
        if (host.Cloud.BackupCloud is not { } cloud) return Fail("Choose the cloud for the library first (backup cloud <cloud id>)");
        Console.WriteLine(await host.Cloud.RecoveryStringAsync(cloud, ct));
        return 0;
    }

    case "recovery" when args.Length >= 3 && args[1] == "restore":
    {
        if (MomentFinder.Cloud.RecoveryString.Parse(args[2]) is not { } recovery) return Fail("This is not a recovery string");
        try
        {
            var restore = new MomentFinder.Cloud.LibraryRestore(host.Paths);
            var found = await restore.FindAsync(recovery, ct);
            if (found.Count == 0) return Fail("The storage answers, but there is no library backup in it");
            Console.WriteLine(found[0].Title);
            await restore.DownloadAsync(recovery, found[0], thumbnails: true, new Progress<MomentFinder.Cloud.LibraryRestore.Step>(s => Console.Write($"\r  {s.What} {s.Detail,-60}")), ct);
        }
        catch (MomentFinder.Cloud.RcloneException e)
        {
            return Fail(e.Message);
        }
        Console.WriteLine("\rDownloaded; the library takes its place at the next start of MomentFinder (or of this tool).");
        return 0;
    }

    case "cloud" when args.Length >= 2:
        try
        {
            return await CloudCommand(args.Skip(1).ToArray());
        }
        catch (MomentFinder.Cloud.RcloneException e)
        {
            return Fail(e.Message);
        }

    case "mirror" when args.Length >= 2:
        try
        {
            return await MirrorCommand(args.Skip(1).ToArray());
        }
        catch (Exception e) when (e is MomentFinder.Cloud.RcloneException or InvalidOperationException or IOException)
        {
            return Fail(e.Message);
        }

    case "source" when args.Length >= 4 && args[1] == "home":
        try
        {
            await host.Cloud.MakeHomeAsync(long.Parse(args[2], CultureInfo.InvariantCulture), args[3],
                new Progress<MomentFinder.Cloud.Rclone.CopyProgress>(p => Console.Write($"\r  {p.Text,-70}")), ct);
            Console.WriteLine($"\rThe folder is at home in {args[3]}, backed up to the cloud from now on." + new string(' ', 20));
            return 0;
        }
        catch (Exception e) when (e is MomentFinder.Cloud.RcloneException or InvalidOperationException)
        {
            return Fail(e.Message);
        }

    case "bucket" when args.Length >= 2:
        return await BucketCommand(args.Skip(1).ToArray());

    default:
        return Fail($"Unknown command: {string.Join(' ', args)}");
}

async Task<int> MirrorCommand(string[] a)
{
    long Id(int i) => long.Parse(a[i], CultureInfo.InvariantCulture);
    var progress = new Progress<MomentFinder.Cloud.Rclone.CopyProgress>(p => Console.Write($"\r  {p.Text,-70}"));
    switch (a[0])
    {
        case "add" when a.Length >= 3:
        {
            var m = await host.Cloud.AddMirrorAsync(Id(1), Id(2), a.Contains("--encrypt"), ct);
            Console.WriteLine($"Mirror {m.Id}: {m.SourceName} -> {m.CloudName}/{m.Target}{(m.Encrypted ? " (encrypted)" : "")}");
            return 0;
        }
        case "run":
        {
            var failed = false;
            foreach (var m in host.Cloud.Mirrors().Where(m => a.Length < 2 || m.Id == Id(1)))
            {
                Console.WriteLine($"{m.SourceName} -> {m.CloudName}/{m.Target}");
                try
                {
                    var r = await host.Cloud.MirrorAsync(m.Id, progress, ct);
                    Console.WriteLine($"\r  uploaded {r.Uploaded}, moved {r.Moved}, deleted here but still in the cloud {r.Gone}" + new string(' ', 20));
                }
                catch (MomentFinder.Cloud.RcloneException e)
                {
                    Console.WriteLine("\r  " + e.Message);
                    failed = true;
                }
            }
            return failed ? 1 : 0;
        }
        case "gone" when a.Length >= 2:
            foreach (var g in host.Cloud.Gone(Id(1))) Console.WriteLine($"{g.Size,12:N0}  {g.Path}");
            return 0;
        case "delete-gone" when a.Length >= 2:
            await host.Cloud.DeleteGoneAsync(Id(1), ct);
            Console.WriteLine("Deleted in the cloud.");
            return 0;
        case "bring-back" when a.Length >= 2:
            await host.Cloud.BringBackGoneAsync(Id(1), progress, ct);
            Console.WriteLine("\rBack in the folder." + new string(' ', 60));
            return 0;
        case "to-cloud" when a.Length >= 2:
        {
            var local = await host.Cloud.MoveToCloudAsync(Id(1), progress, ct);
            Console.WriteLine($"\rThe folder lives in the cloud now; the one on this computer is source {local}." + new string(' ', 20));
            return 0;
        }
    }
    return Fail($"Unknown command: mirror {string.Join(' ', a)}");
}

async Task<int> CloudCommand(string[] a)
{
    string? Opt(string name) => a.SkipWhile(x => x != name).Skip(1).FirstOrDefault();
    var positional = new List<string>();
    for (var i = 0; i < a.Length; i++)
    {
        if (a[i].StartsWith("--")) { i++; continue; }
        positional.Add(a[i]);
    }
    var password = Opt("--password");
    switch (positional[0])
    {
        case "list":
            foreach (var c in host.Cloud.Clouds())
                Console.WriteLine($"{c.Id,4}  {c.Name,-20} {c.KindTitle,-12} {(c.Encrypted ? "encrypted" : "plain")}");
            foreach (var m in host.Cloud.Mirrors())
                Console.WriteLine($"  mirror {m.Id}: {m.SourceName} -> {m.CloudName}/{m.Target}{(m.Encrypted ? " (encrypted)" : "")}{(m.Enabled ? "" : " (stopped)")}" +
                                  $"  last: {m.LastResult ?? "never"}{(m.Gone > 0 ? $", {m.Gone} deleted here still in the cloud" : "")}");
            return 0;
        case "add-folder" when positional.Count >= 3:
        {
            var c = await host.Cloud.AddFolderAsync(positional[1], Path.GetFullPath(positional[2]), password, ct);
            Console.WriteLine($"Cloud {c.Id} added");
            return 0;
        }
        case "add-s3" when positional.Count >= 5:
        {
            var c = await host.Cloud.AddS3Async(positional[1], new MomentFinder.Cloud.S3Settings(Opt("--provider") ?? "AWS", Opt("--endpoint") ?? "",
                Opt("--region") ?? "", positional[3], positional[4], positional[2]), password, ct);
            Console.WriteLine($"Cloud {c.Id} added");
            return 0;
        }
        case "add-onedrive" when positional.Count >= 2:
            Console.WriteLine($"Cloud {(await host.Cloud.AddOneDriveAsync(positional[1], password, ct)).Id} added");
            return 0;
        case "add-gdrive" when positional.Count >= 2:
            Console.WriteLine($"Cloud {(await host.Cloud.AddGoogleDriveAsync(positional[1], password, ct)).Id} added");
            return 0;
        case "folders" when positional.Count >= 2:
            foreach (var f in await host.Cloud.FoldersAsync(long.Parse(positional[1], CultureInfo.InvariantCulture), ct))
                Console.WriteLine(f);
            return 0;
        case "source" when positional.Count >= 3:
        {
            var s = host.Cloud.AddCloudSource(long.Parse(positional[1], CultureInfo.InvariantCulture), positional[2]);
            Console.WriteLine($"Source {s.Id}: {s.Name}. Run 'momentfinder index'.");
            return 0;
        }
        case "restore" when positional.Count >= 4:
            await host.Cloud.RestoreAsync(long.Parse(positional[1], CultureInfo.InvariantCulture), positional[2], Path.GetFullPath(positional[3]),
                new Progress<MomentFinder.Cloud.Rclone.CopyProgress>(p => Console.Write($"\r{p.Text,-70}")), ct);
            Console.WriteLine("\rRestored" + new string(' ', 70));
            return 0;
        case "import-index" when positional.Count >= 2:
        {
            var file = await host.Cloud.DownloadIndexAsync(long.Parse(positional[1], CultureInfo.InvariantCulture), ct);
            if (file == null) return Fail("The cloud has no index backup yet (run a mirror first)");
            var r = new MomentFinder.Cloud.IndexImport(host.Db).Import(file);
            File.Delete(file);
            Console.WriteLine($"Matched {r.MatchedItems} items: {r.People} new people, {r.Detections} detections, {r.BucketItems} bucket items");
            return 0;
        }
        default:
            return Fail("Unknown cloud command");
    }
}

async Task<int> BucketCommand(string[] a)
{
    switch (a[0])
    {
        case "list":
            foreach (var b in host.Buckets.List())
                Console.WriteLine($"{b.Id,4}  {b.Name,-30} {b.Count} items");
            return 0;
        case "create" when a.Length >= 2:
            host.Buckets.Create(a[1]);
            return 0;
        case "add" when a.Length >= 3:
        {
            var bucket = host.Buckets.Find(a[1]) ?? host.Buckets.Create(a[1]);
            host.TryLoadSceneModel();
            var hits = host.Search.Search(string.Join(' ', a.Skip(2)), new MediaFilter());
            var added = host.Buckets.Add(bucket.Id, hits.Select(h => h.Item.Id));
            Console.WriteLine($"Added {added} of {hits.Count} to {bucket.Name}");
            return 0;
        }
        case "export" when a.Length >= 3:
        {
            if (host.Buckets.Find(a[1]) is not { } bucket) return Fail($"No bucket {a[1]}");
            var result = await host.Buckets.ExportAsync(bucket.Id, Path.GetFullPath(a[2]),
                new ExportOptions(NumberByOrder: !a.Contains("--no-numbers")),
                new Progress<(int Done, int Total)>(p => Console.Write($"\r{p.Done}/{p.Total}")), ct);
            Console.WriteLine($"\rCopied {result.Copied}, already there {result.Skipped}, errors {result.Errors.Count}");
            foreach (var e in result.Errors) Console.WriteLine("  " + e);
            return result.Errors.Count == 0 ? 0 : 1;
        }
        default:
            return Fail("Unknown bucket command");
    }
}

void PrintMatch(MomentFinder.People.IdentityService.Result r) =>
    Console.WriteLine($"{r.Faces} faces, {r.Bodies} bodies: {r.Assigned} recognized, {r.Suggestions} suggestions, {r.Clusters} unnamed groups");

void PrintStats()
{
    var s = host.Repo.Stats(Indexer.AnalysisVersion);
    Console.WriteLine($"{s.Total} items ({s.Photos} photos, {s.Videos} videos), thumbnails {s.Thumbnailed}, analyzed {s.Analyzed}, people {s.PeopleAnalyzed}");
}

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}
