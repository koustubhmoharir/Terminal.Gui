using System.IO.Abstractions;

namespace Terminal.Gui.Views;

public partial class FileDialog
{
    private void TableViewHandleCommandNotBound (object? sender, CommandEventArgs e)
    {
        if (e.Context!.Command != Command.Context)
        {
            return;
        }

        if (e.Context.Binding is MouseBinding { MouseEvent: { Flags: MouseFlags.RightButtonClicked } mouse })
        {
            Point? clickedCell = _tableView.ScreenToCell (mouse.Position!.Value.X, mouse.Position!.Value.Y, out int? clickedCol);

            if (clickedCol is { })
            {
                // right click in a header
                ShowHeaderContextMenu (clickedCol.Value, mouse);
            }
            else if (clickedCell is { })
            {
                // right click in rest of table
                ShowCellContextMenu (clickedCell, mouse);
            }
        }

        if (e.Context.Binding is not KeyBinding)
        {
            return;
        }

        PopoverMenu contextMenu = new ([
                                           new MenuItem (Strings.fdCtxNew, string.Empty, New),
                                           new MenuItem (Strings.fdCtxRename, string.Empty, () => Rename (App)),
                                           new MenuItem (Strings.fdCtxDelete, string.Empty, Delete)
                                       ]);

        // Registering with the PopoverManager will ensure that the context menu is closed when the view is no longer focused
        // and the context menu is disposed when it is closed.
        App!.Popovers?.Register (contextMenu);

        Point pos = new (_tableView.FrameToScreen ().X + 15, _tableView.FrameToScreen ().Y + (_tableView.Value?.SelectedCell.Y ?? 0) + _tableView.GetHeaderHeight ());
        contextMenu.MakeVisible (pos);
    }

    /// <inheritdoc/>
    protected override bool OnDrawingContent (DrawContext? context)
    {
        if (string.IsNullOrWhiteSpace (_feedback))
        {
            return true;
        }

        int feedbackWidth = _feedback.GetColumns ();
        int feedbackPadLeft = (Viewport.Width - feedbackWidth) / 2 - 1;

        feedbackPadLeft = Math.Min (Viewport.Width, feedbackPadLeft);
        feedbackPadLeft = Math.Max (0, feedbackPadLeft);

        int feedbackPadRight = Viewport.Width - (feedbackPadLeft + feedbackWidth + 2);
        feedbackPadRight = Math.Min (Viewport.Width, feedbackPadRight);
        feedbackPadRight = Math.Max (0, feedbackPadRight);

        Move (0, Viewport.Height / 2);

        SetAttribute (new Attribute (Color.Red, GetAttributeForRole (VisualRole.Normal).Background));
        AddStr (new string (' ', feedbackPadLeft));
        AddStr (_feedback);
        AddStr (new string (' ', feedbackPadRight));

        return true;
    }

    internal void ApplySort ()
    {
        FileSystemInfoStats [] stats = State?.Children ?? [];

        // This portion is never reordered (always .. at top then folders)
        IOrderedEnumerable<FileSystemInfoStats> forcedOrder = stats.OrderByDescending (f => f.IsParent).ThenBy (f => f.IsDir ? -1 : 100);

        // This portion is flexible based on the column clicked (e.g. alphabetical)
        IOrderedEnumerable<FileSystemInfoStats> ordered = _currentSortIsAsc
                                                              ? forcedOrder.ThenBy (f => FileDialogTableSource.GetRawColumnValue (_currentSortColumn, f))
                                                              : forcedOrder.ThenByDescending (f => FileDialogTableSource.GetRawColumnValue (_currentSortColumn,
                                                                                                  f));

        State?.Children = ordered.ToArray ();

        _tableView.Update ();
    }

    internal void SortColumn (int col, bool isAsc)
    {
        // set a sort order
        _currentSortColumn = col;
        _currentSortIsAsc = isAsc;

        ApplySort ();
    }

