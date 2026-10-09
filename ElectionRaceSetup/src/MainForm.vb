Option Strict On
Option Infer On

Imports System.Drawing
Imports System.Globalization
Imports System.IO
Imports System.Windows.Forms

Public Class MainForm
    Inherits Form

    Private Const CheckCol As String = "colSend"
    Private ReadOnly _settings As AppSettings = AppSettings.Load()
    Private ReadOnly _client As New MosClient

    Private _rows As List(Of String())
    Private _map As New Dictionary(Of RaceField, Integer)
    Private _mapHeaderKey As String = ""
    Private _races As New List(Of Race)

    Private _sendTotal As Integer
    Private _sendDone As Integer

    ' Controls
    Private txtHost As TextBox
    Private numPort As NumericUpDown
    Private btnConnect As Button
    Private dtpElection As DateTimePicker
    Private txtSource As TextBox
    Private ckTest As CheckBox
    Private numDelay As NumericUpDown
    Private txtCsv As TextBox
    Private btnBrowse As Button
    Private btnReload As Button
    Private btnMap As Button
    Private gridRaces As DataGridView
    Private gridCands As DataGridView
    Private lblRaceSummary As Label
    Private btnCheckAll As Button
    Private btnUncheckAll As Button
    Private btnPreview As Button
    Private btnSend As Button
    Private btnCancel As Button
    Private txtLog As TextBox
    Private statusConn As ToolStripStatusLabel
    Private statusSend As ToolStripStatusLabel

    Public Sub New()
        Text = "Election Race Setup"
        Font = New Font("Segoe UI", 9.0!)
        AutoScaleMode = AutoScaleMode.Dpi
        ClientSize = New Size(1180, 820)
        MinimumSize = New Size(960, 620)
        StartPosition = FormStartPosition.CenterScreen

        BuildLayout()

        AddHandler _client.Log, Sub(t) UiLog(t)
        AddHandler _client.ConnectionChanged, Sub(c) UiInvoke(Sub() UpdateConnectionUi())
        AddHandler _client.MessageSent, Sub(tag) UiInvoke(Sub() OnMessageSent(tag))

        txtHost.Text = _settings.Host
        numPort.Value = Math.Max(numPort.Minimum, Math.Min(numPort.Maximum, _settings.Port))
        txtSource.Text = _settings.Source
        ckTest.Checked = _settings.IsTest
        numDelay.Value = Math.Max(numDelay.Minimum, Math.Min(numDelay.Maximum, _settings.DelayMs))
        txtCsv.Text = _settings.LastCsv

        Dim d As Date
        If Date.TryParseExact(_settings.ElectionDate, "M/d/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, d) Then
            dtpElection.Value = d
        End If

        UpdateConnectionUi()
        UpdateActionState()
    End Sub

#Region "Layout"

    Private Shared Function MakeLabel(text As String) As Label
        Return New Label With {.Text = text, .AutoSize = True, .Anchor = AnchorStyles.Left, .Margin = New Padding(0, 6, 4, 0)}
    End Function

    Private Sub BuildLayout()
        SuspendLayout()

        ' --- Connection + election settings -------------------------------------------------
        txtHost = New TextBox With {.Width = 150}
        numPort = New NumericUpDown With {.Minimum = 1, .Maximum = 65535, .Value = 10387, .Width = 70}
        btnConnect = New Button With {.Text = "Connect", .AutoSize = True, .MinimumSize = New Size(90, 27)}
        AddHandler btnConnect.Click, AddressOf OnConnectClick

        dtpElection = New DateTimePicker With {.Format = DateTimePickerFormat.Custom, .CustomFormat = "M/d/yyyy", .Width = 100}
        txtSource = New TextBox With {.Width = 110}
        ckTest = New CheckBox With {.Text = "Send as TEST races", .AutoSize = True, .Margin = New Padding(0, 6, 0, 0)}
        numDelay = New NumericUpDown With {.Minimum = 0, .Maximum = 10000, .Increment = 100, .Value = 500, .Width = 65}

        Dim connGroup As New GroupBox With {.Text = "Election Manager (MOS server)", .AutoSize = True, .Padding = New Padding(8, 4, 8, 8)}
        Dim connFlow As New FlowLayoutPanel With {.AutoSize = True, .WrapContents = False}
        connFlow.Controls.AddRange({MakeLabel("Host"), txtHost, MakeLabel("Port"), numPort, btnConnect})
        connGroup.Controls.Add(connFlow)

        Dim elecGroup As New GroupBox With {.Text = "Election", .AutoSize = True, .Padding = New Padding(8, 4, 8, 8)}
        Dim elecFlow As New FlowLayoutPanel With {.AutoSize = True, .WrapContents = False}
        elecFlow.Controls.AddRange({MakeLabel("Date"), dtpElection, MakeLabel("Source"), txtSource, ckTest,
                                    MakeLabel("Delay (ms)"), numDelay})
        elecGroup.Controls.Add(elecFlow)

        Dim topFlow As New FlowLayoutPanel With {.Dock = DockStyle.Top, .AutoSize = True, .Padding = New Padding(8, 4, 8, 0)}
        topFlow.Controls.AddRange({connGroup, elecGroup})

        ' --- CSV row ------------------------------------------------------------------------
        txtCsv = New TextBox With {.Dock = DockStyle.Fill, .ReadOnly = True}
        btnBrowse = New Button With {.Text = "Open CSV...", .AutoSize = True}
        btnReload = New Button With {.Text = "Reload", .AutoSize = True}
        btnMap = New Button With {.Text = "Map columns...", .AutoSize = True}
        AddHandler btnBrowse.Click, AddressOf OnBrowseClick
        AddHandler btnReload.Click, Sub() LoadCsv(txtCsv.Text, False)
        AddHandler btnMap.Click, AddressOf OnMapClick

        Dim csvRow As New TableLayoutPanel With {.Dock = DockStyle.Top, .AutoSize = True, .ColumnCount = 5, .Padding = New Padding(8, 6, 8, 0)}
        csvRow.ColumnStyles.Add(New ColumnStyle(SizeType.AutoSize))
        csvRow.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
        csvRow.ColumnStyles.Add(New ColumnStyle(SizeType.AutoSize))
        csvRow.ColumnStyles.Add(New ColumnStyle(SizeType.AutoSize))
        csvRow.ColumnStyles.Add(New ColumnStyle(SizeType.AutoSize))
        csvRow.Controls.Add(MakeLabel("Races CSV"), 0, 0)
        csvRow.Controls.Add(txtCsv, 1, 0)
        csvRow.Controls.Add(btnBrowse, 2, 0)
        csvRow.Controls.Add(btnReload, 3, 0)
        csvRow.Controls.Add(btnMap, 4, 0)

        ' --- Races grid + candidate grid ----------------------------------------------------
        gridRaces = MakeGrid()
        gridRaces.MultiSelect = False
        gridRaces.EditMode = DataGridViewEditMode.EditOnEnter
        gridRaces.Columns.Add(New DataGridViewCheckBoxColumn With {.Name = CheckCol, .HeaderText = "Send", .Width = 45})
        AddTextColumn(gridRaces, "colId", "Race ID", 70)
        AddTextColumn(gridRaces, "colOffice", "Office", 200)
        AddTextColumn(gridRaces, "colLevel", "Level", 75)
        AddTextColumn(gridRaces, "colState", "State", 45)
        AddTextColumn(gridRaces, "colCounty", "County", 90)
        AddTextColumn(gridRaces, "colCity", "City", 90)
        AddTextColumn(gridRaces, "colDistrict", "District", 80)
        AddTextColumn(gridRaces, "colParty", "Party", 50)
        AddTextColumn(gridRaces, "colType", "Type", 80)
        AddTextColumn(gridRaces, "colSeat", "Seat", 90)
        AddTextColumn(gridRaces, "colWinners", "Win", 40)
        AddTextColumn(gridRaces, "colPrecincts", "Prec.", 50)
        AddTextColumn(gridRaces, "colCands", "Cands", 50)
        AddTextColumn(gridRaces, "colNotes", "Notes", 300).AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill
        For Each col As DataGridViewColumn In gridRaces.Columns
            If col.Name <> CheckCol Then col.ReadOnly = True
        Next
        AddHandler gridRaces.SelectionChanged, AddressOf OnRaceSelected
        AddHandler gridRaces.CurrentCellDirtyStateChanged, Sub()
                                                               If gridRaces.IsCurrentCellDirty Then gridRaces.CommitEdit(DataGridViewDataErrorContexts.Commit)
                                                           End Sub
        AddHandler gridRaces.CellValueChanged, Sub() UpdateActionState()

        gridCands = MakeGrid()
        AddTextColumn(gridCands, "cName", "Candidate", 250)
        AddTextColumn(gridCands, "cFirst", "First", 100)
        AddTextColumn(gridCands, "cMiddle", "Middle", 80)
        AddTextColumn(gridCands, "cLast", "Last", 100)
        AddTextColumn(gridCands, "cSuffix", "Suffix", 60)
        AddTextColumn(gridCands, "cParty", "Party", 60)
        AddTextColumn(gridCands, "cInc", "Incumbent", 70)

        lblRaceSummary = New Label With {.Dock = DockStyle.Top, .Height = 22, .Padding = New Padding(2, 4, 0, 0), .Text = "Open a CSV to see the races."}

        Dim split As New SplitContainer With {.Dock = DockStyle.Fill, .Orientation = Orientation.Horizontal, .SplitterDistance = 300}
        Dim racesPanel As New Panel With {.Dock = DockStyle.Fill}
        racesPanel.Controls.Add(gridRaces)
        racesPanel.Controls.Add(lblRaceSummary)
        split.Panel1.Controls.Add(racesPanel)
        Dim candLabel As New Label With {.Dock = DockStyle.Top, .Height = 22, .Padding = New Padding(2, 4, 0, 0), .Text = "Candidates in the selected race"}
        split.Panel2.Controls.Add(gridCands)
        split.Panel2.Controls.Add(candLabel)

        Dim body As New Panel With {.Dock = DockStyle.Fill, .Padding = New Padding(8, 6, 8, 0)}
        body.Controls.Add(split)

        ' --- Action bar ---------------------------------------------------------------------
        btnCheckAll = New Button With {.Text = "Check all", .AutoSize = True}
        btnUncheckAll = New Button With {.Text = "Uncheck all", .AutoSize = True}
        btnPreview = New Button With {.Text = "Preview XML...", .AutoSize = True}
        btnSend = New Button With {.Text = "Send checked races", .AutoSize = True, .MinimumSize = New Size(150, 29)}
        btnCancel = New Button With {.Text = "Cancel pending", .AutoSize = True}
        AddHandler btnCheckAll.Click, Sub() SetAllChecks(True)
        AddHandler btnUncheckAll.Click, Sub() SetAllChecks(False)
        AddHandler btnPreview.Click, AddressOf OnPreviewClick
        AddHandler btnSend.Click, AddressOf OnSendClick
        AddHandler btnCancel.Click, AddressOf OnCancelClick

        Dim actions As New FlowLayoutPanel With {.Dock = DockStyle.Bottom, .AutoSize = True, .Padding = New Padding(8, 4, 8, 4)}
        actions.Controls.AddRange({btnCheckAll, btnUncheckAll, btnPreview, btnSend, btnCancel})

        ' --- Log + status -------------------------------------------------------------------
        txtLog = New TextBox With {
            .Multiline = True, .ReadOnly = True, .ScrollBars = ScrollBars.Vertical, .Dock = DockStyle.Fill,
            .Font = New Font("Consolas", 8.5!), .BackColor = SystemColors.Window
        }
        Dim logPanel As New Panel With {.Dock = DockStyle.Bottom, .Height = 130, .Padding = New Padding(8, 0, 8, 4)}
        logPanel.Controls.Add(txtLog)

        Dim status As New StatusStrip
        statusConn = New ToolStripStatusLabel With {.Text = "Not connected"}
        statusSend = New ToolStripStatusLabel With {.Spring = True, .TextAlign = ContentAlignment.MiddleRight}
        status.Items.AddRange({statusConn, statusSend})

        ' Dock order: later-added controls are laid out first.
        Controls.Add(body)
        Controls.Add(actions)
        Controls.Add(logPanel)
        Controls.Add(status)
        Controls.Add(csvRow)
        Controls.Add(topFlow)

        ResumeLayout(True)
    End Sub

    Private Shared Function MakeGrid() As DataGridView
        Return New DataGridView With {
            .Dock = DockStyle.Fill, .AllowUserToAddRows = False, .AllowUserToDeleteRows = False,
            .AllowUserToResizeRows = False, .RowHeadersVisible = False, .SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            .BackgroundColor = SystemColors.Window, .BorderStyle = BorderStyle.FixedSingle, .ReadOnly = False,
            .AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None
        }
    End Function

    Private Shared Function AddTextColumn(grid As DataGridView, name As String, header As String, width As Integer) As DataGridViewColumn
        Dim col As New DataGridViewTextBoxColumn With {.Name = name, .HeaderText = header, .Width = width, .ReadOnly = True}
        grid.Columns.Add(col)
        Return col
    End Function

#End Region

#Region "Helpers"

    Private Sub UiInvoke(action As Action)
        If IsDisposed OrElse Not IsHandleCreated Then Return
        Try
            If InvokeRequired Then BeginInvoke(action) Else action()
        Catch ex As ObjectDisposedException
        End Try
    End Sub

    Private Sub UiLog(text As String)
        UiInvoke(Sub()
                     txtLog.AppendText(Date.Now.ToString("HH:mm:ss") & "  " & text & vbCrLf)
                 End Sub)
    End Sub

    Private Function ElectionDateText() As String
        Return dtpElection.Value.ToString("M/d/yyyy", CultureInfo.InvariantCulture)
    End Function

    Private Sub UpdateConnectionUi()
        If _client.IsConnected Then
            statusConn.Text = $"Connected to {txtHost.Text.Trim()}:{numPort.Value}"
            statusConn.ForeColor = Color.DarkGreen
        ElseIf _client.IsRunning Then
            statusConn.Text = "Connecting..."
            statusConn.ForeColor = Color.DarkOrange
        Else
            statusConn.Text = "Not connected"
            statusConn.ForeColor = SystemColors.ControlText
        End If
        btnConnect.Text = If(_client.IsRunning, "Disconnect", "Connect")
        txtHost.Enabled = Not _client.IsRunning
        numPort.Enabled = Not _client.IsRunning
        UpdateActionState()
    End Sub

    Private Sub UpdateActionState()
        Dim checkedCount = CheckedRaces().Count
        btnSend.Text = If(checkedCount > 0, $"Send {checkedCount} race{If(checkedCount = 1, "", "s")}", "Send checked races")
        btnSend.Enabled = checkedCount > 0 AndAlso _client.IsConnected
        btnPreview.Enabled = SelectedRace() IsNot Nothing
        btnCheckAll.Enabled = _races.Count > 0
        btnUncheckAll.Enabled = _races.Count > 0
        btnMap.Enabled = _rows IsNot Nothing
        btnReload.Enabled = txtCsv.Text <> ""
        btnCancel.Enabled = _client.PendingCount > 0
    End Sub

    Private Function SelectedRace() As Race
        If gridRaces.CurrentRow Is Nothing Then Return Nothing
        Return TryCast(gridRaces.CurrentRow.Tag, Race)
    End Function

    Private Function CheckedRaces() As List(Of Race)
        Dim list As New List(Of Race)
        For Each row As DataGridViewRow In gridRaces.Rows
            Dim r = TryCast(row.Tag, Race)
            If r IsNot Nothing AndAlso r.Errors.Count = 0 AndAlso CBool(row.Cells(CheckCol).Value) Then list.Add(r)
        Next
        Return list
    End Function

    Private Sub SaveSettings()
        _settings.Host = txtHost.Text.Trim()
        _settings.Port = CInt(numPort.Value)
        _settings.Source = txtSource.Text.Trim()
        _settings.IsTest = ckTest.Checked
        _settings.DelayMs = CInt(numDelay.Value)
        _settings.LastCsv = txtCsv.Text
        _settings.ElectionDate = ElectionDateText()
        _settings.Save()
    End Sub

#End Region

#Region "CSV loading"

    Private Sub OnBrowseClick(sender As Object, e As EventArgs)
        Using dlg As New OpenFileDialog With {.Filter = "CSV files (*.csv;*.txt)|*.csv;*.txt|All files (*.*)|*.*", .Title = "Open races CSV"}
            If txtCsv.Text <> "" AndAlso File.Exists(txtCsv.Text) Then dlg.InitialDirectory = Path.GetDirectoryName(txtCsv.Text)
            If dlg.ShowDialog(Me) = DialogResult.OK Then LoadCsv(dlg.FileName, True)
        End Using
    End Sub

    Private Sub LoadCsv(path As String, isNewFile As Boolean)
        Try
            _rows = CsvParser.ReadFile(path)
        Catch ex As Exception
            MessageBox.Show(Me, "Could not read the file:" & vbCrLf & ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error)
            Return
        End Try
        txtCsv.Text = path

        If _rows.Count < 2 Then
            MessageBox.Show(Me, "The file needs a header row and at least one data row.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning)
            _races = New List(Of Race)
            RefreshRaceGrid(New ImportResult)
            Return
        End If

        ' Keep a hand-made mapping when the same file is reloaded; otherwise guess from the headers.
        Dim headers = _rows(0)
        Dim headerKey = String.Join("|", headers)
        If headerKey <> _mapHeaderKey OrElse isNewFile Then
            _map = RaceImporter.AutoMap(headers)
            _mapHeaderKey = headerKey
            If RaceImporter.MissingRequired(_map).Count > 0 Then
                If Not ShowMapping() Then
                    _races = New List(Of Race)
                    RefreshRaceGrid(New ImportResult)
                    UiLog("Import cancelled: columns not mapped.")
                    Return
                End If
            End If
        End If
        Rebuild()
        SaveSettings()
    End Sub

    ''' <returns>True if the user confirmed a usable mapping.</returns>
    Private Function ShowMapping() As Boolean
        Using dlg As New MappingForm(_rows(0), _map)
            If dlg.ShowDialog(Me) <> DialogResult.OK Then Return False
            _map = dlg.Result
            Return True
        End Using
    End Function

    Private Sub OnMapClick(sender As Object, e As EventArgs)
        If _rows Is Nothing Then Return
        If ShowMapping() Then Rebuild()
    End Sub

    Private Sub Rebuild()
        Dim result = RaceImporter.Build(_rows, _map)
        _races = result.Races
        RefreshRaceGrid(result)
        UiLog($"Loaded {_races.Count} race(s), {_races.Sum(Function(r) r.Candidates.Count)} candidate(s) from {Path.GetFileName(txtCsv.Text)}.")
        For Each m In result.Messages
            UiLog("  ! " & m)
        Next
    End Sub

    Private Sub RefreshRaceGrid(result As ImportResult)
        gridRaces.Rows.Clear()
        gridCands.Rows.Clear()
        For Each r In _races
            Dim notes = String.Join("  ", r.Errors.Select(Function(s) "ERROR: " & s).Concat(r.Warnings))
            Dim idx = gridRaces.Rows.Add(r.Errors.Count = 0, r.ID, r.Office, r.Level, r.State, r.County, r.City, r.District,
                                         r.Party, r.RaceType, (r.SeatName & " " & r.SeatNumber).Trim(), r.Winners,
                                         r.PrecinctsTotal, r.Candidates.Count, notes)
            Dim row = gridRaces.Rows(idx)
            row.Tag = r
            If r.Errors.Count > 0 Then
                row.DefaultCellStyle.BackColor = Color.FromArgb(255, 220, 220)
                row.Cells(CheckCol).ReadOnly = True
            ElseIf r.Warnings.Count > 0 Then
                row.DefaultCellStyle.BackColor = Color.FromArgb(255, 247, 210)
            End If
        Next

        Dim errs = _races.Where(Function(r) r.Errors.Count > 0).Count()
        Dim warns = _races.Where(Function(r) r.Errors.Count = 0 AndAlso r.Warnings.Count > 0).Count()
        lblRaceSummary.Text = If(_races.Count = 0, If(result.Messages.Count > 0, result.Messages(0), "No races found."),
                                 $"{_races.Count} races, {_races.Sum(Function(r) r.Candidates.Count)} candidates" &
                                 If(errs > 0, $"  -  {errs} with errors (red, cannot be sent)", "") &
                                 If(warns > 0, $"  -  {warns} with warnings (yellow)", ""))
        OnRaceSelected(Nothing, EventArgs.Empty)
        UpdateActionState()
    End Sub

    Private Sub OnRaceSelected(sender As Object, e As EventArgs)
        gridCands.Rows.Clear()
        Dim r = SelectedRace()
        If r IsNot Nothing Then
            For Each c In r.Candidates
                gridCands.Rows.Add(c.DisplayName, c.FirstName, c.MiddleName, c.LastName, c.Suffix, c.Party, If(c.Incumbent, "Yes", ""))
            Next
        End If
        btnPreview.Enabled = r IsNot Nothing
    End Sub

    Private Sub SetAllChecks(value As Boolean)
        For Each row As DataGridViewRow In gridRaces.Rows
            Dim r = TryCast(row.Tag, Race)
            If r IsNot Nothing AndAlso r.Errors.Count = 0 Then row.Cells(CheckCol).Value = value
        Next
        UpdateActionState()
    End Sub

#End Region

#Region "Connect / send"

    Private Sub OnConnectClick(sender As Object, e As EventArgs)
        If _client.IsRunning Then
            _client.Stop()
            UiLog("Disconnected.")
        Else
            If txtHost.Text.Trim() = "" Then
                MessageBox.Show(Me, "Enter the MOS server host name.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information)
                Return
            End If
            SaveSettings()
            _client.Start(txtHost.Text.Trim(), CInt(numPort.Value))
        End If
        UpdateConnectionUi()
    End Sub

    Private Function BuildXml(r As Race) As String
        Return WireBuilder.BuildRaceXml(r, ElectionDateText(), txtSource.Text.Trim(), ckTest.Checked)
    End Function

    Private Sub OnPreviewClick(sender As Object, e As EventArgs)
        Dim r = SelectedRace()
        If r Is Nothing Then Return
        Dim xml = BuildXml(r)
        Dim text = "Sent as:  " & WireBuilder.Command & "<time>=WIREDATA=" & vbCrLf & vbCrLf & xml.Replace(vbLf, vbCrLf)

        Using dlg As New Form With {.Text = "Preview - " & r.Office, .Size = New Size(640, 700), .StartPosition = FormStartPosition.CenterParent}
            Dim box As New TextBox With {
                .Multiline = True, .ReadOnly = True, .ScrollBars = ScrollBars.Both, .WordWrap = False,
                .Dock = DockStyle.Fill, .Font = New Font("Consolas", 9.0!), .Text = text
            }
            box.Select(0, 0)
            dlg.Controls.Add(box)
            dlg.ShowDialog(Me)
        End Using
    End Sub

    Private Sub OnSendClick(sender As Object, e As EventArgs)
        If Not _client.IsConnected Then
            MessageBox.Show(Me, "Connect to the MOS server first.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information)
            Return
        End If
        If txtSource.Text.Trim() = "" Then
            MessageBox.Show(Me, "Enter a Source name (for example ""CSV""). Keep it the same every time so races update instead of duplicating.",
                            Text, MessageBoxButtons.OK, MessageBoxIcon.Information)
            Return
        End If

        Dim toSend = CheckedRaces()
        If toSend.Count = 0 Then Return

        Dim msg = $"Send {toSend.Count} race(s) to {txtHost.Text.Trim()}?" & vbCrLf & vbCrLf &
                  $"Election date: {ElectionDateText()}   (this election must already exist in Election Manager)" & vbCrLf &
                  $"Source: {txtSource.Text.Trim()}" & vbCrLf &
                  $"Mode: {If(ckTest.Checked, "TEST", "LIVE (TEST = NO)")}"
        If MessageBox.Show(Me, msg, "Send races", MessageBoxButtons.OKCancel,
                           If(ckTest.Checked, MessageBoxIcon.Question, MessageBoxIcon.Warning)) <> DialogResult.OK Then Return

        SaveSettings()
        _client.SendDelayMs = CInt(numDelay.Value)
        _sendTotal += toSend.Count
        For Each r In toSend
            _client.Enqueue(WireBuilder.BuildMessage(BuildXml(r), Date.Now), r.ID)
        Next
        UiLog($"Queued {toSend.Count} race(s).")
        UpdateSendStatus()
    End Sub

    Private Sub OnCancelClick(sender As Object, e As EventArgs)
        Dim dropped = _client.ClearQueue()
        _sendTotal -= dropped
        UiLog($"Cancelled {dropped} pending race(s).")
        UpdateSendStatus()
    End Sub

    Private Sub OnMessageSent(tag As Object)
        _sendDone += 1
        UiLog("Sent race " & Convert.ToString(tag))
        UpdateSendStatus()
    End Sub

    Private Sub UpdateSendStatus()
        If _sendDone >= _sendTotal Then
            statusSend.Text = If(_sendTotal = 0, "", $"Sent {_sendDone} race(s)")
            _sendDone = 0
            _sendTotal = 0
        Else
            statusSend.Text = $"Sending {_sendDone} / {_sendTotal}"
        End If
        UpdateActionState()
    End Sub

#End Region

    Protected Overrides Sub OnFormClosing(e As FormClosingEventArgs)
        If _client.PendingCount > 0 Then
            If MessageBox.Show(Me, $"{_client.PendingCount} race(s) have not been sent yet. Quit anyway?", Text,
                               MessageBoxButtons.YesNo, MessageBoxIcon.Warning) <> DialogResult.Yes Then
                e.Cancel = True
                Return
            End If
        End If
        SaveSettings()
        _client.Dispose()
        MyBase.OnFormClosing(e)
    End Sub

    Protected Overrides Sub OnShown(e As EventArgs)
        MyBase.OnShown(e)
        If txtCsv.Text <> "" AndAlso File.Exists(txtCsv.Text) Then LoadCsv(txtCsv.Text, False)
    End Sub

End Class
