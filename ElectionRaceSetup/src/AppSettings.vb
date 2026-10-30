Option Strict On
Option Infer On

Imports System.Globalization
Imports System.IO

''' <summary>Remembers the last-used values in %APPDATA%\ElectionRaceSetup\settings.ini.</summary>
Public Class AppSettings
    Public Property Host As String = "HATV-MOS"
    Public Property Port As Integer = 10387
    Public Property Source As String = "CSV"
    Public Property IsTest As Boolean = True
    Public Property DelayMs As Integer = 500
    Public Property LastCsv As String = ""
    Public Property ElectionDate As String = ""
    Public Property FeedSource As String = ""
    Public Property FeedHeaders As String = ""
    Public Property FeedProfile As String = ""
    Public Property FeedIntervalSec As Integer = 60
    Public Property FeedAuto As Boolean
    Public Property FeedSkipAreas As String = ""
    Public Property FeedSubRaces As Boolean

    Private Shared ReadOnly Property FilePath As String
        Get
            Return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ElectionRaceSetup", "settings.ini")
        End Get
    End Property

    Private Shared Function ParseInt(value As String, fallback As Integer) As Integer
        Dim n As Integer
        Return If(Integer.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, n), n, fallback)
    End Function

    Public Shared Function Load() As AppSettings
        Dim s As New AppSettings
        Try
            If Not File.Exists(FilePath) Then Return s
            For Each line In File.ReadAllLines(FilePath)
                Dim eq = line.IndexOf("="c)
                If eq <= 0 Then Continue For
                Dim key = line.Substring(0, eq).Trim()
                Dim value = line.Substring(eq + 1).Trim()
                Select Case key
                    Case "Host" : If value <> "" Then s.Host = value
                    Case "Port" : s.Port = ParseInt(value, s.Port)
                    Case "Source" : If value <> "" Then s.Source = value
                    Case "IsTest" : s.IsTest = Not String.Equals(value, "false", StringComparison.OrdinalIgnoreCase)
                    Case "DelayMs" : s.DelayMs = ParseInt(value, s.DelayMs)
                    Case "LastCsv" : s.LastCsv = value
                    Case "ElectionDate" : s.ElectionDate = value
                    Case "FeedSource" : s.FeedSource = value
                    Case "FeedHeaders" : s.FeedHeaders = value
                    Case "FeedProfile" : s.FeedProfile = value
                    Case "FeedIntervalSec" : s.FeedIntervalSec = ParseInt(value, s.FeedIntervalSec)
                    Case "FeedAuto" : s.FeedAuto = String.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                    Case "FeedSkipAreas" : s.FeedSkipAreas = value
                    Case "FeedSubRaces" : s.FeedSubRaces = String.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                End Select
            Next
        Catch
        End Try
        Return s
    End Function

    Public Sub Save()
        Try
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath))
            File.WriteAllLines(FilePath, {
                "Host=" & Host,
                "Port=" & Port.ToString(CultureInfo.InvariantCulture),
                "Source=" & Source,
                "IsTest=" & IsTest.ToString(),
                "DelayMs=" & DelayMs.ToString(CultureInfo.InvariantCulture),
                "LastCsv=" & LastCsv,
                "ElectionDate=" & ElectionDate,
                "FeedSource=" & FeedSource,
                "FeedHeaders=" & FeedHeaders,
                "FeedProfile=" & FeedProfile,
                "FeedIntervalSec=" & FeedIntervalSec.ToString(CultureInfo.InvariantCulture),
                "FeedAuto=" & FeedAuto.ToString(),
                "FeedSkipAreas=" & FeedSkipAreas,
                "FeedSubRaces=" & FeedSubRaces.ToString()
            })
        Catch
        End Try
    End Sub
End Class
