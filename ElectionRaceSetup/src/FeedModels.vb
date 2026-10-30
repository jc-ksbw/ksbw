Option Strict On
Option Infer On

''' <summary>One candidate's line in a results feed.</summary>
Public Class FeedCandidate
    Public Property FullName As String = ""
    Public Property Party As String = ""

    ''' <summary>Nothing when the feed gave something that is not a whole number.</summary>
    Public Property Votes As Long? = 0
    Public Property Winner As Boolean
    Public Property Incumbent As Boolean
End Class

''' <summary>One contest (race) as the feed describes it, before it is paired with a setup race.</summary>
Public Class FeedContest
    Public Property FeedId As String = ""
    Public Property Office As String = ""
    Public Property Level As String = ""
    Public Property State As String = ""
    Public Property County As String = ""
    Public Property City As String = ""
    Public Property District As String = ""
    Public Property Party As String = ""
    Public Property RaceType As String = ""
    Public Property SeatName As String = ""
    Public Property SeatNumber As String = ""
    Public Property PrecinctsReporting As Integer?
    Public Property PrecinctsTotal As Integer?
    Public ReadOnly Property Candidates As New List(Of FeedCandidate)
    Public ReadOnly Property Warnings As New List(Of String)

    ''' <summary>Stable identity for remembering a manual pairing: the feed's ID, else its description.</summary>
    Public ReadOnly Property Key As String
        Get
            If FeedId <> "" Then Return FeedId
            Return "name:" & String.Join("|", {Office, SeatName, SeatNumber, Party, State, County, City, District}.Select(Function(s) s.Trim().ToLowerInvariant()))
        End Get
    End Property
End Class
