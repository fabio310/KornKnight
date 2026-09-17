namespace ChessBot.Tests;

using System.Linq;
using System.Reflection;
using ChessBot.Engine.Search;
using Xunit;

/// <summary>
/// ENGINEERING.md's first standing principle: the engine has no feature switches. It plays its
/// best known configuration, always, and a flag that survives a decision is a decision that was
/// never made.
///
/// The heuristic toggles are the one exception, and only because the gate that compares the search
/// against an unpruned minimax has no other way to exclude the unsound heuristics from the
/// comparison. The exception is kept honest by keeping the toggles out of reach: internal setters,
/// so the engine's own callers — the UCI session, the match runner, the app — cannot ask it to
/// play with a heuristic off.
///
/// These tests are what stops that eroding. Making a setter public again is one keystroke and
/// nothing else in the codebase would notice.
/// </summary>
public class SearchSettingsSurfaceTests
{
    /// <summary>
    /// Every toggle the search reads. Named rather than discovered so that adding a new one is a
    /// deliberate act: a toggle added and not listed here fails the count assertion below.
    /// </summary>
    private static readonly string[] Toggles =
    {
        nameof(SearchSettings.UseNullMove),
        nameof(SearchSettings.UseLmr),
        nameof(SearchSettings.UseFutility),
        nameof(SearchSettings.UseTranspositionTable),
        nameof(SearchSettings.UseQuiescence),
        nameof(SearchSettings.UseAspiration),
        nameof(SearchSettings.UseCheckExtension),
    };

    [Theory]
    [MemberData(nameof(ToggleNames))]
    public void AToggleCanBeReadFromAnywhereAndSetFromNowhereOutsideTheEngine(string name)
    {
        PropertyInfo property = typeof(SearchSettings).GetProperty(name)!;

        Assert.NotNull(property);
        Assert.True(property.GetMethod!.IsPublic, $"{name} should still be readable");
        Assert.False(property.SetMethod!.IsPublic,
                     $"{name} has a public setter — any caller can now ask the engine to play " +
                     "with this heuristic off, which is the feature switch the guide forbids");
    }

    public static TheoryData<string> ToggleNames()
    {
        var data = new TheoryData<string>();
        foreach (string name in Toggles) data.Add(name);
        return data;
    }

    /// <summary>
    /// No toggle may be added without being listed above, and none of the old LMR schedule
    /// overrides may come back. Those existed to A/B a schedule without recompiling; arms are
    /// separate binaries now, so the harness never needs the engine to carry a knob for it.
    /// </summary>
    [Fact]
    public void TheToggleSurfaceIsExactlyTheListedOne()
    {
        var found = typeof(SearchSettings)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.Name.StartsWith("Use") || p.Name.StartsWith("Lmr"))
            .Select(p => p.Name)
            .OrderBy(n => n)
            .ToArray();

        Assert.Equal(Toggles.OrderBy(n => n).ToArray(), found);
    }

    /// <summary>
    /// The gate's own construction, which is the reason any of this exists. UseFutility gates
    /// futility, reverse futility and late move pruning together, so one switch has to turn off
    /// all three — they are the same bet made in three directions and the gate needs the whole
    /// family off at once.
    /// </summary>
    [Fact]
    public void PlainAlphaBetaTurnsOffEveryUnsoundHeuristic()
    {
        var settings = SearchSettings.PlainAlphaBeta(5);

        Assert.Equal(5, settings.MaxDepth);
        Assert.False(settings.UseNullMove);
        Assert.False(settings.UseLmr);
        Assert.False(settings.UseFutility);
        Assert.False(settings.UseTranspositionTable);
        Assert.False(settings.UseQuiescence);
        Assert.False(settings.UseAspiration);
        Assert.False(settings.UseCheckExtension);
    }

    /// <summary>
    /// And the default is everything on, so an ordinary caller gets the engine's best known
    /// configuration without asking for it.
    /// </summary>
    [Fact]
    public void ADefaultSettingsObjectHasEveryHeuristicOn()
    {
        var settings = new SearchSettings();

        foreach (string name in Toggles)
            Assert.True((bool)typeof(SearchSettings).GetProperty(name)!.GetValue(settings)!,
                        $"{name} does not default to on");
    }
}
