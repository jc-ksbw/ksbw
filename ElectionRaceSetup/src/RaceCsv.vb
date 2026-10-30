Option Strict On
Option Infer On

Imports System.Text
Imports System.Windows.Forms

''' <summary>Writes races back out as a CSV the app can read again, keeping the API's IDs.</summary>
Public Module RaceCsv

    Private Function Q(s As String) As String
        Return """" & If(s, "").Replace("""", """""") & """"
    End Function

    Public Function Build(races As IEnumerable(Of Race)) As String
        Dim sb As New StringBuilder
        sb.AppendLine("Race ID,Feed ID,Level,State,County,City,District,Office,Race Party,Type,Seat Name,Seat Number,Winners,Total Precincts,Candidate,First,Middle,Last,Suffix,Party,Incumbent,Candidate ID")
        For Each r In races
            For Each c In r.Candidates
                sb.AppendLine(String.Join(",", {
                    Q(r.ID), Q(r.FeedId), Q(r.Level), Q(r.State), Q(r.County), Q(r.City), Q(r.District),
                    Q(r.Office), Q(r.Party), Q(r.RaceType), Q(r.SeatName), Q(r.SeatNumber),
                    Q(r.Winners.ToString()), Q(r.PrecinctsTotal.ToString()),
                    Q(c.FullName), Q(c.FirstName), Q(c.MiddleName), Q(c.LastName), Q(c.Suffix), Q(c.Party),
                    Q(If(c.Incumbent, "Y", "")), Q(c.FeedId)}))
            Next
        Next
        Return sb.ToString()
    End Function
End Module

''' <summary>A one-line question with a text box.</summary>
Public Module Prompt

    ''' <returns>The text entered, or Nothing if the user cancelled.</returns>
    Public Function Ask(owner As IWin32Window, title As String, question As String, value As String) As String
        Using dlg As New Form With {
            .Text = title, .FormBorderStyle = FormBorderStyle.FixedDialog, .StartPosition = FormStartPosition.CenterParent,
            .MaximizeBox = False, .MinimizeBox = False, .ShowInTaskbar = False, .ClientSize = New Drawing.Size(520, 120),
            .Font = New Drawing.Font("Segoe UI", 9.0!)}

            Dim lbl As New Label With {.Text = question, .Dock = DockStyle.Top, .Height = 26, .Padding = New Padding(12, 10, 12, 0)}
            Dim box As New TextBox With {.Text = value, .Width = 492, .Left = 12, .Top = 42, .Anchor = AnchorStyles.Left Or AnchorStyles.Right Or AnchorStyles.Top}
            Dim ok As New Button With {.Text = "OK", .DialogResult = DialogResult.OK, .Width = 85, .Top = 78, .Left = 330, .Anchor = AnchorStyles.Right Or AnchorStyles.Bottom}
            Dim cancel As New Button With {.Text = "Cancel", .DialogResult = DialogResult.Cancel, .Width = 85, .Top = 78, .Left = 421, .Anchor = AnchorStyles.Right Or AnchorStyles.Bottom}

            dlg.Controls.AddRange({box, ok, cancel, lbl})
            dlg.AcceptButton = ok
            dlg.CancelButton = cancel
            box.SelectAll()
            If dlg.ShowDialog(owner) <> DialogResult.OK Then Return Nothing
            Return box.Text.Trim()
        End Using
    End Function
End Module
