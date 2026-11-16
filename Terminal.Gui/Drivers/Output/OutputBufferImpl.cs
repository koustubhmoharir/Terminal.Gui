using System.Diagnostics;
using System.Text;

namespace Terminal.Gui.Drivers;

/// <summary>
///     Stores the desired output state for the whole application. This is updated during
///     draw operations before being flushed to the console as part of the main loop.
///     operation
/// </summary>
public class OutputBufferImpl : IOutputBuffer
{
    /// <summary>
    ///     Stable lock object for synchronizing access to <see cref="Contents"/>, <see cref="DirtyLines"/>,
    ///     <see cref="Clip"/>, and related state. Unlike locking on <c>Contents</c> itself, this object is
    ///     never replaced, guaranteeing mutual exclusion across <see cref="ClearContents()"/>,
    ///     <see cref="AddStr(string)"/>, and <see cref="FillRect(Rectangle, Rune)"/>.
    /// </summary>
    private readonly Lock _contentsLock = new ();

    private int _cols;
    private int _rows;

    /// <summary>
    ///     Maps cell positions to explicitly assigned URLs for OSC 8 hyperlink support.
    /// </summary>
    private Dictionary<Point, string>? _explicitUrlMap;

    /// <summary>
    ///     Maps cell positions to auto-detected URLs found in plain text content.
    /// </summary>
    private Dictionary<Point, string>? _autoUrlMap;

    private int _urlStateVersion;

    /// <summary>
    ///     Monotonic counter incremented when URL state is wiped (e.g. via
    ///     <see cref="ClearContents(bool)"/> or <see cref="SetSize"/>). Consumers
    ///     that cache per-frame URL-row tracking can compare to detect resets.
    /// </summary>
    internal int UrlStateVersion => _urlStateVersion;

    private Rune _column1ReplacementChar = Glyphs.WideGlyphReplacement;

    private Region? _clip;
    private readonly List<RasterImageCommand> _rasterImages = [];

    // Row scrolls requested since the last write, in request order. A full redraw (ClearContents, SetSize) discards them.
    private readonly List<(int Top, int Height, int Lines)> _pendingRowScrolls = [];

    /// <summary>
    ///     The contents of the application output. The driver outputs this buffer to the terminal when
    ///     UpdateScreen is called.
    ///     <remarks>The format of the array is rows, columns. The first index is the row, the second index is the column.</remarks>
    /// </summary>
    public Cell [,]? Contents { get; set; } = new Cell [0, 0];

    /// <summary>
    ///     The <see cref="Attribute"/> that will be used for the next <see cref="AddRune(Rune)"/> or <see cref="AddStr"/>
    ///     call.
    /// </summary>
    public Attribute CurrentAttribute { get; set; }

    /// <summary>
    ///     Gets or sets the URL that will be associated with cells added via <see cref="AddRune(Rune)"/> or
    ///     <see cref="AddStr(string)"/>.
    ///     When set, subsequent cells will include this URL for OSC 8 hyperlink rendering.
    /// </summary>
    public string? CurrentUrl { get; set; }

    /// <summary>
    ///     Gets the URL associated with the cell at the specified position.
    /// </summary>
    /// <param name="col">The column.</param>
    /// <param name="row">The row.</param>
    /// <returns>The URL if one exists, otherwise null.</returns>
    public string? GetCellUrl (int col, int row)
    {
        if (_explicitUrlMap is null && _autoUrlMap is null)
        {
            return null;
        }

        lock (_contentsLock)
        {
            Point point = new (col, row);

            if (_explicitUrlMap?.TryGetValue (point, out string? explicitUrl) == true)
            {
                return explicitUrl;
            }

            return _autoUrlMap?.TryGetValue (point, out string? autoUrl) == true ? autoUrl : null;
        }
    }

    /// <summary>The leftmost column in the terminal.</summary>
    public virtual int Left { get; set; } = 0;

