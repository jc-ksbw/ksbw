Option Strict On
Option Infer On

Imports System.Drawing
Imports System.IO
Imports System.Threading.Tasks
Imports System.Windows.Forms

''' <summary>
''' Reads a JSON or XML results feed (file or URL), pairs each contest with a setup race, and sends the
''' numbers over the main window's connection. The window hides instead of closing so polling can continue.
''' </summary>
Public Class ResultsForm
    Inherits Form

    Private Const SendCol As String = "colSend"
    Private Const RaceCol As String = "colRace"
    Private Const LabelSep As String = "  |  "

    Private ReadOnly _main As MainForm
    Private ReadOnly _engine As New ResultsEngine
    Private ReadOnly _timer As New System.Windows.Forms.Timer
    Private ReadOnly _crosswalkPath As String = CrosswalkStore.DefaultPath()
    Private ReadOnly _checks As New Dictionary(Of String, Boolean)(StringComparer.OrdinalIgnoreCase)
    Private ReadOnly _pending As New HashSet(Of String)

    Private _contests As New List(Of FeedContest)
    Private _items As New List(Of ResultItem)
    Private _busy As Boolean
    Private _rebuilding As Boolean
    Private _allowClose As Boolean
    Private _warnedDisconnected As Boolean
    Private _lastSummary As String = ""

    Private txtSource As TextBox
    Private txtHeaders As TextBox
    Private txtProfile As TextBox
    Private txtSkip As TextBox
    Private ckSubs As CheckBox
    Private btnReport As Button
    Private cboWriteIns As ComboBox
    Private lblApi As Label
    Private ReadOnly _monitor As New MonitorClient
    Private _apiMode As Boolean
    Private numInterval As NumericUpDown
    Private ckAuto As CheckBox
    Private btnPoll As Button
    Private btnFetch As Button
    Private lblInfo As Label
    Private lblSummary As Label
    Private grid As DataGridView
    Private btnSend As Button
    Private btnCheckReady As Button
    Private btnUncheckAll As Button
    Private btnResetSent As Button
    Private txtLog As TextBox
    Private lblState As Label

    Public Sub New(main As MainForm)
        _main = main
        Text = "Live results"
        Font = New Font("Segoe UI", 9.0!)
        AutoScaleMode = AutoScaleMode.Dpi
        ClientSize = New Size(1100, 760)
        MinimumSize = New Size(900, 560)
        StartPosition = FormStartPosition.CenterParent

        BuildLayout()

        Dim s = _main.Settings
        txtSource.Text = s.FeedSource
        txtHeaders.Text = s.FeedHeaders
        txtProfile.Text = s.FeedProfile
        numInterval.Value = Math.Max(numInterval.Minimum, Math.Min(numInterval.Maximum, s.FeedIntervalSec))
        ckAuto.Checked = s.FeedAuto
        txtSkip.Text = s.FeedSkipAreas
        ckSubs.Checked = s.FeedSubRaces
        cboWriteIns.SelectedIndex = Math.Max(0, Math.Min(2, s.ApiWriteIns))

        For Each kv In CrosswalkStore.Load(_crosswalkPath)
            _engine.Crosswalk(kv.Key) = kv.Value
        Next

        AddHandler _timer.Tick, Sub(o, e) FetchNow()
        AddHandler _main.Client.MessageSent, AddressOf OnMessageSent
        AddHandler _main.Client.ConnectionChanged, AddressOf OnConnectionChanged
        UpdateInfo()
        UpdateButtons()
    End Sub

