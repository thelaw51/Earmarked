using Earmarked.Core.Books;
using Earmarked.Core.Data;

namespace Earmarked.Tests;

public class BookRankingTests
{
    [Fact]
    public void Ranks_by_podcasts_then_episodes_then_latest_mention()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid();
        Guid show1 = Guid.NewGuid(), show2 = Guid.NewGuid();
        var day = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        MentionFact Fact(Guid book, Guid show, int episode, int days, MentionKind kind = MentionKind.Passing) =>
            new(book, new Guid(episode, 0, 0, new byte[8]), show, day.AddDays(days), kind);

        var ranked = BookRanking.Rank(
        [
            // a: three episodes of one show, all recommending it.
            Fact(a, show1, 1, 1, MentionKind.Recommended), Fact(a, show1, 2, 2, MentionKind.Recommended), Fact(a, show1, 3, 3, MentionKind.Recommended),
            // b: two shows, so it ranks first despite fewer episodes. Two mentions in episode 4 count once.
            Fact(b, show1, 4, 1), Fact(b, show1, 4, 1), Fact(b, show2, 5, 2),
            // c: one show, three episodes like a, but mentioned more recently.
            Fact(c, show2, 6, 1), Fact(c, show2, 7, 2), Fact(c, show2, 8, 9),
        ]);

        Assert.Equal([b, c, a], ranked.Select(r => r.BookId));
        Assert.Equal((2, 2), (ranked[0].Podcasts, ranked[0].Episodes));
        Assert.Equal(3, ranked[2].RecommendedIn);
    }
}