    private string AspectGetter (object o)
    {
        var fsi = (IFileSystemInfo)o;

        if (o is IDirectoryInfo dir && _treeRoots.TryGetValue (dir, out string? getter))
        {
            // Directory has a special name e.g. 'Pictures'
            return getter;
        }

        return (Style.IconProvider.GetIconWithOptionalSpace (fsi) + fsi.Name).Trim ();
    }

    private void TableViewOnAccepted (object? sender, CommandEventArgs e)
    {
        if (TryAcceptMulti ())
        {
            return;
        }

        if (TryEnterSelectedDirectory ())
        {
            //if (d == State?.Directory || d.FullName == State?.Directory.FullName)
            //{
            //    FinishAccept ();
            //}

            return;
        }

        FileSystemInfoStats stats = RowToStats (_tableView.Value!.SelectedCell.Y);

        if (stats.FileSystemInfo is IFileInfo f)
        {
            Accept (f);
        }
    }

    private void ClearFeedback () => _feedback = null;

    private Scheme ColorGetter (CellColorGetterArgs args)
    {
        FileSystemInfoStats stats = RowToStats (args.RowIndex);

        if (!Style.UseColors)
        {
            return _tableView.GetScheme ();
        }

        Color foreground = Style.ColorProvider.GetColor (stats.FileSystemInfo!) ?? GetAttributeForRole (VisualRole.Normal).Foreground;
        Color background = GetAttributeForRole (VisualRole.Normal).Background;

        // TODO: Add some kind of cache for this
        return new Scheme
        {
            Normal = new Attribute (foreground, background),
            HotNormal = new Attribute (foreground, background),
            Focus = args.RowScheme.Focus,
            HotFocus = args.RowScheme.HotFocus,
            Active = args.RowScheme.Active,
            HotActive = args.RowScheme.HotActive,
            Disabled = args.RowScheme.Disabled
        };
    }

    private string GetProposedNewSortOrder (int clickedCol, out bool isAsc)
    {
        // work out new sort order
        if (_currentSortColumn == clickedCol && _currentSortIsAsc)
        {
            isAsc = false;

            return string.Format (Strings.fdCtxSortDesc, _tableView.Table!.ColumnNames [clickedCol]);
        }

        isAsc = true;

        return string.Format (Strings.fdCtxSortAsc, _tableView.Table!.ColumnNames [clickedCol]);
    }

    private void HideColumn (int clickedCol)
    {
        ColumnStyle style = _tableView.Style.GetOrCreateColumnStyle (clickedCol);
        style.Visible = false;
        _tableView.Update ();
    }

    private void OnTableViewActivating (object? sender, CommandEventArgs e)
    {
        // Only handle mouse clicks, not keyboard selections
        if (e.Context?.Binding is not MouseBinding { MouseEvent: { } mouse })
        {
            return;
        }

        _tableView.ScreenToCell (mouse.Position!.Value.X, mouse.Position!.Value.Y, out int? clickedCol);

        if (clickedCol is null || !mouse.Flags.FastHasFlags (MouseFlags.LeftButtonClicked))
        {
            return;
        }

        // left click in a header
        SortColumn (clickedCol.Value);
    }

    private FileSystemInfoStats RowToStats (int rowIndex) => State?.Children [rowIndex]!;

    private void ShowCellContextMenu (Point? clickedCell, Mouse e)
    {
        if (clickedCell is null)
        {
            return;
        }

        PopoverMenu contextMenu = new ([
                                           new MenuItem (Strings.fdCtxNew, string.Empty, New),
                                           new MenuItem (Strings.fdCtxRename, string.Empty, () => Rename (App)),
                                           new MenuItem (Strings.fdCtxDelete, string.Empty, Delete)
                                       ]);

        _tableView.SetSelection (clickedCell.Value.X, clickedCell.Value.Y, false);

        // Registering with the PopoverManager will ensure that the context menu is closed when the view is no longer focused
        // and the context menu is disposed when it is closed.
        App!.Popovers?.Register (contextMenu);

        contextMenu.MakeVisible (e.ScreenPosition);
    }

