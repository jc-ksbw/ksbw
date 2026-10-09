Option Strict On
Option Infer On

Imports System.Drawing
Imports System.Windows.Forms

''' <summary>Lets the user say which CSV column feeds which field.</summary>
Public Class MappingForm
    Inherits Form

    Private ReadOnly _combos As New Dictionary(Of RaceField, ComboBox)
    Private ReadOnly _headers As String()

    Public ReadOnly Property Result As Dictionary(Of RaceField, Integer)
        Get
            Dim map As New Dictionary(Of RaceField, Integer)
            For Each kv In _combos
                If kv.Value.SelectedIndex > 0 Then map(kv.Key) = kv.Value.SelectedIndex - 1
            Next
            Return map
        End Get
    End Property

    Public Sub New(headers As String(), current As Dictionary(Of RaceField, Integer))
        _headers = headers
        Text = "Map CSV columns"
        StartPosition = FormStartPosition.CenterParent
        FormBorderStyle = FormBorderStyle.FixedDialog
        MaximizeBox = False
        MinimizeBox = False
        ShowInTaskbar = False
        AutoScaleMode = AutoScaleMode.Dpi
        Font = New Font("Segoe UI", 9.0!)
        ClientSize = New Size(520, 640)

        Dim intro As New Label With {
            .Text = "Pick the CSV column for each field. Fields marked * are required (candidate: full name, or last name). The file has one row per candidate; rows with the same Race ID - or identical race fields - become one race.",
            .Dock = DockStyle.Top, .Height = 58, .Padding = New Padding(10, 8, 10, 0)
        }

        Dim grid As New TableLayoutPanel With {
            .Dock = DockStyle.Fill, .AutoScroll = True, .Padding = New Padding(10, 6, 10, 6),
            .ColumnCount = 2, .GrowStyle = TableLayoutPanelGrowStyle.AddRows
        }
        grid.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 45))
        grid.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 55))

        For Each def In RaceImporter.Fields
            Dim required = def.Field = RaceField.Office
            Dim lbl As New Label With {
                .Text = def.Label & If(required, " *", ""), .AutoSize = False, .Dock = DockStyle.Fill,
                .TextAlign = ContentAlignment.MiddleLeft, .Height = 28
            }
            Dim cbo As New ComboBox With {.DropDownStyle = ComboBoxStyle.DropDownList, .Dock = DockStyle.Fill}
            cbo.Items.Add("(not in file)")
            For i = 0 To headers.Length - 1
                cbo.Items.Add(If(String.IsNullOrWhiteSpace(headers(i)), $"(column {i + 1})", headers(i)))
            Next
            Dim idx As Integer
            cbo.SelectedIndex = If(current.TryGetValue(def.Field, idx), idx + 1, 0)
            _combos(def.Field) = cbo
            grid.Controls.Add(lbl)
            grid.Controls.Add(cbo)
        Next

        Dim ok As New Button With {.Text = "OK", .DialogResult = DialogResult.OK, .Width = 90}
        Dim cancel As New Button With {.Text = "Cancel", .DialogResult = DialogResult.Cancel, .Width = 90}
        Dim buttons As New FlowLayoutPanel With {
            .Dock = DockStyle.Bottom, .FlowDirection = FlowDirection.RightToLeft, .Height = 44, .Padding = New Padding(10, 6, 10, 6)
        }
        buttons.Controls.Add(cancel)
        buttons.Controls.Add(ok)

        Controls.Add(grid)
        Controls.Add(buttons)
        Controls.Add(intro)
        AcceptButton = ok
        CancelButton = cancel
    End Sub

    Protected Overrides Sub OnFormClosing(e As FormClosingEventArgs)
        If DialogResult = DialogResult.OK Then
            Dim missing = RaceImporter.MissingRequired(Result)
            If missing.Count > 0 Then
                MessageBox.Show(Me, "Still needed: " & String.Join(", ", missing), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning)
                e.Cancel = True
            End If
        End If
        MyBase.OnFormClosing(e)
    End Sub
End Class
