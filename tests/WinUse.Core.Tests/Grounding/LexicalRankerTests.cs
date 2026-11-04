using WinUse.Core.Grounding;
using static WinUse.Core.Tests.Grounding.G;

namespace WinUse.Core.Tests.Grounding;

public class LexicalRankerTests
{
    [Fact]
    public void Rank_PutsTheLabelNamedInTheIntentFirst()
    {
        var r = LexicalRanker.Rank("click the Save button", [Cand("u1", "Cancel"), Cand("u2", "Save"), Cand("u3", "Save As")], 5);
        Assert.Equal("u2", r[0].Candidate.Id);
        Assert.Equal("u3", r[1].Candidate.Id);
        Assert.Equal("u1", r[2].Candidate.Id);
        Assert.True(r[0].Probability > r[1].Probability);
        Assert.Equal(0, r[2].Probability);
    }

    [Fact]
    public void Rank_ExactLabelScoresOne_UnrelatedScoresZero()
    {
        Assert.Equal(1, LexicalRanker.Score(Cand("u1", "Save"), "save"));
        Assert.Equal(0, LexicalRanker.Score(Cand("u1", "Cancel"), "save the file"));
        Assert.Equal(0, LexicalRanker.Score(Cand("u1", "Save"), ""));
    }

    [Fact]
    public void Rank_MatchesJapaneseWithoutWordSpaces()
    {
        var r = LexicalRanker.Rank("保存ボタンを押す", [Cand("u1", "キャンセル"), Cand("u2", "保存"), Cand("u3", "名前を付けて保存")], 5);
        Assert.Equal("u2", r[0].Candidate.Id);
        Assert.Equal("u3", r[1].Candidate.Id);
        Assert.Equal(0, r[2].Probability);
    }

    [Fact]
    public void Rank_RoleWordOnlyBreaksTies()
    {
        var button = Cand("u1", "Search", role: "Button");
        var field = Cand("u2", "Search", role: "Edit");
        Assert.True(LexicalRanker.Score(button, "the search button") > LexicalRanker.Score(field, "the search button"));
        Assert.True(LexicalRanker.Score(field, "the search box") > LexicalRanker.Score(button, "the search box"));
        // A role word alone matches nothing: every button would otherwise score for "click the button".
        Assert.Equal(0, LexicalRanker.Score(Cand("u3", "Cancel", role: "Button"), "click the button"));
    }

    [Fact]
    public void Rank_ConsidersTheValueAndRespectsTopK()
    {
        var combo = new Candidate("u1", "ComboBox", "Font", "Consolas", true, false, true, null, "top-left", null);
        Assert.True(LexicalRanker.Score(combo, "the Consolas dropdown") > 0.5);
        var r = LexicalRanker.Rank("x", [Cand("u1", "a"), Cand("u2", "b"), Cand("u3", "c")], 2);
        Assert.Equal(2, r.Count);
    }

    [Fact]
    public void Rank_IgnoresCaseAndWidth()
    {
        Assert.Equal(1, LexicalRanker.Score(Cand("u1", "ＯＫ"), "ok"));
        Assert.True(LexicalRanker.Score(Cand("u1", "Open File..."), "OPEN FILE") >= 0.9);
    }
}