    /// <summary>
    ///     Gets the row last set by <see cref="Move"/>. <see cref="Col"/> and <see cref="Row"/> are used by
    ///     <see cref="AddRune(Rune)"/> and <see cref="AddStr"/> to determine where to add content.
    /// </summary>
    public int Row { get; private set; }

    /// <summary>
    ///     Gets the column last set by <see cref="Move"/>. <see cref="Col"/> and <see cref="Row"/> are used by
    ///     <see cref="AddRune(Rune)"/> and <see cref="AddStr"/> to determine where to add content.
    /// </summary>
    public int Col { get; private set; }

    /// <summary>The number of rows visible in the terminal.</summary>
    public int Rows
    {
        get => _rows;
        set
        {
            _rows = value;
            ClearContents ();
            _rasterImages.Clear ();
        }
    }

    /// <summary>The number of columns visible in the terminal.</summary>
    public int Cols
    {
        get => _cols;
        set
        {
            _cols = value;
            ClearContents ();
            _rasterImages.Clear ();
        }
    }

    /// <summary>The topmost row in the terminal.</summary>
    public virtual int Top { get; set; } = 0;

    /// <inheritdoc/>
    public void SetWideGlyphReplacement (Rune column1ReplacementChar) => _column1ReplacementChar = column1ReplacementChar;

    /// <summary>
    ///     Indicates which lines have been modified and need to be redrawn.
    /// </summary>
    public bool [] DirtyLines { get; set; } = [];

    /// <summary>
    ///     Gets or sets the clip rectangle that <see cref="AddRune(Rune)"/> and <see cref="AddStr(string)"/> are subject
    ///     to.
    /// </summary>
    /// <value>The rectangle describing the of <see cref="Clip"/> region.</value>
    public Region? Clip
    {
        get => _clip;
        set
        {
            if (ReferenceEquals (_clip, value))
            {
                return;
            }

            _clip = value;

            // Don't ever let Clip be bigger than Screen
            _clip?.Intersect (Screen);
        }
    }

    /// <summary>Adds the specified rune to the display at the current cursor position.</summary>
    /// <remarks>
    ///     <para>
    ///         When the method returns, <see cref="Col"/> will be incremented by the number of columns
    ///         <paramref name="rune"/> required, even if the new column value is outside the <see cref="Clip"/> or screen
    ///         dimensions defined by <see cref="Cols"/>.
    ///     </para>
    ///     <para>
    ///         If <paramref name="rune"/> requires more than one column, and <see cref="Col"/> plus the number of columns
    ///         needed exceeds the <see cref="Clip"/> or screen dimensions, the default Unicode replacement character (U+FFFD)
    ///         will be added instead.
    ///     </para>
    /// </remarks>
    /// <param name="rune">Text to add.</param>
    public void AddRune (Rune rune) => AddStr (rune.ToString ());

    /// <summary>
    ///     Adds the specified <see langword="char"/> to the display at the current cursor position. This method is a
    ///     convenience method that calls <see cref="AddRune(Rune)"/> with the <see cref="Rune"/> constructor.
    /// </summary>
    /// <param name="c">Character to add.</param>
    public void AddRune (char c) => AddRune (new Rune (c));

    /// <summary>Adds the <paramref name="str"/> to the display at the cursor position.</summary>
    /// <remarks>
    ///     <para>
    ///         When the method returns, <see cref="Col"/> will be incremented by the number of columns
    ///         <paramref name="str"/> required, unless the new column value is outside the <see cref="Clip"/> or screen
    ///         dimensions defined by <see cref="Cols"/>.
    ///     </para>
    ///     <para>If <paramref name="str"/> requires more columns than are available, the output will be clipped.</para>
    /// </remarks>
    /// <param name="str">String.</param>
    public void AddStr (string str)
    {
        lock (_contentsLock)
        {
            if (Contents is null || string.IsNullOrEmpty (str))
            {
                return;
            }

            Clip ??= new Region (Screen);
            Rectangle clipRect = Clip!.GetBounds ();
            Rectangle [] clipRects = Clip!.GetRectangles ();

            if (IsPrintableAscii (str))
            {
                // Every printable ASCII char is its own grapheme, so skip the enumerator and its allocations.
                foreach (char c in str)
                {
                    AddGraphemeCore (AsciiGraphemes [c], clipRects, clipRect);
                }

                return;
            }

            foreach (string grapheme in GraphemeHelper.GetGraphemes (str))
            {
                AddGraphemeCore (grapheme, clipRects, clipRect);
            }
        }
    }

