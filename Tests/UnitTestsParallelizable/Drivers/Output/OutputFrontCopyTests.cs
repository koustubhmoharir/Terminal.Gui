using System.Text;
using System.Text.RegularExpressions;

namespace DriverTests.Output;

// Tests for the front copy: dirty cells whose content already matches what the terminal shows are not resent.
[Collection ("Driver Tests")]
public class OutputFrontCopyTests
{
    // Matches CSI, OSC and ST sequences so that only the text the terminal would display remains.
    private static readonly Regex _escapeSequence = new (@"\x1b\[[0-9;?]*[@-~]|\x1b\][^\x07\x1b]*(\x07|\x1b\\)|\x1b\\");
    private static readonly Regex _cursorMove = new (@"\x1b\[\d+;\d+H");

    // Captures bytes written to a non-degraded AnsiOutput so the emitted frame can be inspected.
    private static AnsiOutput CreateTerminal (StringBuilder bytes, AppModel appModel = AppModel.FullScreen)
    {
        return new AnsiOutput (chunk =>
                               {
                                   bytes.Append (Encoding.UTF8.GetString (chunk));

                                   return true;
                               },
                               appModel);
    }

    private static OutputBufferImpl CreateBuffer (int cols, int rows)
    {
        OutputBufferImpl buffer = new ();
        buffer.SetSize (cols, rows);

        return buffer;
    }

    private static void Draw (IOutputBuffer buffer, int col, int row, string text)
    {
        buffer.Move (col, row);
        buffer.AddStr (text);
    }

    // Changes one cell without dirtying its neighbour (AddStr also dirties the next cell), so each test
    // controls exactly which cells are dirty.
    private static void SetCell (IOutputBuffer buffer, int col, int row, string grapheme)
    {
        buffer.Contents! [row, col].Grapheme = grapheme;
        buffer.Contents [row, col].IsDirty = true;
        buffer.DirtyLines [row] = true;
    }

    private static void MarkAllDirty (IOutputBuffer buffer)
    {
        for (int row = 0; row < buffer.Rows; row++)
        {
            buffer.DirtyLines [row] = true;

            for (int col = 0; col < buffer.Cols; col++)
            {
                buffer.Contents! [row, col].IsDirty = true;
            }
        }
    }

    private static string WriteFrame (AnsiOutput output, StringBuilder bytes, IOutputBuffer buffer)
    {
        bytes.Clear ();
        output.Write (buffer);

        return bytes.ToString ();
    }

    private static string VisibleText (string terminalBytes) => _escapeSequence.Replace (terminalBytes, string.Empty);

    private static int CursorMoves (string terminalBytes) => _cursorMove.Matches (terminalBytes).Count;

    // Claude - Haiku 5.5
    [Fact]
    public void Write_RedrawOfUnchangedCells_EmitsNoCellText ()
    {
        // Arrange
        StringBuilder bytes = new ();
        AnsiOutput output = CreateTerminal (bytes);
        OutputBufferImpl buffer = CreateBuffer (8, 1);
        Draw (buffer, 0, 0, "abcdefgh");
        output.Write (buffer);

        MarkAllDirty (buffer);

        // Act
        string frame = WriteFrame (output, bytes, buffer);

        // Assert
        Assert.Equal (string.Empty, VisibleText (frame));
    }

    // Claude - Haiku 5.5
    [Fact]
    public void Write_ChangedCellInMiddleOfRow_EmitsOnlyThatCellWithAtMostOneCursorMove ()
    {
        // Arrange
        StringBuilder bytes = new ();
        AnsiOutput output = CreateTerminal (bytes);
        OutputBufferImpl buffer = CreateBuffer (10, 1);
        Draw (buffer, 0, 0, "abcdefghij");
        output.Write (buffer);

        SetCell (buffer, 4, 0, "X");
        MarkAllDirty (buffer);

        // Act
        string frame = WriteFrame (output, bytes, buffer);

        // Assert
        Assert.Equal ("X", VisibleText (frame));
        Assert.True (CursorMoves (frame) <= 1);
    }

