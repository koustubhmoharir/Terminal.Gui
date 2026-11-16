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
}