#Region "Layout"

    Private Shared Function MakeLabel(text As String) As Label
        Return New Label With {.Text = text, .AutoSize = True, .Anchor = AnchorStyles.Left, .Margin = New Padding(0, 6, 6, 0)}
    End Function

    Private Sub BuildLayout()
        SuspendLayout()

        txtSource = New TextBox With {.Dock = DockStyle.Fill}
        txtHeaders = New TextBox With {.Dock = DockStyle.Fill}
        txtProfile = New TextBox With {.Dock = DockStyle.Fill}
        Dim btnSourceBrowse As New Button With {.Text = "Browse...", .AutoSize = True}
        Dim btnProfileBrowse As New Button With {.Text = "Browse...", .AutoSize = True}
        AddHandler btnSourceBrowse.Click, Sub() BrowseInto(txtSource, "Feed files (*.json;*.xml)|*.json;*.xml|All files (*.*)|*.*")
        AddHandler btnProfileBrowse.Click, Sub() BrowseInto(txtProfile, "Mapping files (*.json)|*.json|All files (*.*)|*.*")

        Dim feedTable As New TableLayoutPanel With {.Dock = DockStyle.Top, .AutoSize = True, .ColumnCount = 3, .Padding = New Padding(8, 8, 8, 0)}
        feedTable.ColumnStyles.Add(New ColumnStyle(SizeType.AutoSize))
        feedTable.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
        feedTable.ColumnStyles.Add(New ColumnStyle(SizeType.AutoSize))
        feedTable.Controls.Add(MakeLabel("Feed (file or URL)"), 0, 0)
        feedTable.Controls.Add(txtSource, 1, 0)
        feedTable.Controls.Add(btnSourceBrowse, 2, 0)
        feedTable.Controls.Add(MakeLabel("Headers (optional)"), 0, 1)
        feedTable.Controls.Add(txtHeaders, 1, 1)
        feedTable.Controls.Add(MakeLabel("Name: value; Name: value"), 2, 1)
        feedTable.Controls.Add(MakeLabel("Mapping file (optional)"), 0, 2)
        feedTable.Controls.Add(txtProfile, 1, 2)
        feedTable.Controls.Add(btnProfileBrowse, 2, 2)
        txtSkip = New TextBox With {.Dock = DockStyle.Fill}
        feedTable.Controls.Add(MakeLabel("Skip counties"), 0, 3)
        feedTable.Controls.Add(txtSkip, 1, 3)
        feedTable.Controls.Add(MakeLabel("e.g. San Benito; separate with ;"), 2, 3)

        numInterval = New NumericUpDown With {.Minimum = 5, .Maximum = 3600, .Value = 60, .Width = 65}
        ckAuto = New CheckBox With {.Text = "Send changes automatically", .AutoSize = True, .Margin = New Padding(12, 6, 0, 0)}
        btnPoll = New Button With {.Text = "Start polling", .AutoSize = True, .MinimumSize = New Size(100, 27)}
        btnFetch = New Button With {.Text = "Fetch now", .AutoSize = True}
        AddHandler btnPoll.Click, AddressOf OnPollClick
        AddHandler btnFetch.Click, Sub() FetchNow(force:=True)
        AddHandler ckAuto.CheckedChanged, Sub() SaveSettings()

        Dim controlRow As New FlowLayoutPanel With {.Dock = DockStyle.Top, .AutoSize = True, .Padding = New Padding(8, 4, 8, 0)}
        ckSubs = New CheckBox With {.Text = "Send county breakdown (sub-races)", .AutoSize = True, .Margin = New Padding(12, 6, 0, 0)}
        AddHandler ckSubs.CheckedChanged, Sub()
                                              SaveSettings()
                                              If _contests.Count > 0 Then Reevaluate()
                                          End Sub
        controlRow.Controls.AddRange({MakeLabel("Check every (s)"), numInterval, btnPoll, btnFetch, ckAuto, ckSubs})

        cboWriteIns = New ComboBox With {.DropDownStyle = ComboBoxStyle.DropDownList, .Width = 230, .Margin = New Padding(12, 3, 0, 0)}
        cboWriteIns.Items.AddRange({"Write-ins: named + one Write-in line", "Write-ins: one Write-in line only", "Write-ins: leave out"})
        cboWriteIns.Visible = False
        AddHandler cboWriteIns.SelectedIndexChanged, Sub()
                                                        _main.Settings.ApiWriteIns = cboWriteIns.SelectedIndex
                                                        SaveSettings()
                                                        If _apiMode Then FetchNow(force:=True)
                                                    End Sub
        controlRow.Controls.Add(cboWriteIns)

        lblApi = New Label With {.Dock = DockStyle.Top, .Height = 22, .Padding = New Padding(10, 4, 0, 0), .ForeColor = Color.FromArgb(70, 70, 70)}
        lblInfo = New Label With {.Dock = DockStyle.Top, .Height = 24, .Padding = New Padding(10, 6, 0, 0)}
        lblSummary = New Label With {.Dock = DockStyle.Top, .Height = 24, .Padding = New Padding(10, 4, 0, 0), .Text = "Fetch the feed to see its contests."}

        grid = New DataGridView With {
            .Dock = DockStyle.Fill, .AllowUserToAddRows = False, .AllowUserToDeleteRows = False, .AllowUserToResizeRows = False,
            .RowHeadersVisible = False, .SelectionMode = DataGridViewSelectionMode.FullRowSelect, .MultiSelect = False,
            .BackgroundColor = SystemColors.Window, .BorderStyle = BorderStyle.FixedSingle, .EditMode = DataGridViewEditMode.EditOnEnter
        }
        grid.Columns.Add(New DataGridViewCheckBoxColumn With {.Name = SendCol, .HeaderText = "Send", .Width = 45})
        grid.Columns.Add(New DataGridViewTextBoxColumn With {.Name = "colStatus", .HeaderText = "Status", .Width = 80, .ReadOnly = True})
        grid.Columns.Add(New DataGridViewTextBoxColumn With {.Name = "colOffice", .HeaderText = "Feed contest", .Width = 220, .ReadOnly = True})
        grid.Columns.Add(New DataGridViewTextBoxColumn With {.Name = "colAreas", .HeaderText = "Counties", .Width = 130, .ReadOnly = True})
        grid.Columns.Add(New DataGridViewTextBoxColumn With {.Name = "colFeedId", .HeaderText = "Feed ID", .Width = 60, .ReadOnly = True})
        grid.Columns.Add(New DataGridViewComboBoxColumn With {.Name = RaceCol, .HeaderText = "Setup race (pick to pair by hand)", .Width = 260, .FlatStyle = FlatStyle.Flat})
        grid.Columns.Add(New DataGridViewTextBoxColumn With {.Name = "colPrec", .HeaderText = "Precincts", .Width = 80, .ReadOnly = True})
        grid.Columns.Add(New DataGridViewTextBoxColumn With {.Name = "colVotes", .HeaderText = "Total votes", .Width = 80, .ReadOnly = True})
        grid.Columns.Add(New DataGridViewTextBoxColumn With {.Name = "colCands", .HeaderText = "Cands", .Width = 50, .ReadOnly = True})
        grid.Columns.Add(New DataGridViewTextBoxColumn With {.Name = "colNotes", .HeaderText = "Notes", .Width = 300, .ReadOnly = True, .AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill})
        AddHandler grid.CurrentCellDirtyStateChanged, Sub()
                                                          If grid.IsCurrentCellDirty Then grid.CommitEdit(DataGridViewDataErrorContexts.Commit)
                                                      End Sub
        AddHandler grid.CellValueChanged, AddressOf OnCellChanged
        AddHandler grid.DataError, Sub(o, e) e.ThrowException = False

        btnSend = New Button With {.Text = "Send checked", .AutoSize = True, .MinimumSize = New Size(120, 29)}
        btnCheckReady = New Button With {.Text = "Check all ready", .AutoSize = True}
        btnUncheckAll = New Button With {.Text = "Uncheck all", .AutoSize = True}
        btnResetSent = New Button With {.Text = "Forget what was sent", .AutoSize = True, .Margin = New Padding(24, 3, 3, 3)}
        AddHandler btnSend.Click, AddressOf OnSendClick
        AddHandler btnCheckReady.Click, Sub() SetChecks(Function(it) it.Status = ResultStatus.Ready)
        AddHandler btnUncheckAll.Click, Sub() SetChecks(Function(it) False)
        AddHandler btnResetSent.Click, AddressOf OnResetClick

        Dim actions As New FlowLayoutPanel With {.Dock = DockStyle.Bottom, .AutoSize = True, .Padding = New Padding(8, 4, 8, 4)}
        btnReport = New Button With {.Text = "Export pairing report...", .AutoSize = True}
        AddHandler btnReport.Click, AddressOf OnReportClick
        actions.Controls.AddRange({btnSend, btnCheckReady, btnUncheckAll, btnReport, btnResetSent})

        txtLog = New TextBox With {
            .Multiline = True, .ReadOnly = True, .ScrollBars = ScrollBars.Vertical, .Dock = DockStyle.Fill,
            .Font = New Font("Consolas", 8.5!), .BackColor = SystemColors.Window
        }
        Dim logPanel As New Panel With {.Dock = DockStyle.Bottom, .Height = 120, .Padding = New Padding(8, 0, 8, 4)}
        logPanel.Controls.Add(txtLog)

        lblState = New Label With {.Dock = DockStyle.Bottom, .Height = 22, .Padding = New Padding(10, 3, 0, 0)}

        Dim body As New Panel With {.Dock = DockStyle.Fill, .Padding = New Padding(8, 4, 8, 0)}
        body.Controls.Add(grid)

        ' Dock order: the Fill control first, outer edges last.
        Controls.Add(body)
        Controls.Add(actions)
        Controls.Add(logPanel)
        Controls.Add(lblState)
        Controls.Add(lblSummary)
        Controls.Add(lblApi)
        Controls.Add(lblInfo)
        Controls.Add(controlRow)
        Controls.Add(feedTable)

        ResumeLayout(True)
    End Sub

    Private Sub BrowseInto(box As TextBox, filter As String)
        Using dlg As New OpenFileDialog With {.Filter = filter}
            If box.Text <> "" AndAlso File.Exists(box.Text) Then dlg.InitialDirectory = Path.GetDirectoryName(box.Text)
            If dlg.ShowDialog(Me) = DialogResult.OK Then box.Text = dlg.FileName
        End Using
    End Sub

