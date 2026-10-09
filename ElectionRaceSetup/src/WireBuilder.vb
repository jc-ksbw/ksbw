Option Strict On
Option Infer On

Imports System.Text

''' <summary>
''' Builds the custom-wire messages Election Manager listens for. The layout is identical to
''' SendRace() in the AP_Custom_Wire sample, with a zero vote count for every candidate.
''' </summary>
Public Module WireBuilder

    Public Const Command As String = "$(ELECTION:CLIENT:UPDATE)="

    Private Function Esc(value As String) As String
        If value Is Nothing Then Return ""
        Dim sb As New StringBuilder(value.Length)
        For Each c In value
            Select Case c
                Case "&"c : sb.Append("&amp;")
                Case "<"c : sb.Append("&lt;")
                Case ">"c : sb.Append("&gt;")
                Case """"c : sb.Append("&quot;")
                Case "'"c : sb.Append("&apos;")
                Case ControlChars.Cr, ControlChars.Lf, ControlChars.Tab : sb.Append(" ")
                Case Else
                    If Not Char.IsControl(c) Then sb.Append(c)
            End Select
        Next
        Return sb.ToString().Trim()
    End Function

    ''' <param name="electionDate">m/d/yyyy or yyyyMMdd - must match an election that exists in Election Manager.</param>
    ''' <param name="source">Name of this wire; keep it the same for every send so races update instead of duplicating.</param>
    Public Function BuildRaceXml(race As Race, electionDate As String, source As String, test As Boolean) As String
        Dim o As New StringBuilder
        o.Append("<RACE>").Append(vbLf)
        o.Append(vbTab).Append("<ELECTION>").Append(Esc(electionDate)).Append("</ELECTION>").Append(vbLf)
        o.Append(vbTab).Append("<SOURCE>").Append(Esc(source)).Append("</SOURCE>").Append(vbLf)
        o.Append(vbTab).Append("<ID>").Append(Esc(race.ID)).Append("</ID>").Append(vbLf)
        o.Append(vbTab).Append("<TEST>").Append(If(test, "YES", "NO")).Append("</TEST>").Append(vbLf)
        o.Append(vbTab).Append("<WINNERS>").Append(race.Winners).Append("</WINNERS>").Append(vbLf)
        o.Append(vbTab).Append("<LEVEL>").Append(Esc(race.Level)).Append("</LEVEL>").Append(vbLf)
        o.Append(vbTab).Append("<STATE>").Append(Esc(race.State)).Append("</STATE>").Append(vbLf)
        o.Append(vbTab).Append("<COUNTY>").Append(Esc(race.County)).Append("</COUNTY>").Append(vbLf)
        o.Append(vbTab).Append("<CITY>").Append(Esc(race.City)).Append("</CITY>").Append(vbLf)
        o.Append(vbTab).Append("<DISTRICT>").Append(Esc(race.District)).Append("</DISTRICT>").Append(vbLf)
        o.Append(vbTab).Append("<OFFICE>").Append(Esc(race.Office)).Append("</OFFICE>").Append(vbLf)
        o.Append(vbTab).Append("<PARTY>").Append(Esc(race.Party)).Append("</PARTY>").Append(vbLf)
        o.Append(vbTab).Append("<TYPE>").Append(Esc(race.RaceType)).Append("</TYPE>").Append(vbLf)
        o.Append(vbTab).Append("<SEATNAME>").Append(Esc(race.SeatName)).Append("</SEATNAME>").Append(vbLf)
        o.Append(vbTab).Append("<SEATNUMBER>").Append(Esc(race.SeatNumber)).Append("</SEATNUMBER>").Append(vbLf)
        o.Append(vbTab).Append("<PRECINCTS_REPORTING>0</PRECINCTS_REPORTING>").Append(vbLf)
        o.Append(vbTab).Append("<PRECINCTS_TOTAL>").Append(race.PrecinctsTotal).Append("</PRECINCTS_TOTAL>").Append(vbLf)
        o.Append(vbTab).Append("<CANDIDATES>").Append(vbLf)

        For Each c In race.Candidates
            o.Append(vbTab).Append(vbTab).Append("<CANDIDATE>").Append(vbLf)
            o.Append(vbTab).Append(vbTab).Append(vbTab).Append("<FULLNAME>").Append(Esc(c.FullName)).Append("</FULLNAME>").Append(vbLf)
            o.Append(vbTab).Append(vbTab).Append(vbTab).Append("<FIRST>").Append(Esc(c.FirstName)).Append("</FIRST>").Append(vbLf)
            o.Append(vbTab).Append(vbTab).Append(vbTab).Append("<MIDDLE>").Append(Esc(c.MiddleName)).Append("</MIDDLE>").Append(vbLf)
            o.Append(vbTab).Append(vbTab).Append(vbTab).Append("<LAST>").Append(Esc(c.LastName)).Append("</LAST>").Append(vbLf)
            o.Append(vbTab).Append(vbTab).Append(vbTab).Append("<SUFFIX>").Append(Esc(c.Suffix)).Append("</SUFFIX>").Append(vbLf)
            o.Append(vbTab).Append(vbTab).Append(vbTab).Append("<PARTY>").Append(Esc(c.Party)).Append("</PARTY>").Append(vbLf)
            If c.Incumbent Then
                o.Append(vbTab).Append(vbTab).Append(vbTab).Append("<INCUMBENT>YES</INCUMBENT>").Append(vbLf)
            End If
            o.Append(vbTab).Append(vbTab).Append(vbTab).Append("<VOTES>0</VOTES>").Append(vbLf)
            o.Append(vbTab).Append(vbTab).Append(vbTab).Append("<DELEGATES></DELEGATES>").Append(vbLf)
            o.Append(vbTab).Append(vbTab).Append("</CANDIDATE>").Append(vbLf)
        Next

        o.Append(vbTab).Append("</CANDIDATES>").Append(vbLf)
        o.Append(vbTab).Append("<SUBRACES>").Append(vbLf)
        o.Append(vbTab).Append("</SUBRACES>").Append(vbLf)
        o.Append("</RACE>")
        Return o.ToString()
    End Function

    ''' <summary>The full line written to the socket (the sample ends it with two CRLFs).</summary>
    Public Function BuildMessage(raceXml As String, [when] As Date) As String
        Return Command & [when].ToString() & "=WIREDATA=" & raceXml & vbCrLf & vbCrLf
    End Function

End Module