    // Claude - Haiku 5.5
    [Fact]
    public void Write_TwoChangedCellsThreeApart_EmitsOneRunWithGapFilled ()
    {
        // Arrange
        StringBuilder bytes = new ();
        AnsiOutput output = CreateTerminal (bytes);
        OutputBufferImpl buffer = CreateBuffer (10, 1);
        Draw (buffer, 0, 0, "abcdefghij");
        output.Write (buffer);

        // Only the two changed cells are dirty; the two cells between them are clean.
        SetCell (buffer, 2, 0, "X");
        SetCell (buffer, 5, 0, "Y");

        // Act
        string frame = WriteFrame (output, bytes, buffer);

        // Assert: the unchanged cells 3 and 4 are emitted inside the run, so there is one cursor move.
        Assert.Equal ("XdeY", VisibleText (frame));
        Assert.Equal (1, CursorMoves (frame));
    }

    // Claude - Haiku 5.5
    [Fact]
    public void Write_TwoChangedCellsThirtyApart_EmitsTwoRuns ()
    {
        // Arrange
        StringBuilder bytes = new ();
        AnsiOutput output = CreateTerminal (bytes);
        OutputBufferImpl buffer = CreateBuffer (40, 1);
        Draw (buffer, 0, 0, "abcdefghijabcdefghijabcdefghijabcdefghij");
        output.Write (buffer);

        SetCell (buffer, 2, 0, "X");
        SetCell (buffer, 32, 0, "Y");

        // Act
        string frame = WriteFrame (output, bytes, buffer);

        // Assert: the gap is too long to fill, so each changed cell is its own run.
        Assert.Equal ("XY", VisibleText (frame));
        Assert.Equal (2, CursorMoves (frame));
    }

    // Claude - Haiku 5.5
    [Fact]
    public void Write_AfterClearContents_ResendsEveryCell ()
    {
        // Arrange
        StringBuilder bytes = new ();
        AnsiOutput output = CreateTerminal (bytes);
        OutputBufferImpl buffer = CreateBuffer (8, 1);
        Draw (buffer, 0, 0, "abcdefgh");
        output.Write (buffer);

        buffer.ClearContents ();
        Draw (buffer, 0, 0, "abcdefgh");

        // Act
        string frame = WriteFrame (output, bytes, buffer);

        // Assert: the buffer has a new Contents array, so the unchanged text is sent again.
        Assert.Equal ("abcdefgh", VisibleText (frame));
    }

    // Claude - Haiku 5.5
    [Fact]
    public void Write_AfterSetSizeWithSameDimensions_ResendsEveryCell ()
    {
        // Arrange
        StringBuilder bytes = new ();
        AnsiOutput output = CreateTerminal (bytes);
        OutputBufferImpl buffer = CreateBuffer (8, 1);
        Draw (buffer, 0, 0, "abcdefgh");
        output.Write (buffer);

        buffer.SetSize (8, 1);
        Draw (buffer, 0, 0, "abcdefgh");

        // Act
        string frame = WriteFrame (output, bytes, buffer);

        // Assert
        Assert.Equal ("abcdefgh", VisibleText (frame));
    }

    // Claude - Haiku 5.5
    [Fact]
    public void Write_AfterInvalidatePhysicalState_ResendsEveryCell ()
    {
        // Arrange
        StringBuilder bytes = new ();
        AnsiOutput output = CreateTerminal (bytes);
        OutputBufferImpl buffer = CreateBuffer (8, 1);
        Draw (buffer, 0, 0, "abcdefgh");
        output.Write (buffer);

        output.InvalidatePhysicalState (buffer);
        MarkAllDirty (buffer);

        // Act
        string frame = WriteFrame (output, bytes, buffer);

        // Assert
        Assert.Equal ("abcdefgh", VisibleText (frame));
    }

    // Claude - Haiku 5.5
    [Fact]
    public void Write_UnchangedWideGlyph_IsStillEmitted ()
    {
        // Arrange
        StringBuilder bytes = new ();
        AnsiOutput output = CreateTerminal (bytes);
        OutputBufferImpl buffer = CreateBuffer (5, 1);
        Draw (buffer, 0, 0, "🦮AB");
        output.Write (buffer);

        MarkAllDirty (buffer);

        // Act
        string frame = WriteFrame (output, bytes, buffer);

        // Assert: wide glyphs are never skipped.
        Assert.Contains ("🦮", VisibleText (frame));

        // Arrange: change the width-1 cell after the glyph.
        SetCell (buffer, 2, 0, "C");
        MarkAllDirty (buffer);

        // Act
        frame = WriteFrame (output, bytes, buffer);

        // Assert: the glyph is redrawn and the changed cell follows it on the same run.
        Assert.Contains ("🦮C", VisibleText (frame));
    }