#End Region

#Region "Helpers"

    Private Sub Log(text As String)
        If IsDisposed Then Return
        txtLog.AppendText(Date.Now.ToString("HH:mm:ss") & "  " & text & vbCrLf)
    End Sub

    Private Sub SaveSettings()
        Dim s = _main.Settings
        s.FeedSource = txtSource.Text.Trim()
        s.FeedHeaders = txtHeaders.Text.Trim()
        s.FeedProfile = txtProfile.Text.Trim()
        s.FeedIntervalSec = CInt(numInterval.Value)
        s.FeedAuto = ckAuto.Checked
        s.FeedSkipAreas = txtSkip.Text.Trim()
        s.FeedSubRaces = ckSubs.Checked
        s.Save()
    End Sub

    Private Sub UpdateInfo()
        lblInfo.Text = $"Sends as election {_main.ElectionDateText()}, source ""{_main.SourceName}"", " &
                       If(_main.IsTest, "TEST", "LIVE") & "  (set in the main window; must match the race setup)"
        lblInfo.ForeColor = If(_main.IsTest, SystemColors.ControlText, Color.DarkRed)
    End Sub

    Private Sub UpdateButtons()
        btnSend.Enabled = _main.Client.IsConnected AndAlso _items.Any(Function(it) it.Race IsNot Nothing)
        btnCheckReady.Enabled = _items.Count > 0
        btnUncheckAll.Enabled = _items.Count > 0
        btnReport.Enabled = _items.Count > 0
        btnPoll.Text = If(_timer.Enabled, "Stop polling", "Start polling")
        lblState.Text = If(_main.Client.IsConnected, "Connected to Election Manager's MOS server.", "Not connected - connect in the main window to send.")
        lblState.ForeColor = If(_main.Client.IsConnected, Color.DarkGreen, Color.DarkRed)
    End Sub

    Private Shared Function RaceLabel(r As Race) As String
        Dim where = String.Join(", ", {r.City, r.County, If(r.District <> "", "district " & r.District, "")}.Where(Function(x) x <> ""))
        Return r.ID & LabelSep & r.Office & If(where <> "", " (" & where & ")", "")
    End Function

    Private Shared Function StatusText(it As ResultItem) As String
        Select Case it.Status
            Case ResultStatus.Ready : Return "Ready"
            Case ResultStatus.Held : Return "HELD"
            Case ResultStatus.Unchanged : Return "Unchanged"
            Case ResultStatus.Sent : Return "Sent"
            Case Else : Return "No match"
        End Select
    End Function

    Private Function IsChecked(it As ResultItem) As Boolean
        Dim v As Boolean
        If _checks.TryGetValue(it.Contest.Key, v) Then Return v
        Return it.Status = ResultStatus.Ready
    End Function

