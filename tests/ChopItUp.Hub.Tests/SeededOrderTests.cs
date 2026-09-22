using Xunit.Abstractions;
using Xunit.Sdk;

namespace ChopItUp.Hub.Tests;

public sealed class SeededOrderTests
{
    private static List<FakeCollection> Collections(int n) =>
        Enumerable.Range(0, n).Select(i => new FakeCollection { DisplayName = "c" + i }).ToList();

    private static List<FakeCase> Cases(int n) =>
        Enumerable.Range(0, n).Select(i => new FakeCase { UniqueID = "t" + i }).ToList();

    [Fact]
    public void Without_a_seed_collections_keep_the_default_order()
    {
        var input = Collections(12);
        var inner = new ReversingCollectionOrderer();
        var ordered = new SeededTestCollectionOrderer(seed: null, inner).OrderTestCollections(input).ToList();
        Assert.Equal(inner.OrderTestCollections(input), ordered);
    }

    [Fact]
    public void Without_a_seed_cases_keep_the_default_order()
    {
        var input = Cases(12);
        var inner = new ReversingCaseOrderer();
        var ordered = new SeededTestCaseOrderer(seed: null, inner).OrderTestCases(input).ToList();
        Assert.Equal(inner.OrderTestCases(input), ordered);
    }

    [Fact]
    public void A_seed_reorders_collections_the_same_way_whatever_the_input_order()
    {
        var input = Collections(20);
        var first = new SeededTestCollectionOrderer(7, new ReversingCollectionOrderer()).OrderTestCollections(input).Select(c => c.DisplayName).ToList();
        var again = new SeededTestCollectionOrderer(7, new ReversingCollectionOrderer()).OrderTestCollections(Enumerable.Reverse(input)).Select(c => c.DisplayName).ToList();
        var other = new SeededTestCollectionOrderer(8, new ReversingCollectionOrderer()).OrderTestCollections(input).Select(c => c.DisplayName).ToList();

        Assert.Equal(input.Select(c => c.DisplayName).Order(), first.Order());
        Assert.Equal(first, again);
        Assert.NotEqual(first, other);
        Assert.NotEqual(input.Select(c => c.DisplayName), first);
    }

    [Fact]
    public void A_seed_reorders_cases_as_a_permutation()
    {
        var input = Cases(20);
        var first = new SeededTestCaseOrderer(7, new ReversingCaseOrderer()).OrderTestCases(input).Select(c => c.UniqueID).ToList();
        var again = new SeededTestCaseOrderer(7, new ReversingCaseOrderer()).OrderTestCases(Enumerable.Reverse(input)).Select(c => c.UniqueID).ToList();

        Assert.Equal(input.Select(c => c.UniqueID).Order(), first.Order());
        Assert.Equal(first, again);
        Assert.NotEqual(input.Select(c => c.UniqueID), first);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("42", 42)]
    [InlineData(" -3 ", -3)]
    public void The_seed_is_read_from_its_variable_text(string? text, int? expected) =>
        Assert.Equal(expected, SeededOrder.ParseSeed(text));

    [Fact]
    public void A_seed_that_is_not_a_number_is_refused()
    {
        Assert.Throws<InvalidOperationException>(() => SeededOrder.ParseSeed("abc"));
    }

    private sealed class ReversingCollectionOrderer : ITestCollectionOrderer
    {
        public IEnumerable<ITestCollection> OrderTestCollections(IEnumerable<ITestCollection> testCollections) => testCollections.Reverse().ToList();
    }

    private sealed class ReversingCaseOrderer : ITestCaseOrderer
    {
        public IEnumerable<TTestCase> OrderTestCases<TTestCase>(IEnumerable<TTestCase> testCases) where TTestCase : ITestCase => testCases.Reverse().ToList();
    }

    private sealed class FakeCollection : LongLivedMarshalByRefObject, ITestCollection
    {
        public ITypeInfo CollectionDefinition => null!;
        public string DisplayName { get; init; } = "";
        public ITestAssembly TestAssembly => null!;
        public Guid UniqueID { get; } = Guid.NewGuid();
        public void Deserialize(IXunitSerializationInfo info) => throw new NotSupportedException();
        public void Serialize(IXunitSerializationInfo info) => throw new NotSupportedException();
    }

    private sealed class FakeCase : LongLivedMarshalByRefObject, ITestCase
    {
        public string DisplayName => UniqueID;
        public string SkipReason => null!;
        public ISourceInformation SourceInformation { get; set; } = null!;
        public ITestMethod TestMethod => null!;
        public object[] TestMethodArguments => [];
        public Dictionary<string, List<string>> Traits => [];
        public string UniqueID { get; init; } = "";
        public void Deserialize(IXunitSerializationInfo info) => throw new NotSupportedException();
        public void Serialize(IXunitSerializationInfo info) => throw new NotSupportedException();
    }
}