    // Claude - Haiku 5.5
    [Fact]
    public void Write_RowWithUrl_IsNotSkipped ()
    {
        // Arrange
        StringBuilder bytes = new ();
        AnsiOutput output = CreateTerminal (bytes);
        OutputBufferImpl buffer = CreateBuffer (5, 1);
        buffer.CurrentUrl = "https://example.com";
        Draw (buffer, 0, 0, "Hello");
        buffer.CurrentUrl = null;
        output.Write (buffer);

        MarkAllDirty (buffer);

        // Act
        string frame = WriteFrame (output, bytes, buffer);

        // Assert
        Assert.Equal ("Hello", VisibleText (frame));
    }

    // Claude - Haiku 5.5
    [Fact]
    public void Write_LegacyConsole_ResendsUnchangedCells ()
    {
        // Arrange
        StringBuilder bytes = new ();
        AnsiOutput output = CreateTerminal (bytes);
        output.IsLegacyConsole = true;
        OutputBufferImpl buffer = CreateBuffer (8, 1);
        Draw (buffer, 0, 0, "abcdefgh");
        output.Write (buffer);

        MarkAllDirty (buffer);

        // Act
        string frame = WriteFrame (output, bytes, buffer);

        // Assert
        // Byte-for-byte what the output sent before the front copy existed.
        Assert.Equal ("\u001b[1;1H\u001b[30m\u001b[40mabcdefgh", frame);
    }

    // Claude - Haiku 5.5
    [Fact]
    public void Write_InlineMode_ResendsUnchangedCells ()
    {
        // Arrange
        StringBuilder bytes = new ();
        AnsiOutput output = CreateTerminal (bytes, AppModel.Inline);
        OutputBufferImpl buffer = CreateBuffer (8, 1);
        Draw (buffer, 0, 0, "abcdefgh");
        output.Write (buffer);

        MarkAllDirty (buffer);

        // Act
        string frame = WriteFrame (output, bytes, buffer);

        // Assert
        // Byte-for-byte what the output sent before the front copy existed.
        Assert.Equal ("\u001b[1;1H\u001b[38;2;0;0;0m\u001b[48;2;0;0;0mabcdefgh", frame);
    }

    // Claude - Haiku 5.5
    [Fact]
    public void Write_DegradedPlatform_ResendsUnchangedCells ()
    {
        // Arrange: a default AnsiOutput is degraded when no terminal is attached, as in tests.
        AnsiOutput output = new ();
        IOutputBuffer buffer = output.GetLastBuffer ()!;
        buffer.SetSize (8, 1);
        Draw (buffer, 0, 0, "abcdefgh");
        output.Write (buffer);

        MarkAllDirty (buffer);

        // Act
        output.Write (buffer);

        // Assert
        Assert.Contains ("abcdefgh", output.GetLastOutput ());
    }

    // Matches one CSI sequence at the position where the search starts.
    private static readonly Regex _csiAt = new (@"\G\x1b\[([0-9;?]*)([@-~])");

    // A ten-row, four-column screen where every row is "r<row>" padded with spaces.
    private static OutputBufferImpl CreateRowsBuffer ()
    {
        OutputBufferImpl buffer = CreateBuffer (4, 10);

        for (int row = 0; row < 10; row++)
        {
            DrawRow (buffer, row, $"r{row}");
        }

        return buffer;
    }

    private static void DrawRow (IOutputBuffer buffer, int row, string text) => Draw (buffer, 0, row, text.PadRight (buffer.Cols));

    private static string [,] BlankScreen (int rows, int cols)
    {
        string [,] screen = new string [rows, cols];

        for (int row = 0; row < rows; row++)
        {
            for (int col = 0; col < cols; col++)
            {
                screen [row, col] = " ";
            }
        }

        return screen;
    }

    private static string [,] ScreenOf (IOutputBuffer buffer)
    {
        string [,] screen = new string [buffer.Rows, buffer.Cols];

        for (int row = 0; row < buffer.Rows; row++)
        {
            for (int col = 0; col < buffer.Cols; col++)
            {
                screen [row, col] = buffer.Contents! [row, col].Grapheme;
            }
        }

        return screen;
    }

    private static string ScreenText (string [,] screen)
    {
        StringBuilder text = new ();

        for (int row = 0; row < screen.GetLength (0); row++)
        {
            for (int col = 0; col < screen.GetLength (1); col++)
            {
                text.Append (screen [row, col]);
            }

            text.Append ('\n');
        }

        return text.ToString ();
    }

