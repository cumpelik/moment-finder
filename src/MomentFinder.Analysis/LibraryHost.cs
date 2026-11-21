using Dapper;
using MomentFinder.Analysis;
using MomentFinder.Analysis.Siglip;
using MomentFinder.Analysis.Translation;
using MomentFinder.Buckets;
using MomentFinder.Cloud;
using MomentFinder.Data;
using MomentFinder.Analysis.People;
using MomentFinder.Library;
using MomentFinder.People;
using MomentFinder.Search;
using MomentFinder.Vectors;

namespace MomentFinder;

/// <summary>All services of one library, shared by the application and the command line.</summary>
public sealed class LibraryHost : IDisposable
{
    public AppPaths Paths { get; }
    public Database Db { get; }
    public MediaRepository Repo { get; }
    public BucketService Buckets { get; }
    public SearchService Search { get; }
    public Indexer Indexer { get; }
    public ModelManager Models { get; }
    public PeopleRepository People { get; }
    public IdentityService Identity { get; }
    public EventBuilder Events { get; }
    public CloudService Cloud { get; }
    public MirrorScheduler Mirrors { get; }
    /// <summary>The library shared with the other computers that use it: changes go out and come in.</summary>
    public MomentFinder.Sync.LibrarySync Sync { get; }
    /// <summary>Copying the library of another computer here (put in place at the next start).</summary>
    public LibraryRestore Restore { get; }
    public PeopleModel? PeopleModel { get; private set; }

    public SiglipModel? SceneModel { get; private set; }
    /// <summary>Place names for GPS positions, once downloaded.</summary>
    public MomentFinder.Places.PlaceNames? Places { get; private set; }

    public bool TryLoadPlaces()
    {
        if (Places != null) return true;
        if (!Models.IsInstalled(ModelCatalog.Places)) return false;
        Places = new MomentFinder.Places.PlaceNames(Path.Combine(Models.DirOf(ModelCatalog.Places), "cities1000.zip"));
        return true;
    }
    public FlatVectorIndex? SceneIndex { get; private set; }
    public MarianTranslator? Translator { get; private set; }

    public LibraryHost(AppPaths? paths = null)
    {
        Paths = paths ?? new AppPaths();
        // A library downloaded with a recovery string takes its place before anything opens the old one.
        LibraryRestore.ApplyPending(Paths);
        Db = new Database(Paths.DbPath);
        LibraryRestore.AdaptToThisComputer(Paths, Db);
        Restore = new LibraryRestore(Paths);
        Repo = new MediaRepository(Db);
        Buckets = new BucketService(Db, Repo);
        Search = new SearchService(Repo);
        Cloud = new CloudService(Paths, Db, Repo);
        Indexer = new Indexer(Paths, Repo, Cloud.Rclone);
        Mirrors = new MirrorScheduler(Cloud, Db);
        Sync = new MomentFinder.Sync.LibrarySync(Db, Cloud, Paths);
        Db.Facts.Recorded += Sync.Wake;
        Indexer.SharedContentBatch = Sync.Content.TakeAsync;
        Models = new ModelManager(Paths.ModelDir);
        People = new PeopleRepository(Db);
        Identity = new IdentityService(People);
        Events = new EventBuilder(Db);
        Search.FirstAppearance = ids => People.FirstAppearance(ids);
        RefreshNames();
    }

    /// <summary>The search box learns new and renamed people.</summary>
    public void RefreshNames() =>
        Search.People = People.ListPersons().Select(p => new NamedPerson(p.Id, p.Name)).ToList();

    public bool PeopleModelInstalled => ModelCatalog.PeopleModels.All(Models.IsInstalled);

    /// <summary>Loads face and person recognition when downloaded.</summary>
    public bool TryLoadPeopleModel()
    {
        if (PeopleModel != null) return true;
        if (!PeopleModelInstalled) return false;
        PeopleModel = new PeopleModel(Models.DirOf(ModelCatalog.Faces), Models.DirOf(ModelCatalog.Persons), Models.DirOf(ModelCatalog.Reid));
        Indexer.AttachPeople(new PeopleAnalyzer(Paths, People, PeopleModel), Identity, Events);
        return true;
    }

    public bool SceneModelInstalled => Models.IsInstalled(ModelCatalog.Siglip2Base);

    /// <summary>Loads the search model when it is downloaded; until then the library works without text search.</summary>
    public bool TryLoadSceneModel()
    {
        if (Translator == null && Models.IsInstalled(ModelCatalog.CzechToEnglish))
        {
            Translator = new MarianTranslator(Models.DirOf(ModelCatalog.CzechToEnglish));
            Search.Translate = Translator.Translate;
        }
        if (SceneModel != null) return true;
        if (!SceneModelInstalled) return false;
        var model = new SiglipModel(Models.DirOf(ModelCatalog.Siglip2Base));
        var index = new FlatVectorIndex(Db, Path.Combine(Paths.VectorDir, "scene.vec"), model.ModelId, model.Dimension,
            onReset: () =>
            {
                using var c = Db.Open();
                c.Execute("UPDATE media SET analysis_version=0");
            });
        SceneModel = model;
        SceneIndex = index;
        Sync.Content.Scene = index;
        Search.AttachSceneModel(model, index);
        Indexer.AttachSceneModel(model, index);
        return true;
    }

    public void Dispose()
    {
        Indexer.Dispose();
        Mirrors.Dispose();
        Sync.Dispose();
        SceneIndex?.Dispose();
        SceneModel?.Dispose();
        Translator?.Dispose();
        PeopleModel?.Dispose();
    }
}
