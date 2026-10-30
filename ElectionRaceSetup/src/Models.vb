Option Strict On
Option Infer On

''' <summary>One candidate (or Yes/No choice on a ballot measure) in a race.</summary>
Public Class Candidate
    Public Property FullName As String = ""
    Public Property FirstName As String = ""
    Public Property MiddleName As String = ""
    Public Property LastName As String = ""
    Public Property Suffix As String = ""
    Public Property Party As String = ""
    Public Property Incumbent As Boolean

    ''' <summary>The candidate's ID in the results feed, when it has one. Makes pairing exact.</summary>
    Public Property FeedId As String = ""

    ''' <summary>Votes and winner flag are only filled in when sending results.</summary>
    Public Property Votes As Long
    Public Property Winner As Boolean

    ''' <summary>Name for display in the UI only.</summary>
    Public ReadOnly Property DisplayName As String
        Get
            If FullName <> "" Then Return FullName
            Return String.Join(" ", New String() {FirstName, MiddleName, LastName, Suffix}.Where(Function(s) s <> ""))
        End Get
    End Property
End Class

''' <summary>A county (or similar) breakdown of a race. Votes and Precincts use backslashes, as in the sample: "3\4", "2\20".</summary>
Public Class SubRaceInfo
    Public Property SubType As String = "COUNTY"
    Public Property Location As String = ""
    Public Property Votes As String = ""
    Public Property Precincts As String = ""
    Public Property Winner As String = ""
End Class

''' <summary>One race, as it will be sent to Election Manager.</summary>
Public Class Race
    Public Property ID As String = ""
    Public Property Level As String = ""
    Public Property State As String = ""
    Public Property County As String = ""
    Public Property City As String = ""
    Public Property District As String = ""
    Public Property Office As String = ""
    Public Property Party As String = ""
    Public Property RaceType As String = ""
    Public Property SeatName As String = ""
    Public Property SeatNumber As String = ""
    Public Property Winners As Integer = 1
    Public Property PrecinctsTotal As Integer
    Public Property PrecinctsReporting As Integer

    ''' <summary>The ID this race has in a results feed, when that differs from ID.</summary>
    Public Property FeedId As String = ""

    Public ReadOnly Property Candidates As New List(Of Candidate)
    Public ReadOnly Property SubRaces As New List(Of SubRaceInfo)

    ''' <summary>Problems that stop the race from being sent.</summary>
    Public ReadOnly Property Errors As New List(Of String)

    ''' <summary>Things worth a look that do not stop the race from being sent.</summary>
    Public Property Warnings As New List(Of String)
End Class
