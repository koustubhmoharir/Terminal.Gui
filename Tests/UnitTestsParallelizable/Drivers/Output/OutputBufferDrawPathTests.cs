using System.Text;

namespace DriverTests.Output;

/// <summary>
///     Pins the observable output of <see cref="OutputBufferImpl"/> AddStr and FillRect for the cases the
///     per-cell draw path must keep identical: combining graphemes, control characters, multi-rectangle clips
///     and wide runes at clip edges.
/// </summary>
[Collection ("Driver Tests")]
public class OutputBufferDrawPathTests
{
    private static OutputBufferImpl CreateBuffer (int rows = 5, int cols = 10)
    {
        return new ()
        {
            Rows = rows, Cols = cols,
            CurrentAttribute = new (Color.White, Color.Black)
        };
    }

    [Fact]
    [Trait ("Category", "Output")]
    public void AddStr_Letter_With_Combining_Mark_Is_One_Grapheme_In_One_Cell ()
    {
        // Claude - Haiku 5.5
        OutputBufferImpl buffer = CreateBuffer ();

        buffer.Move (0, 0);
        buffer.AddStr ("éx");

        Assert.Equal ("é", buffer.Contents! [0, 0].Grapheme);
        Assert.Equal ("x", buffer.Contents [0, 1].Grapheme);
        Assert.Equal (" ", buffer.Contents [0, 2].Grapheme);
        Assert.Equal (2, buffer.Col);
    }

    [Fact]
    [Trait ("Category", "Output")]
    public void AddStr_Control_Characters_Render_As_Control_Pictures ()
    {
        // Claude - Haiku 5.5
        OutputBufferImpl buffer = CreateBuffer ();

        buffer.Move (0, 0);
        buffer.AddStr ("a\tb\u0007");

        Assert.Equal ("a", buffer.Contents! [0, 0].Grapheme);
        Assert.Equal ("␉", buffer.Contents [0, 1].Grapheme); // U+0009 -> U+2409 (symbol for horizontal tab)
        Assert.Equal ("b", buffer.Contents [0, 2].Grapheme);
        Assert.Equal ("␇", buffer.Contents [0, 3].Grapheme); // U+0007 -> U+2407 (symbol for bell)
        Assert.Equal (4, buffer.Col);
    }

    [Fact]
    [Trait ("Category", "Output")]
    public void AddStr_Clipped_To_Region_With_Hole_Writes_Only_Inside_Clip ()
    {
        // Claude - Haiku 5.5
        OutputBufferImpl buffer = CreateBuffer ();

        // Two rectangles: the screen minus a hole at columns 2-4 of row 1
        buffer.Clip = new (new Rectangle (0, 0, 10, 5));
        buffer.Clip.Exclude (new Rectangle (2, 1, 3, 1));

        buffer.Move (0, 1);
        buffer.AddStr ("abcdefghij");

        Assert.Equal ("a", buffer.Contents! [1, 0].Grapheme);
        Assert.Equal ("b", buffer.Contents [1, 1].Grapheme);
        Assert.Equal (" ", buffer.Contents [1, 2].Grapheme);
        Assert.Equal (" ", buffer.Contents [1, 3].Grapheme);
        Assert.Equal (" ", buffer.Contents [1, 4].Grapheme);
        Assert.Equal ("f", buffer.Contents [1, 5].Grapheme);
        Assert.Equal ("j", buffer.Contents [1, 9].Grapheme);
        Assert.Equal (10, buffer.Col);
    }

    [Fact]
    [Trait ("Category", "Output")]
    public void AddStr_Partially_Outside_Clip_Writes_Only_Inside_Clip ()
    {
        // Claude - Haiku 5.5
        OutputBufferImpl buffer = CreateBuffer ();

        // Clip is columns 0-5 of the screen, with a hole at columns 2-3 of row 0
        buffer.Clip = new (new Rectangle (0, 0, 6, 3));
        buffer.Clip.Exclude (new Rectangle (2, 0, 2, 1));

        buffer.Move (0, 0);
        buffer.AddStr ("abcdefgh");

        Assert.Equal ("a", buffer.Contents! [0, 0].Grapheme);
        Assert.Equal ("b", buffer.Contents [0, 1].Grapheme);
        Assert.Equal (" ", buffer.Contents [0, 2].Grapheme);
        Assert.Equal (" ", buffer.Contents [0, 3].Grapheme);
        Assert.Equal ("e", buffer.Contents [0, 4].Grapheme);
        Assert.Equal ("f", buffer.Contents [0, 5].Grapheme);
        Assert.Equal (" ", buffer.Contents [0, 6].Grapheme);
        Assert.Equal (8, buffer.Col);
    }