    private static int [] ParseNumbers (string parameters) => parameters.Length == 0 ? [] : parameters.Split (';').Select (int.Parse).ToArray ();

    // Replays one frame onto a screen of graphemes the way a terminal shows it. Handles the sequences the output writes:
    // cursor position, scroll region (DECSTBM), scroll up (SU), and text. SGR changes no grapheme.
    private static void Replay (string [,] screen, string frame)
    {
        int rows = screen.GetLength (0);
        int cols = screen.GetLength (1);
        int top = 0;
        int bottom = rows - 1;
        int row = 0;
        int col = 0;
        int index = 0;

        while (index < frame.Length)
        {
            if (frame [index] != '\u001b')
            {
                screen [row, col] = frame [index].ToString ();
                col++;
                index++;

                continue;
            }

            Match csi = _csiAt.Match (frame, index);

            if (!csi.Success)
            {
                throw new NotSupportedException ("Replay supports only CSI sequences.");
            }

            index += csi.Length;
            char final = csi.Groups [2].Value [0];
            int [] numbers = ParseNumbers (csi.Groups [1].Value);

            if (final == 'm')
            {
                continue;
            }

            if (final == 'H')
            {
                row = (numbers.Length > 0 ? numbers [0] : 1) - 1;
                col = (numbers.Length > 1 ? numbers [1] : 1) - 1;

                continue;
            }

            if (final == 'r')
            {
                top = numbers.Length == 0 ? 0 : numbers [0] - 1;
                bottom = numbers.Length == 0 ? rows - 1 : numbers [1] - 1;
                row = 0;
                col = 0;

                continue;
            }

            if (final == 'S')
            {
                int count = numbers.Length == 0 ? 1 : numbers [0];

                for (int scroll = 0; scroll < count; scroll++)
                {
                    for (int scrollRow = top; scrollRow < bottom; scrollRow++)
                    {
                        for (int scrollCol = 0; scrollCol < cols; scrollCol++)
                        {
                            screen [scrollRow, scrollCol] = screen [scrollRow + 1, scrollCol];
                        }
                    }

                    for (int scrollCol = 0; scrollCol < cols; scrollCol++)
                    {
                        screen [bottom, scrollCol] = " ";
                    }
                }

                continue;
            }

            throw new NotSupportedException ($"Replay does not support CSI {final}.");
        }
    }

    private const string ScrollTwoRowsSequence = "\u001b[3;8r\u001b[2S\u001b[r";

    // Claude - Haiku 5.5
    [Fact]
    public void ScrollRowsUp_BandRedrawn_EmitsScrollBeforeCellsAndOnlyNewRowsAreSent ()
    {
        // Arrange
        StringBuilder bytes = new ();
        AnsiOutput output = CreateTerminal (bytes);
        OutputBufferImpl buffer = CreateRowsBuffer ();
        string [,] screen = BlankScreen (10, 4);
        Replay (screen, WriteFrame (output, bytes, buffer));

        // Rows 2..5 now hold what rows 4..7 held; rows 6 and 7 hold new text.
        buffer.ScrollRowsUp (2, 6, 2);

        for (int row = 2; row < 6; row++)
        {
            DrawRow (buffer, row, $"r{row + 2}");
        }

        DrawRow (buffer, 6, "n6");
        DrawRow (buffer, 7, "n7");

        // Act
        string frame = WriteFrame (output, bytes, buffer);

        // Assert
        // DECSTBM over rows 3..8 (1-based), SU 2, then the margins are reset. All of it precedes the first cell.
        Assert.StartsWith (ScrollTwoRowsSequence, frame, StringComparison.Ordinal);

        // The shifted rows already show on the terminal after the scroll, so only the new rows are sent.
        Assert.Equal ("n6  n7  ", VisibleText (frame));

        Replay (screen, frame);
        Assert.Equal (ScreenText (ScreenOf (buffer)), ScreenText (screen));
    }

