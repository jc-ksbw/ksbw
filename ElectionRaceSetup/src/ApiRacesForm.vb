Option Strict On
Option Infer On

Imports System.Drawing
Imports System.Windows.Forms

''' <summary>
''' Picks which contests from the API's ballot definition to set up as races. Races built here carry
''' the API's contest and candidate IDs, so results pair exactly afterwards.
''' </summary>
Public Class ApiRacesForm
    Inherits Form

    Private Const PickCol As String = "colPick"

    Private ReadOnly _definition As ApiDefinition
    Private ReadOnly _options As MonitorOptions
    Private ReadOnly _all As List(Of ApiContest)
    Private ReadOnly _picked As New HashSet(Of String)(StringComparer.Ordinal)

    Private grid As DataGridView
    Private cboCounty As ComboBox
    Private cboLevel As ComboBox
    Private txtFind As TextBox
    Private cboWriteIns As ComboBox
    Private lblCount As Label
    Private _filling As Boolean

    ''' <summary>The races to set up, in the definition's order.</summary>
    Public ReadOnly Property Result As New List(Of Race)

    Public Sub New(definition As ApiDefinition, options As MonitorOptions)
        _definition = definition
        _options = options
        _all = MonitorAdapter.SelectableContests(definition)
        For Each c In _all
            _picked.Add(c.Id)
        Next

        Text = $"Set up races from the API  -  ballot version {definition.Version}"
        Font = New Font("Segoe UI", 9.0!)
        AutoScaleMode = AutoScaleMode.Dpi
        ClientSize = New Size(1080, 680)
        MinimumSize = New Size(820, 500)
        StartPosition = FormStartPosition.CenterParent

        BuildLayout()
        Fill()
    End Sub

    Private Shared Function MakeLabel(text As String) As Label
        Return New Label With {.Text = text, .AutoSize = True, .Anchor = AnchorStyles.Left, .Margin = New Padding(0, 7, 4, 0)}
    End Function

    Private Sub BuildLayout()
        SuspendLayout()

        Dim intro As New Label With {
            .Dock = DockStyle.Top, .Height = 38, .Padding = New Padding(10, 8, 10, 0),
            .Text = "Tick the contests to set up in Election Manager. Each race keeps the API's contest ID, so results pair exactly later. Races already set up with the same IDs are simply re-sent."}

        cboCounty = New ComboBox With {.DropDownStyle = ComboBoxStyle.DropDownList, .Width = 150}
        cboCounty.Items.Add("All counties")
        For Each c In _all.SelectMany(Function(x) x.JurisdictionCounties).Distinct().OrderBy(Function(x) x)
            cboCounty.Items.Add(c)
        Next
        cboCounty.SelectedIndex = 0

        cboLevel = New ComboBox With {.DropDownStyle = ComboBoxStyle.DropDownList, .Width = 120}
        cboLevel.Items.AddRange({"All levels", "NATIONAL", "STATE", "COUNTY", "CITY", "DISTRICT"})
        cboLevel.SelectedIndex = 0

        txtFind = New TextBox With {.Width = 180}
        cboWriteIns = New ComboBox With {.DropDownStyle = ComboBoxStyle.DropDownList, .Width = 230}
        cboWriteIns.Items.AddRange({"Write-ins: named + one Write-in line", "Write-ins: one Write-in line only", "Write-ins: leave out"})
        cboWriteIns.SelectedIndex = CInt(_options.WriteIns)

        AddHandler cboCounty.SelectedIndexChanged, Sub() Fill()
        AddHandler cboLevel.SelectedIndexChanged, Sub() Fill()
        AddHandler txtFind.TextChanged, Sub() Fill()
        AddHandler cboWriteIns.SelectedIndexChanged, Sub()
                                                         _options.WriteIns = CType(cboWriteIns.SelectedIndex, WriteInMode)
                                                         Fill()
                                                     End Sub

        Dim filters As New FlowLayoutPanel With {.Dock = DockStyle.Top, .AutoSize = True, .Padding = New Padding(8, 4, 8, 4)}
        filters.Controls.AddRange({MakeLabel("County"), cboCounty, MakeLabel("Level"), cboLevel,
                                   MakeLabel("Find"), txtFind, cboWriteIns})

        grid = New DataGridView With {
            .Dock = DockStyle.Fill, .AllowUserToAddRows = False, .AllowUserToDeleteRows = False,
            .AllowUserToResizeRows = False, .RowHeadersVisible = False, .SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            .BackgroundColor = SystemColors.Window, .BorderStyle = BorderStyle.FixedSingle,
            .EditMode = DataGridViewEditMode.EditOnEnter, .AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None}
        grid.Columns.Add(New DataGridViewCheckBoxColumn With {.Name = PickCol, .HeaderText = "Set up", .Width = 55})
        For Each col In {Tuple.Create("colOffice", "Office / measure", 240), Tuple.Create("colWhere", "Where", 230),
                         Tuple.Create("colLevel", "Level", 75), Tuple.Create("colType", "Type", 85),
                         Tuple.Create("colSeat", "Seat", 110), Tuple.Create("colWin", "Win", 40),
                         Tuple.Create("colCands", "Cands", 50), Tuple.Create("colCounties", "Counties", 150),
                         Tuple.Create("colId", "Contest ID", 170)}
            grid.Columns.Add(New DataGridViewTextBoxColumn With {.Name = col.Item1, .HeaderText = col.Item2, .Width = col.Item3, .ReadOnly = True})
        Next
        AddHandler grid.CurrentCellDirtyStateChanged, Sub()
                                                          If grid.IsCurrentCellDirty Then grid.CommitEdit(DataGridViewDataErrorContexts.Commit)
                                                      End Sub
        AddHandler grid.CellValueChanged, AddressOf OnPickChanged

        Dim btnAll As New Button With {.Text = "Tick all shown", .AutoSize = True}
        Dim btnNone As New Button With {.Text = "Untick all shown", .AutoSize = True}
        AddHandler btnAll.Click, Sub() SetShown(True)
        AddHandler btnNone.Click, Sub() SetShown(False)
        lblCount = New Label With {.AutoSize = True, .Margin = New Padding(16, 8, 0, 0)}

        Dim ok As New Button With {.Text = "Set up races", .DialogResult = DialogResult.OK, .AutoSize = True, .MinimumSize = New Size(110, 29)}
        Dim cancel As New Button With {.Text = "Cancel", .DialogResult = DialogResult.Cancel, .AutoSize = True, .MinimumSize = New Size(90, 29)}

        Dim left As New FlowLayoutPanel With {.Dock = DockStyle.Left, .AutoSize = True}
        left.Controls.AddRange({btnAll, btnNone, lblCount})
        Dim right As New FlowLayoutPanel With {.Dock = DockStyle.Right, .AutoSize = True, .FlowDirection = FlowDirection.RightToLeft}
        right.Controls.AddRange({cancel, ok})
        Dim actions As New Panel With {.Dock = DockStyle.Bottom, .Height = 40, .Padding = New Padding(8, 5, 8, 5)}
        actions.Controls.Add(left)
        actions.Controls.Add(right)

        Dim body As New Panel With {.Dock = DockStyle.Fill, .Padding = New Padding(8, 4, 8, 0)}
        body.Controls.Add(grid)

        Controls.Add(body)
        Controls.Add(actions)
        Controls.Add(filters)
        Controls.Add(intro)
        AcceptButton = ok
        CancelButton = cancel
        ResumeLayout(True)
    End Sub

    Private Function Shown_() As List(Of ApiContest)
        Dim county = If(cboCounty.SelectedIndex > 0, Convert.ToString(cboCounty.SelectedItem), "")
        Dim level = If(cboLevel.SelectedIndex > 0, Convert.ToString(cboLevel.SelectedItem), "")
        Dim find = FeedNode.Norm(txtFind.Text)
        Return _all.Where(Function(c)
                              If county <> "" AndAlso Not c.JurisdictionCounties.Contains(county) Then Return False
                              If level <> "" AndAlso MonitorAdapter.LevelOf(c) <> level Then Return False
                              If find <> "" AndAlso Not FeedNode.Norm(MonitorAdapter.OfficeOf(c) & c.Jurisdiction & c.Id).Contains(find) Then Return False
                              Return True
                          End Function).ToList()
    End Function

    Private Sub Fill()
        _filling = True
        Try
            grid.Rows.Clear()
            For Each c In Shown_()
                Dim r = MonitorAdapter.ToRace(c, _options)
                Dim where = String.Join(", ", {r.City, r.County, r.District}.Where(Function(x) x <> ""))
                Dim idx = grid.Rows.Add(_picked.Contains(c.Id), MonitorAdapter.OfficeOf(c),
                                        If(where <> "", where, If(c.Jurisdiction <> "", c.Jurisdiction, "California")),
                                        r.Level, If(r.RaceType <> "", r.RaceType, c.ContestType),
                                        (r.SeatName & " " & r.SeatNumber).Trim(), r.Winners, r.Candidates.Count,
                                        String.Join(", ", c.JurisdictionCounties), c.Id)
                grid.Rows(idx).Tag = c
                If c.Status = "unopposed" Then grid.Rows(idx).DefaultCellStyle.ForeColor = Color.DimGray
            Next
        Finally
            _filling = False
        End Try
        UpdateCount()
    End Sub

    Private Sub UpdateCount()
        lblCount.Text = $"{_picked.Count} of {_all.Count} contests ticked; {grid.Rows.Count} shown"
    End Sub

    Private Sub OnPickChanged(sender As Object, e As DataGridViewCellEventArgs)
        If _filling OrElse e.RowIndex < 0 OrElse grid.Columns(e.ColumnIndex).Name <> PickCol Then Return
        Dim c = TryCast(grid.Rows(e.RowIndex).Tag, ApiContest)
        If c Is Nothing Then Return
        If CBool(grid.Rows(e.RowIndex).Cells(PickCol).Value) Then _picked.Add(c.Id) Else _picked.Remove(c.Id)
        UpdateCount()
    End Sub

    Private Sub SetShown(value As Boolean)
        _filling = True
        Try
            For Each row As DataGridViewRow In grid.Rows
                Dim c = TryCast(row.Tag, ApiContest)
                If c Is Nothing Then Continue For
                row.Cells(PickCol).Value = value
                If value Then _picked.Add(c.Id) Else _picked.Remove(c.Id)
            Next
        Finally
            _filling = False
        End Try
        UpdateCount()
    End Sub

    Private Sub Build()
        Result.Clear()
        For Each c In _all
            If _picked.Contains(c.Id) Then Result.Add(MonitorAdapter.ToRace(c, _options))
        Next
    End Sub

    Protected Overrides Sub OnFormClosing(e As FormClosingEventArgs)
        ' Built here rather than on the button's Click, which can run after the form has closed.
        If DialogResult = DialogResult.OK Then Build()
        If DialogResult = DialogResult.OK AndAlso Result.Count = 0 Then
            MessageBox.Show(Me, "Tick at least one contest.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information)
            e.Cancel = True
        End If
        MyBase.OnFormClosing(e)
    End Sub
End Class