#End Region

#Region "Fetching and pairing"

    Private Sub OnPollClick(sender As Object, e As EventArgs)
        If _timer.Enabled Then
            _timer.Stop()
            Log("Polling stopped.")
        Else
            _timer.Interval = CInt(numInterval.Value) * 1000
            _timer.Start()
            Log($"Polling every {numInterval.Value} s.")
            SaveSettings()
            FetchNow()
        End If
        UpdateButtons()
    End Sub

    ''' <summary>What one fetch produced, whether it came from the monitor API or a plain feed.</summary>
    Private Class Fetched
        Public Property Contests As List(Of FeedContest)
        Public Property Unchanged As Boolean
        Public Property ApiMode As Boolean
        Public Property CountyLine As String = ""
        Public ReadOnly Property Notes As New List(Of String)
    End Class

    Private Sub FetchNow(Optional force As Boolean = False)
        If _busy Then Return
        Dim source = txtSource.Text.Trim()
        If source = "" Then
            Log("Enter a feed address, an API address or a file first.")
            Return
        End If
        _busy = True
        lblSummary.Text = "Fetching..."
        SaveSettings()                ' so the skip list and write-in choice reach the worker
        Dim headers = txtHeaders.Text
        Dim profilePath = txtProfile.Text.Trim()
        Dim options = _main.ApiOptions()
        Dim work = Task.Run(Function() Pull(source, headers, profilePath, options, force))
        work.ContinueWith(Sub(t) UiInvoke(Sub() OnFetched(t)))
    End Sub

    ''' <summary>
    ''' Runs on a worker thread. Tries the Election Results Monitor API first, because it pairs by ID;
    ''' anything else is read as a plain results feed.
    ''' </summary>
    Private Function Pull(source As String, headers As String, profilePath As String,
                          options As MonitorOptions, force As Boolean) As Fetched
        Dim outcome As New Fetched
        _monitor.Headers = headers

        If _monitor.ResultsUrl = "" OrElse Not String.Equals(_monitor.ResultsUrl, MonitorClient.Endpoints(source).Key, StringComparison.OrdinalIgnoreCase) Then
            _monitor.SetSource(source)
        End If

        Dim probe As FetchResult = Nothing
        Try
            probe = _monitor.Fetch(_monitor.ResultsUrl)
        Catch ex As Exception
            ' Not the API, or not reachable there: fall back to reading the address as a plain feed.
        End Try

        If probe IsNot Nothing AndAlso probe.Text <> "" Then
            Dim looksApi = False
            Try
                looksApi = MonitorReader.LooksLikeResults(FeedNode.ParseAny(probe.Text))
            Catch
            End Try
            If looksApi Then
                outcome.ApiMode = True
                Dim snap = _monitor.Poll(force)
                outcome.Notes.AddRange(snap.Notes)
                If snap.Unchanged OrElse snap.Results Is Nothing Then
                    outcome.Unchanged = True
                    Return outcome
                End If
                outcome.Contests = MonitorAdapter.ToFeedContests(snap.Definition, snap.Results, options)
                outcome.CountyLine = "Counties: " & String.Join("   ", snap.Results.Counties.Select(
                    Function(c) $"{c.County} {c.Status.Replace("_", " ")}" & If(c.ErrorText <> "", " (check failed)", ""))) &
                    $"   ballot version {snap.Results.BallotVersion}"
                Dim apiDate = MonitorAdapter.ElectionDateText(snap.Results.ElectionDate)
                If apiDate <> "" AndAlso apiDate <> _main.ElectionDateText() Then
                    outcome.Notes.Add($"The API's election date is {apiDate} but this app is sending {_main.ElectionDateText()}.")
                End If
                For Each c In snap.Results.Counties
                    If c.ErrorText <> "" Then outcome.Notes.Add($"{c.County}: last check failed - {c.ErrorText}")
                Next
                Return outcome
            End If
        End If

        outcome.Contests = FeedFetcher.Load(source, headers, profilePath)
        Return outcome
    End Function

    Private Sub UiInvoke(action As Action)
        If IsDisposed OrElse Not IsHandleCreated Then Return
        Try
            BeginInvoke(action)
        Catch ex As ObjectDisposedException
        Catch ex As InvalidOperationException
        End Try
    End Sub

    Private Sub OnFetched(t As Task(Of Fetched))
        _busy = False
        If t.IsFaulted OrElse t.IsCanceled Then
            Dim msg = If(t.Exception IsNot Nothing, t.Exception.GetBaseException().Message, "cancelled")
            lblSummary.Text = "Could not read the feed: " & msg
            Log("Feed error: " & msg)
            Return
        End If

        Dim got = t.Result
        _apiMode = got.ApiMode
        cboWriteIns.Visible = got.ApiMode
        txtProfile.Enabled = Not got.ApiMode
        If got.CountyLine <> "" Then lblApi.Text = got.CountyLine
        For Each n In got.Notes
            Log("  ! " & n)
        Next

        If got.Unchanged Then
            ' The API says nothing has moved, so the rows on screen are still current.
            lblSummary.Text = If(_lastSummary <> "", _lastSummary, "No change.") & $"   (no change at {Date.Now:HH:mm:ss})"
            UpdateButtons()
            Return
        End If

        _contests = got.Contests
        If _contests.Count = 0 Then
            Log(If(got.ApiMode, "The API returned no county results yet.", "The feed was read but no contests were found. Try a mapping file."))
        End If
        Reevaluate()
        If ckAuto.Checked Then AutoSend()
    End Sub

    Private Sub Reevaluate()
        UpdateInfo()
        Dim races = _main.SetupRaces
        If races.Count = 0 Then Log("No setup races are loaded. Open the race CSV in the main window first, so results can be paired.")

        Dim skip = txtSkip.Text.Split(";"c, ","c).Select(Function(x) x.Trim()).Where(Function(x) x <> "").ToList()
        _items = _engine.Evaluate(_contests, races, _main.ElectionDateText(), _main.SourceName, _main.IsTest, skip, ckSubs.Checked)
        If _main.Client.PendingCount = 0 Then _pending.Clear()
        Rebuild(races)

        Dim ready = _items.Where(Function(it) it.Status = ResultStatus.Ready).Count()
        Dim held = _items.Where(Function(it) it.Status = ResultStatus.Held).Count()
        Dim none = _items.Where(Function(it) it.Status = ResultStatus.Unmatched).Count()
        Dim same = _items.Where(Function(it) it.Status = ResultStatus.Unchanged OrElse it.Status = ResultStatus.Sent).Count()
        Dim paired = New HashSet(Of String)(_items.Where(Function(it) it.Race IsNot Nothing).Select(Function(it) it.Race.ID), StringComparer.OrdinalIgnoreCase)
        Dim missing = races.Where(Function(r) Not paired.Contains(r.ID)).Count()
        Dim fedContests = _items.Sum(Function(it) it.Contests.Count)
        _lastSummary = $"{_items.Count} races/contests from {fedContests} feed contests: {ready} ready, {held} held, {none} with no match, {same} unchanged." &
                       If(_engine.SkippedCount > 0, $"  {_engine.SkippedCount} skipped.", "") & $"  {missing} setup race(s) have no feed data."
        lblSummary.Text = _lastSummary
        UpdateButtons()
    End Sub

    Private Sub Rebuild(races As List(Of Race))
        _rebuilding = True
        Try
            Dim choices = grid.Columns(RaceCol)
            Dim combo = DirectCast(choices, DataGridViewComboBoxColumn)
            combo.Items.Clear()
            combo.Items.Add("")
            For Each r In races
                combo.Items.Add(RaceLabel(r))
            Next

            grid.Rows.Clear()
            For Each it In _items
                Dim c = it.Contest
                Dim prec As String
                If it.Outgoing IsNot Nothing Then
                    prec = it.Outgoing.PrecinctsReporting.ToString() & If(it.Outgoing.PrecinctsTotal > 0, " / " & it.Outgoing.PrecinctsTotal, "")
                Else
                    prec = If(c.PrecinctsReporting.HasValue, c.PrecinctsReporting.Value.ToString(), "?")
                End If
                Dim title = c.Office & If(it.Contests.Count > 1, $"  (+{it.Contests.Count - 1} more)", "")
                Dim idx = grid.Rows.Add(IsChecked(it) AndAlso it.Race IsNot Nothing, StatusText(it), title, If(it.Areas <> "", it.Areas, c.Area), c.FeedId,
                                        If(it.Race IsNot Nothing, RaceLabel(it.Race), ""), prec,
                                        If(it.Race IsNot Nothing, it.TotalVotes.ToString("N0"), ""),
                                        If(it.Race IsNot Nothing, $"{it.MatchedCandidates}/{it.Contests.Sum(Function(x) x.Candidates.Count)}", c.Candidates.Count.ToString()),
                                        String.Join("  ", it.Notes))
                Dim row = grid.Rows(idx)
                row.Tag = it
                Select Case it.Status
                    Case ResultStatus.Unmatched
                        row.DefaultCellStyle.BackColor = Color.FromArgb(255, 220, 220)
                        row.Cells(SendCol).ReadOnly = True
                    Case ResultStatus.Held
                        row.DefaultCellStyle.BackColor = Color.FromArgb(255, 235, 200)
                    Case ResultStatus.Sent, ResultStatus.Unchanged
                        row.DefaultCellStyle.ForeColor = Color.DimGray
                End Select
            Next
        Finally
            _rebuilding = False
        End Try
    End Sub

    Private Sub OnCellChanged(sender As Object, e As DataGridViewCellEventArgs)
        If _rebuilding OrElse e.RowIndex < 0 Then Return
        Dim it = TryCast(grid.Rows(e.RowIndex).Tag, ResultItem)
        If it Is Nothing Then Return

        If grid.Columns(e.ColumnIndex).Name = SendCol Then
            _checks(it.Contest.Key) = CBool(grid.Rows(e.RowIndex).Cells(SendCol).Value)
        ElseIf grid.Columns(e.ColumnIndex).Name = RaceCol Then
            Dim label = Convert.ToString(grid.Rows(e.RowIndex).Cells(RaceCol).Value)
            For Each c In it.Contests
                If label = "" Then
                    _engine.Crosswalk.Remove(c.Key)
                Else
                    _engine.Crosswalk(c.Key) = label.Substring(0, label.IndexOf(LabelSep, StringComparison.Ordinal))
                End If
            Next
            CrosswalkStore.Save(_crosswalkPath, _engine.Crosswalk)
            Log($"Paired ""{it.Contest.Office}"" with {If(label = "", "(nothing)", label.Substring(0, label.IndexOf(LabelSep, StringComparison.Ordinal)))}.")
            _checks.Remove(it.Contest.Key)
            UiInvoke(AddressOf Reevaluate)
        End If
        UpdateButtons()
    End Sub

    Private Sub SetChecks(pick As Func(Of ResultItem, Boolean))
        _rebuilding = True
        Try
            For Each row As DataGridViewRow In grid.Rows
                Dim it = TryCast(row.Tag, ResultItem)
                If it Is Nothing OrElse it.Race Is Nothing Then Continue For
                Dim v = pick(it)
                row.Cells(SendCol).Value = v
                _checks(it.Contest.Key) = v
            Next
        Finally
            _rebuilding = False
        End Try
        UpdateButtons()
    End Sub

#End Region

#Region "Sending"

    Private Sub AutoSend()
        Dim ready = _items.Where(Function(it) it.Status = ResultStatus.Ready AndAlso IsChecked(it)).ToList()
        If ready.Count = 0 Then Return
        If Not _main.Client.IsConnected Then
            If Not _warnedDisconnected Then Log($"{ready.Count} race(s) are ready but the main window is not connected; they will go out once it is.")
            _warnedDisconnected = True
            Return
        End If
        _warnedDisconnected = False
        Enqueue(ready)
    End Sub

    Private Sub OnSendClick(sender As Object, e As EventArgs)
        If Not _main.Client.IsConnected Then Return
        Dim toSend As New List(Of ResultItem)
        For Each row As DataGridViewRow In grid.Rows
            Dim it = TryCast(row.Tag, ResultItem)
            If it IsNot Nothing AndAlso it.Race IsNot Nothing AndAlso CBool(row.Cells(SendCol).Value) AndAlso it.Status <> ResultStatus.Unmatched Then toSend.Add(it)
        Next
        If toSend.Count = 0 Then
            Log("Nothing is checked.")
            Return
        End If
        Dim heldCount = toSend.Where(Function(it) it.Status = ResultStatus.Held).Count()
        If heldCount > 0 Then
            If MessageBox.Show(Me, $"{heldCount} of the checked races are HELD because something looks wrong (see Notes). Send them anyway?",
                               "Send held races", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) <> DialogResult.Yes Then Return
        End If
        If Not _main.IsTest Then
            If MessageBox.Show(Me, $"Send {toSend.Count} race(s) as LIVE results (TEST = NO)?", "Send results", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) <> DialogResult.OK Then Return
        End If
        Enqueue(toSend, True)
    End Sub

    Private Sub Enqueue(items As List(Of ResultItem), Optional force As Boolean = False)
        _main.Client.SendDelayMs = _main.SendDelayMs
        Dim n = 0
        For Each it In items
            Dim key = it.Race.ID & "|" & it.Xml
            If _pending.Contains(key) Then Continue For
            _pending.Add(key)
            it.PendingKey = key
            _main.Client.Enqueue(WireBuilder.BuildMessage(it.Xml, Date.Now), it)
            n += 1
        Next
        If n > 0 Then Log($"Queued {n} race(s).")
        UpdateButtons()
    End Sub

    Private Sub OnMessageSent(tag As Object)
        Dim it = TryCast(tag, ResultItem)
        If it Is Nothing Then Return
        UiInvoke(Sub()
                     _engine.MarkSent(it)
                     _pending.Remove(it.PendingKey)
                     Log($"Sent {it.Race.ID} ({it.Race.Office}): {it.TotalVotes:N0} votes, {it.Outgoing.PrecinctsReporting} precincts reporting.")
                     For Each row As DataGridViewRow In grid.Rows
                         Dim rowItem = TryCast(row.Tag, ResultItem)
                         If rowItem IsNot Nothing AndAlso rowItem.Race IsNot Nothing AndAlso String.Equals(rowItem.Race.ID, it.Race.ID, StringComparison.OrdinalIgnoreCase) Then
                             row.Cells("colStatus").Value = "Sent"
                         End If
                     Next
                     UpdateButtons()
                 End Sub)
    End Sub

    Private Sub OnConnectionChanged(connected As Boolean)
        UiInvoke(Sub()
                     UpdateButtons()
                     If connected Then _warnedDisconnected = False
                 End Sub)
    End Sub

    Private Sub OnReportClick(sender As Object, e As EventArgs)
        Using dlg As New SaveFileDialog With {.Filter = "CSV files (*.csv)|*.csv", .FileName = "pairing-report.csv", .Title = "Save pairing report"}
            If dlg.ShowDialog(Me) <> DialogResult.OK Then Return
            Try
                File.WriteAllText(dlg.FileName, PairingReport.Build(_items, _main.SetupRaces), New System.Text.UTF8Encoding(True))
                Log("Saved pairing report to " & dlg.FileName)
            Catch ex As Exception
                MessageBox.Show(Me, "Could not save the report:" & vbCrLf & ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error)
            End Try
        End Using
    End Sub

    Private Sub OnResetClick(sender As Object, e As EventArgs)
        If MessageBox.Show(Me, "Forget what has been sent? Every paired race will count as new and be sent again on the next check.",
                           "Forget what was sent", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) <> DialogResult.OK Then Return
        _engine.Reset()
        _pending.Clear()
        Log("Forgot what was sent.")
        Reevaluate()
    End Sub

#End Region

    ''' <summary>Called by the main window when the app is closing.</summary>
    Friend Sub ShutDown()
        _allowClose = True
        _timer.Stop()
        RemoveHandler _main.Client.MessageSent, AddressOf OnMessageSent
        RemoveHandler _main.Client.ConnectionChanged, AddressOf OnConnectionChanged
        SaveSettings()
        Close()
    End Sub

    Protected Overrides Sub OnFormClosing(e As FormClosingEventArgs)
        If Not _allowClose AndAlso e.CloseReason = CloseReason.UserClosing Then
            e.Cancel = True
            SaveSettings()
            Hide()
            Return
        End If
        MyBase.OnFormClosing(e)
    End Sub

    Protected Overrides Sub OnShown(e As EventArgs)
        MyBase.OnShown(e)
        Reevaluate()
    End Sub

    Protected Overrides Sub OnVisibleChanged(e As EventArgs)
        MyBase.OnVisibleChanged(e)
        If Visible Then
            UpdateInfo()
            UpdateButtons()
        End If
    End Sub
End Class