    // Claude - Haiku 5.5
    [Fact]
    public void ScrollRowsUp_BandNotRedrawn_CorrectsShiftedRowsOnScreen ()
    {
        // Arrange
        StringBuilder bytes = new ();
        AnsiOutput output = CreateTerminal (bytes);
        OutputBufferImpl buffer = CreateRowsBuffer ();
        string [,] screen = BlankScreen (10, 4);
        Replay (screen, WriteFrame (output, bytes, buffer));

        // The caller asks for the scroll but does not draw the band, so Contents still holds the old rows.
        buffer.ScrollRowsUp (2, 6, 2);

        // Act
        string frame = WriteFrame (output, bytes, buffer);

        // Assert
        Assert.StartsWith (ScrollTwoRowsSequence, frame, StringComparison.Ordinal);

        // The scroll put r4..r7 on rows 2..5, which differ from Contents. Replaying the frame must restore r2..r7.
        Replay (screen, frame);
        Assert.Equal (ScreenText (ScreenOf (buffer)), ScreenText (screen));
    }

    // Claude - Haiku 5.5
    [Fact]
    public void ScrollRowsUp_RowsOutsideBand_AreNotAffected ()
    {
        // Arrange
        StringBuilder bytes = new ();
        AnsiOutput output = CreateTerminal (bytes);
        OutputBufferImpl buffer = CreateRowsBuffer ();
        string [,] screen = BlankScreen (10, 4);
        Replay (screen, WriteFrame (output, bytes, buffer));

        buffer.ScrollRowsUp (2, 6, 2);

        for (int row = 2; row < 6; row++)
        {
            DrawRow (buffer, row, $"r{row + 2}");
        }

        DrawRow (buffer, 6, "n6");
        DrawRow (buffer, 7, "n7");

        // A changed cell above the band, then every row marked dirty: rows 8 and 9 are unchanged and must not be sent.
        SetCell (buffer, 1, 0, "X");
        MarkAllDirty (buffer);

        // Act
        string frame = WriteFrame (output, bytes, buffer);

        // Assert
        Assert.Equal ("Xn6  n7  ", VisibleText (frame));

        Replay (screen, frame);
        Assert.Equal (ScreenText (ScreenOf (buffer)), ScreenText (screen));
    }

    // Claude - Haiku 5.5
    [Fact]
    public void ScrollRowsUp_TwoRequestsInOneFrame_AreAppliedInOrder ()
    {
        // Arrange
        StringBuilder bytes = new ();
        AnsiOutput output = CreateTerminal (bytes);
        OutputBufferImpl buffer = CreateRowsBuffer ();
        string [,] screen = BlankScreen (10, 4);
        Replay (screen, WriteFrame (output, bytes, buffer));

        // Two scrolls by two rows move rows 6 and 7 to rows 2 and 3. Rows 4..7 hold new text.
        buffer.ScrollRowsUp (2, 6, 2);
        buffer.ScrollRowsUp (2, 6, 2);
        DrawRow (buffer, 2, "r6");
        DrawRow (buffer, 3, "r7");
        DrawRow (buffer, 4, "n4");
        DrawRow (buffer, 5, "n5");
        DrawRow (buffer, 6, "n6");
        DrawRow (buffer, 7, "n7");

        // Act
        string frame = WriteFrame (output, bytes, buffer);

        // Assert
        Assert.StartsWith (ScrollTwoRowsSequence + ScrollTwoRowsSequence, frame, StringComparison.Ordinal);
        Assert.Equal ("n4  n5  n6  n7  ", VisibleText (frame));

        Replay (screen, frame);
        Assert.Equal (ScreenText (ScreenOf (buffer)), ScreenText (screen));
    }

    // Writes a ten-row frame, then a second frame in which rows 2..5 hold what rows 4..7 held and rows 6 and 7 hold new text.
    // Returns the bytes of the second frame.
    private static string RenderScrolledFrame (AppModel appModel, bool legacy, bool requestScroll)
    {
        StringBuilder bytes = new ();
        AnsiOutput output = CreateTerminal (bytes, appModel);
        output.IsLegacyConsole = legacy;
        OutputBufferImpl buffer = CreateRowsBuffer ();
        WriteFrame (output, bytes, buffer);

        if (requestScroll)
        {
            buffer.ScrollRowsUp (2, 6, 2);
        }

        for (int row = 2; row < 6; row++)
        {
            DrawRow (buffer, row, $"r{row + 2}");
        }

        DrawRow (buffer, 6, "n6");
        DrawRow (buffer, 7, "n7");

        return WriteFrame (output, bytes, buffer);
    }