    private void ShowHeaderContextMenu (int clickedCol, Mouse e)
    {
        string sort = GetProposedNewSortOrder (clickedCol, out bool isAsc);

        PopoverMenu contextMenu = new ([
                                           new MenuItem (string.Format (Strings.fdCtxHide, StripArrows (_tableView.Table!.ColumnNames [clickedCol])),
                                                         string.Empty,
                                                         () => HideColumn (clickedCol)),
                                           new MenuItem (StripArrows (sort), string.Empty, () => SortColumn (clickedCol, isAsc))
                                       ]);

        // Registering with the PopoverManager will ensure that the context menu is closed when the view is no longer focused
        // and the context menu is disposed when it is closed.
        App!.Popovers?.Register (contextMenu);

        contextMenu.MakeVisible (e.ScreenPosition);
    }

    private void SortColumn (int clickedCol)
    {
        GetProposedNewSortOrder (clickedCol, out bool isAsc);
        SortColumn (clickedCol, isAsc);

        _tableView.Table = new FileDialogTableSource (this, State, Style, _currentSortColumn, _currentSortIsAsc);
    }

    private static string StripArrows (string columnName) => columnName.Replace (" (▼)", string.Empty).Replace (" (▲)", string.Empty);

    // Navigation keys: Backspace / Alt+Left = back, Shift+Backspace / Alt+Right = forward,
    // Left / Alt+Up = up to parent, Right = enter the selected directory (file rows fall through to the TableView).
    // TODO: Port this to use key bindings
    private bool TableView_KeyDown (Key keyEvent)
    {
        switch (keyEvent.KeyCode)
        {
            case KeyCode.Backspace:
            case KeyCode.AltMask | KeyCode.CursorLeft: return _history.Back ();

            case KeyCode.ShiftMask | KeyCode.Backspace:
            case KeyCode.AltMask | KeyCode.CursorRight: return _history.Forward ();

            case KeyCode.CursorLeft:
            case KeyCode.AltMask | KeyCode.CursorUp:
                _history.Up ();

                return true;

            case KeyCode.CursorRight: return TryEnterSelectedDirectory ();

            case KeyCode.Delete:
                Delete ();

                return true;

            case KeyCode.CtrlMask | KeyCode.R:
                Rename (App);

                return true;

            case KeyCode.CtrlMask | KeyCode.N:
                New ();

                return true;

            default: return false;
        }
    }

    // BUGBUG: See https://github.com/tui-cs/Terminal.Gui/issues/5087#issuecomment-4328093883
    private void TableViewOnValueChanged (object? sender, ValueChangedEventArgs<TableSelection?> e)
    {
        if (_pushingState || !_tableView.HasFocus || _tableView.Value is null || _tableView.Table?.Rows == 0)
        {
            return;
        }

        if (_tableView.MultiSelect && _tableView.MultiSelectedRegions.Any ())
        {
            return;
        }

        FileSystemInfoStats stats = RowToStats (_tableView.Value.SelectedCell.Y);

        IFileSystemInfo? dest = stats.IsParent ? State!.Directory : stats.FileSystemInfo;

        try
        {
            _pushingState = true;

            SetPathToSelectedObject (dest);
            State!.Selected = stats;
            _tbPath.Autocomplete?.ClearSuggestions ();
        }
        finally
        {
            _pushingState = false;
        }
    }

    /// <summary>
    /// If the selected row is a directory (including the parent <c>..</c> row), enters it and returns <see langword="true"/>.
    /// Otherwise (file row, no selection or empty table) returns <see langword="false"/>.
    /// </summary>
    private bool TryEnterSelectedDirectory ()
    {
        if (_tableView.Value is null || _tableView.Table?.Rows == 0)
        {
            return false;
        }

        FileSystemInfoStats stats = RowToStats (_tableView.Value.SelectedCell.Y);

        if (stats.FileSystemInfo is not IDirectoryInfo d)
        {
            return false;
        }

        PushState (d, true);

        return true;
    }

    private void WriteStateToTableView ()
    {
        _tableView.Table = new FileDialogTableSource (this, State, Style, _currentSortColumn, _currentSortIsAsc);

        ApplySort ();
        _tableView.Update ();
    }
}