    [Fact]
    [Trait ("Category", "Output")]
    public void FillRect_Clipped_To_Region_With_Hole_Writes_Only_Inside_Clip ()
    {
        // Claude - Haiku 5.5
        OutputBufferImpl buffer = CreateBuffer ();

        buffer.Clip = new (new Rectangle (0, 0, 10, 5));
        buffer.Clip.Exclude (new Rectangle (2, 1, 3, 1));

        buffer.FillRect (new Rectangle (0, 0, 10, 3), new Rune ('X'));

        for (var c = 0; c < 10; c++)
        {
            Assert.Equal ("X", buffer.Contents! [0, c].Grapheme);
            Assert.Equal ("X", buffer.Contents [2, c].Grapheme);
        }

        Assert.Equal ("X", buffer.Contents [1, 0].Grapheme);
        Assert.Equal ("X", buffer.Contents [1, 1].Grapheme);
        Assert.Equal (" ", buffer.Contents [1, 2].Grapheme);
        Assert.Equal (" ", buffer.Contents [1, 3].Grapheme);
        Assert.Equal (" ", buffer.Contents [1, 4].Grapheme);
        Assert.Equal ("X", buffer.Contents [1, 5].Grapheme);
        Assert.Equal ("X", buffer.Contents [1, 9].Grapheme);
    }

    [Fact]
    [Trait ("Category", "Output")]
    public void FillRect_Partially_Outside_Clip_Writes_Only_Inside_Clip ()
    {
        // Claude - Haiku 5.5
        OutputBufferImpl buffer = CreateBuffer ();

        // Clip is columns 0-3 of rows 0-1 plus columns 0-7 of row 2 (two rectangles)
        buffer.Clip = new (new Rectangle (0, 0, 4, 2));
        buffer.Clip.Combine (new Rectangle (0, 2, 8, 1), RegionOp.Union);

        buffer.FillRect (new Rectangle (2, 0, 8, 3), new Rune ('X'));

        Assert.Equal (" ", buffer.Contents! [0, 1].Grapheme);
        Assert.Equal ("X", buffer.Contents [0, 2].Grapheme);
        Assert.Equal ("X", buffer.Contents [0, 3].Grapheme);
        Assert.Equal (" ", buffer.Contents [0, 4].Grapheme);
        Assert.Equal ("X", buffer.Contents [2, 2].Grapheme);
        Assert.Equal ("X", buffer.Contents [2, 7].Grapheme);
        Assert.Equal (" ", buffer.Contents [2, 8].Grapheme);
        Assert.Equal (" ", buffer.Contents [3, 2].Grapheme);
    }

    [Fact]
    [Trait ("Category", "Output")]
    public void FillRect_WideRune_At_Right_Edge_Of_Screen_Is_Skipped_When_It_Does_Not_Fit ()
    {
        // Claude - Haiku 5.5
        OutputBufferImpl buffer = CreateBuffer ();

        // Column 8 has room for both columns of the wide rune; column 9 does not
        buffer.FillRect (new Rectangle (8, 1, 2, 1), new Rune ('你'));

        Assert.Equal ("你", buffer.Contents! [1, 8].Grapheme);
        Assert.Equal (" ", buffer.Contents [1, 9].Grapheme);
    }

    [Fact]
    [Trait ("Category", "Output")]
    public void FillRect_WideRune_At_Right_Edge_Of_Clip_Uses_Column1Replacement ()
    {
        // Claude - Haiku 5.5
        OutputBufferImpl buffer = CreateBuffer ();

        buffer.Clip = new (new Rectangle (0, 0, 5, 5));

        buffer.FillRect (new Rectangle (3, 2, 3, 1), new Rune ('你'));

        // Column 3 holds the wide rune; column 4 is the last clipped column, so the rune there
        // cannot fit and becomes the column-1 replacement; column 5 is outside the clip and untouched.
        Assert.Equal (Glyphs.WideGlyphReplacement.ToString (), buffer.Contents! [2, 3].Grapheme);
        Assert.Equal (Glyphs.WideGlyphReplacement.ToString (), buffer.Contents [2, 4].Grapheme);
        Assert.Equal (" ", buffer.Contents [2, 5].Grapheme);
    }
}