    // Claude - Haiku 5.5
    [Fact]
    public void ScrollRowsUp_InlineMode_EmitsNothingAndMatchesOutputWithoutRequest ()
    {
        // Arrange
        string withoutRequest = RenderScrolledFrame (AppModel.Inline, false, false);

        // Act
        string withRequest = RenderScrolledFrame (AppModel.Inline, false, true);

        // Assert
        Assert.Equal (withoutRequest, withRequest);
        Assert.DoesNotContain ("\u001b[2S", withRequest, StringComparison.Ordinal);
    }

    // Claude - Haiku 5.5
    [Fact]
    public void ScrollRowsUp_LegacyConsole_EmitsNothingAndMatchesOutputWithoutRequest ()
    {
        // Arrange
        string withoutRequest = RenderScrolledFrame (AppModel.FullScreen, true, false);

        // Act
        string withRequest = RenderScrolledFrame (AppModel.FullScreen, true, true);

        // Assert
        Assert.Equal (withoutRequest, withRequest);
        Assert.DoesNotContain ("\u001b[2S", withRequest, StringComparison.Ordinal);
    }

    // Claude - Haiku 5.5
    [Fact]
    public void ScrollRowsUp_AfterClearContents_IsDroppedAndEveryCellIsSent ()
    {
        // Arrange
        StringBuilder bytes = new ();
        AnsiOutput output = CreateTerminal (bytes);
        OutputBufferImpl buffer = CreateRowsBuffer ();
        WriteFrame (output, bytes, buffer);

        buffer.ScrollRowsUp (2, 6, 2);
        buffer.ClearContents ();

        for (int row = 0; row < 10; row++)
        {
            DrawRow (buffer, row, $"r{row}");
        }

        // Act
        string frame = WriteFrame (output, bytes, buffer);

        // Assert
        Assert.DoesNotContain ("\u001b[2S", frame, StringComparison.Ordinal);
        Assert.Equal (string.Concat (Enumerable.Range (0, 10).Select (row => $"r{row}  ")), VisibleText (frame));
    }

    // Claude - Haiku 5.5
    [Fact]
    public void ScrollRowsUp_AfterSetSize_IsDroppedAndEveryCellIsSent ()
    {
        // Arrange
        StringBuilder bytes = new ();
        AnsiOutput output = CreateTerminal (bytes);
        OutputBufferImpl buffer = CreateRowsBuffer ();
        WriteFrame (output, bytes, buffer);

        buffer.ScrollRowsUp (2, 6, 2);
        buffer.SetSize (4, 10);

        for (int row = 0; row < 10; row++)
        {
            DrawRow (buffer, row, $"r{row}");
        }

        // Act
        string frame = WriteFrame (output, bytes, buffer);

        // Assert
        Assert.DoesNotContain ("\u001b[2S", frame, StringComparison.Ordinal);
        Assert.Equal (string.Concat (Enumerable.Range (0, 10).Select (row => $"r{row}  ")), VisibleText (frame));
    }

    // Claude - Haiku 5.5
    [Fact]
    public void ScrollRowsUp_InvalidArguments_ThrowArgumentOutOfRangeException ()
    {
        // Arrange
        OutputBufferImpl buffer = CreateBuffer (4, 10);

        // Act and Assert
        Assert.Throws<ArgumentOutOfRangeException> (() => buffer.ScrollRowsUp (0, 1, 1));   // height below two
        Assert.Throws<ArgumentOutOfRangeException> (() => buffer.ScrollRowsUp (0, 4, 4));   // lines equal to height
        Assert.Throws<ArgumentOutOfRangeException> (() => buffer.ScrollRowsUp (0, 4, 5));   // lines above height
        Assert.Throws<ArgumentOutOfRangeException> (() => buffer.ScrollRowsUp (0, 4, 0));   // no lines
        Assert.Throws<ArgumentOutOfRangeException> (() => buffer.ScrollRowsUp (0, 4, -1));  // negative lines
        Assert.Throws<ArgumentOutOfRangeException> (() => buffer.ScrollRowsUp (-1, 4, 1));  // negative top
        Assert.Throws<ArgumentOutOfRangeException> (() => buffer.ScrollRowsUp (8, 4, 1));   // band runs past the last row
        Assert.Throws<ArgumentOutOfRangeException> (() => buffer.ScrollRowsUp (10, 2, 1));  // band starts past the last row

        // The band that ends exactly on the last row is valid.
        buffer.ScrollRowsUp (6, 4, 1);
    }
}
