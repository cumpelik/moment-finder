using Dapper;
using MediaLens.Analysis;
using MediaLens.Analysis.Siglip;
using MediaLens.Buckets;
using MediaLens.Cloud;
using MediaLens.Data;
using MediaLens.Analysis.People;
using MediaLens.Library;
using MediaLens.People;
using MediaLens.Search;
using MediaLens.Vectors;

namespace MediaLens;

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
    public PeopleModel? PeopleModel { get; private set; }

    public SiglipModel? SceneModel { get; private set; }
    public FlatVectorIndex? SceneIndex { get; private set; }

    public LibraryHost(AppPaths? paths = null)
    {
        Paths = paths ?? new AppPaths();
        Db = new Database(Paths.DbPath);
        Repo = new MediaRepository(Db);
        Buckets = new BucketService(Db, Repo);
        Search = new SearchService(Repo);
        Cloud = new CloudService(Paths, Db, Repo);
        Indexer = new Indexer(Paths, Repo, Cloud.Rclone);
        Mirrors = new MirrorScheduler(Cloud, Db);
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
        Search.AttachSceneModel(model, index);
        Indexer.AttachSceneModel(model, index);
        return true;
    }

    public void Dispose()
    {
        Indexer.Dispose();
        Mirrors.Dispose();
        SceneIndex?.Dispose();
        SceneModel?.Dispose();
        PeopleModel?.Dispose();
    }
}