    /// <summary>Clears the <see cref="Contents"/> of the driver.</summary>
    public void ClearContents () => ClearContents (!InlineMode);

    /// <summary>Tests whether the specified coordinate are valid for drawing the specified Text.</summary>
    /// <param name="text">Used to determine if one or two columns are required.</param>
    /// <param name="col">The column.</param>
    /// <param name="row">The row.</param>
    /// <returns>
    ///     <see langword="false"/> if the coordinate is outside the screen bounds or outside of <see cref="Clip"/>.
    ///     <see langword="true"/> otherwise.
    /// </returns>
    public bool IsValidLocation (string text, int col, int row)
    {
        int textWidth = text.GetColumns ();

        return col >= 0 && row >= 0 && col + textWidth <= Cols && row < Rows && Clip!.Contains (col, row);
    }

    /// <inheritdoc/>
    public void SetSize (int cols, int rows)
    {
        lock (_contentsLock)
        {
            _cols = cols;
            _rows = rows;
            ClearContentsCore (!InlineMode);
            _rasterImages.Clear ();
        }
    }

    /// <inheritdoc/>
    public void FillRect (Rectangle rect, Rune rune)
    {
        lock (_contentsLock)
        {
            if (Contents is null)
            {
                return;
            }

            Clip ??= new Region (Screen);
            Rectangle clipBounds = Clip!.GetBounds ();
            Rectangle [] clipRects = Clip!.GetRectangles ();

            rect = Rectangle.Intersect (rect, clipBounds);

            // The same for every cell, so computed once. Validity uses the raw rune's width, as it always has;
            // drawing uses the printable grapheme's width.
            string rawText = rune.ToString ();
            int rawWidth = rawText.GetColumns ();
            string grapheme = rune != default (Rune) ? rawText.MakePrintable () : " ";
            int graphemeWidth = grapheme.GetColumns ();

            for (int r = rect.Y; r < rect.Y + rect.Height; r++)
            {
                for (int c = rect.X; c < rect.X + rect.Width; c++)
                {
                    if (!IsValidCell (rawWidth, c, r, clipRects))
                    {
                        continue;
                    }

                    // Inlined rather than calling a locking helper, since this method already holds the lock.
                    SetAttributeAndDirty (c, r);
                    InvalidateOverlappedWideGlyph (c, r);
                    WriteGraphemeByWidth (c, r, grapheme, graphemeWidth, clipBounds, clipRects);
                }
            }
        }
    }

    /// <inheritdoc/>
    public void FillRect (Rectangle rect, char rune)
    {
        FillRect (rect, new Rune (rune));
    }

    /// <summary>
    ///     Updates <see cref="Col"/> and <see cref="Row"/> to the specified column and row in <see cref="Contents"/>.
    ///     Used by <see cref="AddRune(Rune)"/> and <see cref="AddStr"/> to determine where to add content.
    /// </summary>
    /// <remarks>
    ///     <para>This does not move the cursor on the screen, it only updates the internal state of the driver.</para>
    ///     <para>
    ///         If <paramref name="col"/> or <paramref name="row"/> are negative or beyond  <see cref="Cols"/> and
    ///         <see cref="Rows"/>, the method still sets those properties.
    ///     </para>
    /// </remarks>
    /// <param name="col">Column to move to.</param>
    /// <param name="row">Row to move to.</param>
    public void Move (int col, int row)
    {
        Col = col;
        Row = row;
    }

