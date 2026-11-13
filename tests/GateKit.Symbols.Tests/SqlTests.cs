namespace GateKit.Symbols.Tests;

public class SqlTests
{
    [Fact]
    public void LikeEscapesWildcards() => Assert.Equal("%a\\_b\\%c%", Sql.Like("a_b%c"));

    [Fact]
    public void QuestionMarksBindInOrder()
    {
        using var db = Sql.OpenWrite(":memory:");
        db.Execute("CREATE TABLE t (a INTEGER, b TEXT)");
        db.Execute("INSERT INTO t VALUES (?, ?)", 1, "x?y");
        db.Execute("INSERT INTO t VALUES (?, ?)", 2, null);
        Assert.Equal("x?y", db.Scalar("SELECT b FROM t WHERE a = ? AND b <> '?'", 1));
        Assert.Null(db.Scalar("SELECT b FROM t WHERE a = ?", 2));
    }
}
