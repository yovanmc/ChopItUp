using System.Globalization;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace ChopItUp.Hub.Tests;

/// <summary>Optional seeded shuffle of collection and test-case order, for hunting order dependence
/// between tests. Set <see cref="SeedVariable"/> to an integer before <c>dotnet test</c>; left unset,
/// both orderers hand the work to xUnit's defaults and change nothing. Under parallel execution only
/// the start order follows the seed, so a seed reproduces a whole run only when run serially.</summary>
public static class SeededOrder
{
    public const string SeedVariable = "CHOPITUP_TEST_ORDER_SEED";

    public static int? ParseSeed(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seed)) return seed;
        throw new InvalidOperationException($"{SeedVariable} must be an integer, not '{text}'.");
    }

    internal static int? SeedFromEnvironment() => ParseSeed(Environment.GetEnvironmentVariable(SeedVariable));

    /// <summary>Sorts by a stable key first, so the result depends only on the seed and the set of
    /// items, never on the order xUnit handed them over in.</summary>
    internal static List<T> Shuffle<T>(IEnumerable<T> items, Func<T, string> key, int seed)
    {
        var list = items.OrderBy(key, StringComparer.Ordinal).ToList();
        var random = new Random(seed);
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
        return list;
    }
}

public sealed class SeededTestCollectionOrderer : ITestCollectionOrderer
{
    private readonly int? _seed;
    private readonly ITestCollectionOrderer _inner;

    public SeededTestCollectionOrderer() : this(SeededOrder.SeedFromEnvironment(), new DefaultTestCollectionOrderer()) { }

    public SeededTestCollectionOrderer(int? seed, ITestCollectionOrderer inner)
    {
        _seed = seed;
        _inner = inner;
    }

    public IEnumerable<ITestCollection> OrderTestCollections(IEnumerable<ITestCollection> testCollections) =>
        _seed is { } seed ? SeededOrder.Shuffle(testCollections, c => c.DisplayName, seed) : _inner.OrderTestCollections(testCollections);
}

public sealed class SeededTestCaseOrderer : ITestCaseOrderer
{
    private readonly int? _seed;
    private readonly ITestCaseOrderer _inner;

    public SeededTestCaseOrderer(IMessageSink diagnosticMessageSink) : this(SeededOrder.SeedFromEnvironment(), new DefaultTestCaseOrderer(diagnosticMessageSink)) { }

    public SeededTestCaseOrderer(int? seed, ITestCaseOrderer inner)
    {
        _seed = seed;
        _inner = inner;
    }

    public IEnumerable<TTestCase> OrderTestCases<TTestCase>(IEnumerable<TTestCase> testCases) where TTestCase : ITestCase =>
        _seed is { } seed ? SeededOrder.Shuffle(testCases, c => c.UniqueID, seed) : _inner.OrderTestCases(testCases);
}
