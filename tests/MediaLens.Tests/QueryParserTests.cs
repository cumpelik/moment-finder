using MediaLens.Library;
using MediaLens.Search;

namespace MediaLens.Tests;

public class QueryParserTests
{
    [Fact]
    public void YearAndKindAreFiltersTheRestIsText()
    {
        var q = QueryParser.Parse("lyžař video 2023");
        Assert.Equal("lyžař", q.Text);
        Assert.Equal(MediaKind.Video, q.Kind);
        Assert.Equal(2023, q.YearFrom);
        Assert.Equal(2023, q.YearTo);
    }

    [Fact]
    public void YearRangeInAnyOrder()
    {
        var q = QueryParser.Parse("fotky 2021-2019 moře u Splitu");
        Assert.Equal(MediaKind.Photo, q.Kind);
        Assert.Equal(2019, q.YearFrom);
        Assert.Equal(2021, q.YearTo);
        Assert.Equal("moře u Splitu", q.Text);
    }

    [Fact]
    public void NumbersThatAreNotYearsStayInTheText()
    {
        var q = QueryParser.Parse("dort 50 svíček 0815");
        Assert.Equal("dort 50 svíček 0815", q.Text);
        Assert.Null(q.YearFrom);
    }

    [Fact]
    public void YearFilterCoversTheWholeYear()
    {
        var f = QueryParser.ToFilter(QueryParser.Parse("2023"), new MediaFilter { SourceId = 7 });
        Assert.Equal(7, f.SourceId);
        Assert.Equal(Media.MediaProbe.ToTakenAt(new DateTime(2023, 1, 1)), f.From);
        Assert.Equal(Media.MediaProbe.ToTakenAt(new DateTime(2024, 1, 1)), f.To);
    }
}