    /// <inheritdoc/>
    public void AddRasterImage (RasterImageCommand command)
    {
        ArgumentNullException.ThrowIfNull (command);
        ArgumentException.ThrowIfNullOrEmpty (command.Id);

        if (command.Pixels is null)
        {
            throw new ArgumentException ("Raster image pixels are required.", nameof (command));
        }

        if (command.DestinationCells.Width <= 0 || command.DestinationCells.Height <= 0)
        {
            throw new ArgumentException ("Raster image destination must have a positive size.", nameof (command));
        }

        lock (_contentsLock)
        {
            command.Clip = Clip?.Clone ();
            int index = _rasterImages.FindIndex (existing => existing.Id == command.Id);

            if (index >= 0)
            {
                MarkRasterImageCellsDirty (_rasterImages [index]);
                MarkRasterImageCellsClean (command);
                command.NeedsTransparentCellClear = ClearCellsUnderRasterImage (command);
                _rasterImages [index] = command;

                return;
            }

            MarkRasterImageCellsClean (command);
            command.NeedsTransparentCellClear = ClearCellsUnderRasterImage (command);
            _rasterImages.Add (command);
        }
    }

    /// <inheritdoc/>
    public void RemoveRasterImage (string id)
    {
        ArgumentException.ThrowIfNullOrEmpty (id);

        lock (_contentsLock)
        {
            for (int i = _rasterImages.Count - 1; i >= 0; i--)
            {
                if (_rasterImages [i].Id != id)
                {

                    continue;
                }

                MarkRasterImageCellsDirty (_rasterImages [i]);
                _rasterImages.RemoveAt (i);
            }
        }
    }

