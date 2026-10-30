Option Strict On
Option Infer On

Imports System.IO

''' <summary>Remembers hand-made pairings (feed contest key to setup race ID) in %APPDATA%\ElectionRaceSetup\crosswalk.ini.</summary>
Public Module CrosswalkStore

    Public Function DefaultPath() As String
        Return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ElectionRaceSetup", "crosswalk.ini")
    End Function

    Public Function Load(filePath As String) As Dictionary(Of String, String)
        Dim map As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
        Try
            If Not File.Exists(filePath) Then Return map
            For Each line In File.ReadAllLines(filePath)
                Dim tab = line.IndexOf(ControlChars.Tab)
                If tab > 0 Then map(line.Substring(0, tab)) = line.Substring(tab + 1).Trim()
            Next
        Catch
        End Try
        Return map
    End Function

    Public Sub Save(filePath As String, map As Dictionary(Of String, String))
        Try
            Directory.CreateDirectory(Path.GetDirectoryName(filePath))
            File.WriteAllLines(filePath, map.Select(Function(kv) kv.Key.Replace(ControlChars.Tab, " "c) & ControlChars.Tab & kv.Value))
        Catch
        End Try
    End Sub
End Module
