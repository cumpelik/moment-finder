using System.Diagnostics;
using System.Globalization;
using MediaLens;
using MediaLens.Analysis;
using MediaLens.Buckets;
using MediaLens.Library;

// The library without the UI: for scripting, for a server without a display, and for testing the pipeline.
// The data folder is the same as the application's unless MEDIALENS_DATA says otherwise.

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    Console.WriteLine("""
        medialens <command>

          sources                          list sources
          add <folder> [name]              add a folder as a source
          remove <source id>               remove a source from the library (files stay)
          index [--no-scan]                scan sources, make thumbnails, analyze (until done)
          stats                            what is in the library
          search <query...>                e.g. search lyžař 2023, search video moře
          rank <text> <image...>           similarity of images to a text (model check)
          models [download]                state of the AI models / download them
          bucket list
          bucket create <name>
          bucket add <name> <query...>     add search results to a bucket
          bucket export <name> <folder> [--no-numbers]
        """);
    return 0;
}

using var host = new LibraryHost();
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
var ct = cts.Token;

switch (args[0])
{
    case "sources":
        foreach (var s in host.Repo.ListSources())
            Console.WriteLine($"{s.Id,4}  {s.Name,-20} {s.Root}");
        return 0;

    case "add" when args.Length >= 2:
    {
        var root = Path.GetFullPath(args[1]);
        if (!Directory.Exists(root)) return Fail($"Folder not found: {root}");
        var name = args.Length >= 3 ? args[2] : new DirectoryInfo(root).Name;
        var s = host.Repo.AddSource(SourceKinds.Local, root, name);
        Console.WriteLine($"Added source {s.Id}: {root}. Run 'medialens index'.");
        return 0;
    }

    case "remove" when args.Length >= 2:
        host.Repo.RemoveSource(long.Parse(args[1], CultureInfo.InvariantCulture));
        return 0;

    case "index":
    {
        if (!host.TryLoadSceneModel())
            Console.WriteLine("Search model not downloaded ('medialens models download'), only thumbnails will be made.");
        else
            Console.WriteLine($"Search model on {host.SceneModel!.Device}");
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
        var m = ModelCatalog.Siglip2Base;
        if (args.Length >= 2 && args[1] == "download")
        {
            await host.Models.DownloadAsync(m, new Progress<(long Done, long Total)>(p =>
                Console.Write($"\r{m.Title}: {p.Done / 1048576} / {p.Total / 1048576} MB   ")), ct);
            Console.WriteLine();
        }
        Console.WriteLine($"{m.Title} [{m.License}]: {(host.Models.IsInstalled(m) ? "installed" : $"not downloaded ({m.TotalSize / 1048576} MB)")}");
        return 0;
    }

    case "bucket" when args.Length >= 2:
        return await BucketCommand(args.Skip(1).ToArray());

    default:
        return Fail($"Unknown command: {string.Join(' ', args)}");
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

void PrintStats()
{
    var s = host.Repo.Stats(Indexer.AnalysisVersion);
    Console.WriteLine($"{s.Total} items ({s.Photos} photos, {s.Videos} videos), thumbnails {s.Thumbnailed}, analyzed {s.Analyzed}");
}

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}