    /// <inheritdoc/>
    public void RetainRasterImages (IReadOnlyCollection<string> activeIds)
    {
        ArgumentNullException.ThrowIfNull (activeIds);

        lock (_contentsLock)
        {
            for (int i = _rasterImages.Count - 1; i >= 0; i--)
            {
                if (_rasterImages [i].Id is { } id && activeIds.Contains (id))
                {
                    continue;
                }

                MarkRasterImageCellsDirty (_rasterImages [i]);
                _rasterImages.RemoveAt (i);
            }
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<RasterImageCommand> GetRasterImages ()
    {
        lock (_contentsLock)
        {
            return _rasterImages.ToArray ();
        }
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentOutOfRangeException">An argument is outside the range documented on <see cref="IOutputBuffer.ScrollRowsUp"/>.</exception>
    public void ScrollRowsUp (int top, int height, int lines)
    {
        lock (_contentsLock)
        {
            ArgumentOutOfRangeException.ThrowIfNegative (top);
            ArgumentOutOfRangeException.ThrowIfLessThan (height, 2);
            ArgumentOutOfRangeException.ThrowIfGreaterThan (height, _rows - top);
            ArgumentOutOfRangeException.ThrowIfLessThan (lines, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual (lines, height);

            _pendingRowScrolls.Add ((top, height, lines));
        }
    }

    // Hands the pending row scrolls to the output in request order, and forgets them.
    internal (int Top, int Height, int Lines) [] TakePendingRowScrolls ()
    {
        lock (_contentsLock)
        {
            if (_pendingRowScrolls.Count == 0)
            {
                return [];
            }

            (int Top, int Height, int Lines) [] scrolls = [.. _pendingRowScrolls];
            _pendingRowScrolls.Clear ();

            return scrolls;
        }
    }

    // A raster image owns the blank cells in its visible destination. Keep those cells clean so
    // Sixel output is not erased by normal cell rendering; OutputBase decides when protocol-specific
    // transparent blanks must be emitted for Kitty's below-text placement model.
    private bool ClearCellsUnderRasterImage (RasterImageCommand command)
    {
        if (Contents is null)
        {
            return false;
        }

        Attribute transparent = new (Color.None, Color.None);
        Region clip = command.Clip ?? new (Screen);
        var needsTerminalClear = false;

        foreach (Rectangle clipRect in clip.GetRectangles ())
        {
            Rectangle visible = Rectangle.Intersect (Rectangle.Intersect (command.DestinationCells, clipRect), Screen);

            if (visible.Width <= 0 || visible.Height <= 0)
            {
                continue;
            }

            for (int row = visible.Y; row < visible.Bottom; row++)
            {
                for (int col = visible.X; col < visible.Right; col++)
                {
                    string grapheme = Contents [row, col].Grapheme;

                    if (!string.IsNullOrEmpty (grapheme) && grapheme != " ")
                    {
                        needsTerminalClear = true;
                    }

                    Contents [row, col].Grapheme = " ";
                    Contents [row, col].Attribute = transparent;
                    Contents [row, col].IsDirty = false;
                }
            }
        }

        return needsTerminalClear;
    }

    private void MarkRasterImageCellsClean (RasterImageCommand command)
    {
        SetRasterImageCellsDirtyState (command, false);
    }

    private void MarkRasterImageCellsDirty (RasterImageCommand command)
    {
        SetRasterImageCellsDirtyState (command, true);
    }

    private void SetRasterImageCellsDirtyState (RasterImageCommand command, bool isDirty)
    {
        if (Contents is null)
        {
            return;
        }

        Region clip = command.Clip ?? new (Screen);

        foreach (Rectangle clipRect in clip.GetRectangles ())
        {
            Rectangle visible = Rectangle.Intersect (Rectangle.Intersect (command.DestinationCells, clipRect), Screen);

            if (visible.Width <= 0 || visible.Height <= 0)
            {

                continue;
            }

            for (int row = visible.Y; row < visible.Bottom; row++)
            {
                for (int col = visible.X; col < visible.Right; col++)
                {
                    if (isDirty)
                    {
                        MarkDirty (col, row);

                        continue;
                    }

                    Contents [row, col].IsDirty = false;
                }
            }
        }
    }

    /// <summary>Clears the <see cref="Contents"/> of the driver.</summary>
    /// <param name="initiallyDirty">
    ///     When <see langword="true"/> (the default), all cells are marked dirty so the first render
    ///     overwrites the entire screen. When <see langword="false"/> (used in inline mode), cells start
    ///     clean so only cells that are explicitly drawn will be flushed, leaving the rest of the terminal
    ///     untouched.
    /// </param>
    public void ClearContents (bool initiallyDirty)
    {
        lock (_contentsLock)
        {
            ClearContentsCore (initiallyDirty);
        }
    }

    /// <summary>
    ///     Non-locking implementation of <see cref="ClearContents(bool)"/>.
    ///     Caller must already hold <see cref="_contentsLock"/>.
    /// </summary>
    private void ClearContentsCore (bool initiallyDirty)
    {
        Contents = new Cell [Rows, Cols];
        _pendingRowScrolls.Clear ();

        // TODO: ClearContents should not clear the clip; it should only clear the contents. Move clearing it elsewhere.
        Clip = new Region (Screen);

        DirtyLines = new bool [Rows];

        // Null out (rather than Clear) so GetCellUrl's null fast-path stays effective
        // for the lifetime of the buffer until URLs are introduced again.
        if (_explicitUrlMap is { } || _autoUrlMap is { })
        {
            _explicitUrlMap = null;
            _autoUrlMap = null;
            _urlStateVersion++;
        }

        for (var row = 0; row < Rows; row++)
        {
            for (var c = 0; c < Cols; c++)
            {
                Contents [row, c] = new Cell { Grapheme = " ", Attribute = new Attribute (Color.White, Color.Black), IsDirty = initiallyDirty };
            }

            DirtyLines [row] = initiallyDirty;
        }
    }

    /// <summary>
    ///     Gets or sets a value indicating whether this buffer is operating in inline mode.
    ///     When <see langword="true"/>, <see cref="ClearContents()"/> initialises cells with
    ///     <c>IsDirty = false</c> so that only cells explicitly drawn are flushed to the
    ///     terminal, leaving the rest of the visible terminal untouched.
    /// </summary>
    public bool InlineMode { get; set; }

    /// <summary>Gets the location and size of the terminal screen.</summary>
    internal Rectangle Screen => new (0, 0, Cols, Rows);

    /// <summary>
    ///     Non-locking core of <see cref="AddStr(string)"/> for one grapheme. Adds it at the current cursor position
    ///     using the clip snapshot <paramref name="clipRects"/>. Caller must already hold <see cref="_contentsLock"/>.
    /// </summary>
    /// <param name="grapheme">The grapheme to add.</param>
    /// <param name="clipRects">The rectangles of <see cref="Clip"/>, snapshotted by the caller.</param>
    /// <param name="clipRect">The bounds of <see cref="Clip"/>, snapshotted by the caller.</param>
    private void AddGraphemeCore (string grapheme, Rectangle [] clipRects, Rectangle clipRect)
    {
        int printableGraphemeWidth = -1;

        if (IsValidCell (grapheme.GetColumns (), Col, Row, clipRects))
        {
            // Set attribute and mark dirty for current cell
            SetAttributeAndDirty (Col, Row);
            InvalidateOverlappedWideGlyph (Col, Row);

            string printableGrapheme = grapheme.MakePrintable ();
            printableGraphemeWidth = printableGrapheme.GetColumns ();
            WriteGraphemeByWidth (Col, Row, printableGrapheme, printableGraphemeWidth, clipRect, clipRects);
        }

        // Always advance cursor (even if location was invalid)
        Col++;

        if (printableGraphemeWidth <= 1)
        {
            return;
        }

        // Skip the second column of a wide character
        // See issue: https://github.com/tui-cs/Terminal.Gui/issues/4492
        // Test: AddStr_WideGlyph_Second_Column_Attribute_Outputs_Correctly
        // Test: AddStr_WideGlyph_Second_Column_Attribute_Set_When_In_Clip
        if (ClipContains (clipRects, Col, Row))
        {
            // Mark dirty only if the attribute is actually changing, to avoid
            // invalidating overlapped content unnecessarily (see #4258).
            if (Contents! [Row, Col].Attribute != CurrentAttribute)
            {
                Contents [Row, Col].Attribute = CurrentAttribute;
                MarkDirty (Col, Row);
            }
        }

        // Advance cursor again for wide character
        Col++;
    }

    /// <summary>
    ///     INTERNAL: Same as <see cref="IsValidLocation(string, int, int)"/> for a text of <paramref name="textWidth"/>
    ///     columns, but tests <paramref name="clipRects"/> instead of calling <see cref="Region.Contains(int, int)"/>.
    /// </summary>
    /// <param name="textWidth">The number of columns the text requires.</param>
    /// <param name="col">The column.</param>
    /// <param name="row">The row.</param>
    /// <param name="clipRects">The rectangles of <see cref="Clip"/>, snapshotted by the caller.</param>
    private bool IsValidCell (int textWidth, int col, int row, Rectangle [] clipRects) =>
        col >= 0 && row >= 0 && col + textWidth <= Cols && row < Rows && ClipContains (clipRects, col, row);

    /// <summary>INTERNAL: Returns whether any of <paramref name="clipRects"/> contains (<paramref name="col"/>, <paramref name="row"/>).</summary>
    /// <param name="clipRects">The rectangles of the clip.</param>
    /// <param name="col">The column.</param>
    /// <param name="row">The row.</param>
    private static bool ClipContains (Rectangle [] clipRects, int col, int row)
    {
        foreach (Rectangle clipRect in clipRects)
        {
            if (clipRect.Contains (col, row))
            {
                return true;
            }
        }

        return false;
    }

    // Printable ASCII graphemes, indexed by char, so AddStr does not allocate one string per character.
    private static readonly string [] AsciiGraphemes = CreateAsciiGraphemes ();

    private static string [] CreateAsciiGraphemes ()
    {
        string [] graphemes = new string [128];

        for (var i = 0; i < graphemes.Length; i++)
        {
            graphemes [i] = ((char)i).ToString ();
        }

        return graphemes;
    }

    /// <summary>INTERNAL: Returns whether <paramref name="str"/> is made only of chars 0x20 to 0x7E (no controls, DEL or non-ASCII).</summary>
    /// <param name="str">The string to check.</param>
    private static bool IsPrintableAscii (string str)
    {
        foreach (char c in str)
        {
            if (c is < ' ' or > '~')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    ///     INTERNAL: If we're writing at an odd column and there's a wide glyph to our left,
    ///     invalidate it since we're overwriting the second half.
    /// </summary>
    /// <param name="col">The column.</param>
    /// <param name="row">The row.</param>
    private void InvalidateOverlappedWideGlyph (int col, int row)
    {
        if (col <= 0 || Contents! [row, col - 1].Grapheme.GetColumns () <= 1)
        {
            return;
        }
        Contents [row, col - 1].Grapheme = _column1ReplacementChar.ToString ();
        MarkDirty (col - 1, row);
    }

    /// <summary>
    ///     INTERNAL: Marks a cell and its row as dirty.
    /// </summary>
    /// <param name="col">The column.</param>
    /// <param name="row">The row.</param>
    private void MarkDirty (int col, int row)
    {
        Contents! [row, col].IsDirty = true;
        DirtyLines [row] = true;
    }

    /// <summary>
    ///     INTERNAL: Helper to set the attribute and mark the cell as dirty.
    /// </summary>
    /// <param name="col">The column.</param>
    /// <param name="row">The row.</param>
    private void SetAttributeAndDirty (int col, int row)
    {
        Contents! [row, col].Attribute = CurrentAttribute;
        MarkDirty (col, row);

        // Update the URL map: store CurrentUrl, or clear any stale entry so cells
        // overdrawn by non-link content are not wrapped in OSC 8 sequences.
        if (!string.IsNullOrEmpty (CurrentUrl))
        {
            SetCellUrl (col, row, CurrentUrl);
        }
        else
        {
            _explicitUrlMap?.Remove (new Point (col, row));
        }
    }

    /// <summary>
    ///     Sets the URL for the cell at the specified position.
    /// </summary>
    /// <param name="col">The column.</param>
    /// <param name="row">The row.</param>
    /// <param name="url">The URL to associate with this cell.</param>
    private void SetCellUrl (int col, int row, string url)
    {
        _explicitUrlMap ??= [];
        _explicitUrlMap [new Point (col, row)] = url;
    }

    internal void SyncAutoUrlsForRow (int row)
    {
        lock (_contentsLock)
        {
            SyncAutoUrlsForRowCore (row);
        }
    }

    internal void SyncAutoUrlsForAllRows ()
    {
        lock (_contentsLock)
        {
            for (int row = 0; row < Rows; row++)
            {
                SyncAutoUrlsForRowCore (row);
            }
        }
    }

    private void SyncAutoUrlsForRowCore (int row)
    {
        if (Contents is null || row < 0 || row >= Rows)
        {
            return;
        }

        if (_autoUrlMap is { Count: > 0 })
        {
            for (int col = 0; col < Cols; col++)
            {
                _autoUrlMap.Remove (new Point (col, row));
            }
        }

        // Build the row text and a parallel char-to-column map so grapheme clusters
        // wider than a single char (ZWJ emoji, base + combining mark) don't shift the
        // detected URL out of alignment with its actual columns.
        StringBuilder rowText = new (Cols);
        int [] colByChar = new int [Cols * 2];
        int charCount = 0;

        for (int col = 0; col < Cols; col++)
        {
            string grapheme = Contents [row, col].Grapheme;
            string append = string.IsNullOrEmpty (grapheme) ? " " : grapheme;
            rowText.Append (append);

            if (charCount + append.Length > colByChar.Length)
            {
                Array.Resize (ref colByChar, Math.Max (colByChar.Length * 2, charCount + append.Length));
            }

            for (int k = 0; k < append.Length; k++)
            {
                colByChar [charCount++] = col;
            }
        }

        foreach (Osc8UrlLinker.UrlRange range in Osc8UrlLinker.FindUrls (rowText.ToString ()))
        {
            if (range.Start >= charCount)
            {
                continue;
            }

            int startCol = colByChar [range.Start];
            int lastCharIdx = range.Start + range.Length - 1;
            int endCol = lastCharIdx < charCount ? colByChar [lastCharIdx] + 1 : Cols;
            endCol = Math.Min (endCol, Cols);

            for (int col = startCol; col < endCol; col++)
            {
                _autoUrlMap ??= [];
                _autoUrlMap [new Point (col, row)] = range.Url;
            }
        }

        if (_autoUrlMap is { Count: 0 })
        {
            _autoUrlMap = null;
        }
    }

    /// <summary>
    ///     INTERNAL: Writes a (0 or 1 column wide) Grapheme.
    /// </summary>
    /// <param name="col">The column.</param>
    /// <param name="row">The row.</param>
    /// <param name="grapheme">The single-width Grapheme to write.</param>
    /// <param name="clipRect">The clipping rectangle.</param>
    private void WriteGrapheme (int col, int row, string grapheme, Rectangle clipRect)
    {
        if (grapheme is null)
        {
            return;
        }

        Debug.Assert (grapheme.GetColumns () < 2);
        Contents! [row, col].Grapheme = grapheme;

        // Mark the next cell as dirty to ensure proper rendering of adjacent content
        if (col < clipRect.Right - 1 && col + 1 < Cols)
        {
            MarkDirty (col + 1, row);
        }
    }

    /// <summary>
    ///     INTERNAL: Writes a Grapheme to the buffer based on its width (0, 1, or 2 columns).
    /// </summary>
    /// <param name="col">The column.</param>
    /// <param name="row">The row.</param>
    /// <param name="text">The printable text to write.</param>
    /// <param name="textWidth">The column width of the text.</param>
    /// <param name="clipRect">The clipping rectangle.</param>
    /// <param name="clipRects">The rectangles of the clip, snapshotted by the caller.</param>
    private void WriteGraphemeByWidth (int col, int row, string text, int textWidth, Rectangle clipRect, Rectangle [] clipRects)
    {
        switch (textWidth)
        {
            case 0:
            case 1:
                WriteGrapheme (col, row, text, clipRect);

                break;

            case 2:
                WriteWideGrapheme (col, row, text, clipRects);

                break;

            default:
                // Negative width or non-spacing character (shouldn't normally occur)
                Contents! [row, col].Grapheme = " ";
                Contents [row, col].IsDirty = false;

                break;
        }
    }

    /// <summary>
    ///     INTERNAL: Writes a wide Grapheme (2 columns wide) handling clipping and partial overlap cases.
    /// </summary>
    /// <param name="col">The column.</param>
    /// <param name="row">The row.</param>
    /// <param name="grapheme">The wide Grapheme to write.</param>
    /// <param name="clipRects">The rectangles of the clip, snapshotted by the caller.</param>
    private void WriteWideGrapheme (int col, int row, string grapheme, Rectangle [] clipRects)
    {
        Debug.Assert (grapheme.GetColumns () == 2);

        if (!ClipContains (clipRects, col + 1, row))
        {
            // Second column is outside clip - can't fit wide char here
            Contents! [row, col].Grapheme = _column1ReplacementChar.ToString ();
        }
        else
        {
            // Both columns are in bounds - write the wide character
            // It will naturally render across both columns when output to the terminal
            Contents! [row, col].Grapheme = grapheme;

            // DO NOT modify column N+1 here!
            // The wide glyph will naturally render across both columns.
            // If we set column N+1 to replacement char, we would overwrite
            // any content that was intentionally drawn there (like borders at odd columns).
            // See: https://github.com/tui-cs/Terminal.Gui/issues/4258
        }
    }
}
