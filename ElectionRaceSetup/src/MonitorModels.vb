Option Strict On
Option Infer On

''' <summary>
''' The Election Results Monitor API, schema version 2: a ballot definition (/api/contests) that holds
''' every name and descriptive field, and ID-only results (/api/results) that point into it.
''' </summary>
Public Module MonitorSchema
    Public Const SupportedSchemaVersion As Integer = 2
End Module

Public Class ApiWarning
    Public Property Code As String = ""
    Public Property Severity As String = ""
    Public Property ContestId As String = ""
    Public Property CandidateId As String = ""
    Public Property Message As String = ""

    Public ReadOnly Property IsError As Boolean
        Get
            Return String.Equals(Severity, "error", StringComparison.OrdinalIgnoreCase)
        End Get
    End Property
End Class

''' <summary>One county's printing of a contest or candidate.</summary>
Public Class ApiSource
    Public Property County As String = ""
    Public Property SourceId As String = ""
    Public Property Name As String = ""
End Class

Public Class ApiCandidate
    Public Property Id As String = ""
    Public Property CandidateType As String = "ballot"
    Public Property CandidateStatus As String = "active"
    Public Property BallotName As String = ""
    Public Property Label As String = ""
    Public Property First As String = ""
    Public Property Middle As String = ""
    Public Property Last As String = ""
    Public Property Suffix As String = ""
    Public Property Nickname As String = ""
    Public Property Party As String = ""
    Public Property PersonId As String = ""
    Public ReadOnly Property Sources As New List(Of ApiSource)

    ''' <summary>A person or ballot choice printed on the ballot (not a write-in tally row).</summary>
    Public ReadOnly Property IsBallotLine As Boolean
        Get
            Return CandidateType = "ballot" OrElse CandidateType = "qualified_write_in"
        End Get
    End Property

    Public ReadOnly Property IsWriteInRow As Boolean
        Get
            Return CandidateType = "write_in_total" OrElse CandidateType = "unqualified_write_in"
        End Get
    End Property
End Class

Public Class ApiContest
    Public Property Id As String = ""
    Public Property ContestType As String = "candidate"
    Public Property Status As String = "contested"
    Public Property Level As String = ""
    Public Property Jurisdiction As String = ""
    Public Property JurisdictionType As String = ""
    Public Property Office As String = ""
    Public Property SeatType As String = ""
    Public Property SeatNumber As String = ""
    Public Property TermYears As Integer?
    Public Property IsShortTerm As Boolean
    Public Property VoteFor As Integer?
    Public Property MeasureLetter As String = ""
    Public Property MeasureTitle As String = ""
    Public Property Threshold As String = ""
    Public Property RetentionName As String = ""
    Public ReadOnly Property JurisdictionCounties As New List(Of String)
    Public ReadOnly Property Sources As New List(Of ApiSource)
    Public ReadOnly Property Candidates As New List(Of ApiCandidate)
    Public ReadOnly Property Warnings As New List(Of ApiWarning)

    Public Function CandidateById(id As String) As ApiCandidate
        For Each c In Candidates
            If String.Equals(c.Id, id, StringComparison.Ordinal) Then Return c
        Next
        Return Nothing
    End Function

    Public Function SourceName(county As String) As String
        For Each s In Sources
            If String.Equals(s.County, county, StringComparison.OrdinalIgnoreCase) Then Return s.Name
        Next
        Return ""
    End Function

    ''' <summary>Winners to send; the guide says a null vote_for means assume 1.</summary>
    Public ReadOnly Property Winners As Integer
        Get
            Return If(VoteFor.HasValue AndAlso VoteFor.Value >= 1, VoteFor.Value, 1)
        End Get
    End Property
End Class

Public Class ApiDefinition
    Public Property Version As Integer
    Public Property UpdatedAt As String = ""
    Public Property ElectionDate As String = ""
    Public ReadOnly Property OverrideErrors As New List(Of String)
    Public ReadOnly Property Contests As New List(Of ApiContest)

    Private _byId As Dictionary(Of String, ApiContest)

    Public Function ContestById(id As String) As ApiContest
        If _byId Is Nothing Then
            _byId = New Dictionary(Of String, ApiContest)(StringComparer.Ordinal)
            For Each c In Contests
                _byId(c.Id) = c
            Next
        End If
        Dim hit As ApiContest = Nothing
        _byId.TryGetValue(If(id, ""), hit)
        Return hit
    End Function
End Class

Public Class ApiResultCandidate
    Public Property CandidateId As String = ""
    Public Property Votes As Long?
End Class

''' <summary>One county's portion of one contest.</summary>
Public Class ApiContestResult
    Public Property ContestId As String = ""
    Public Property SourceId As String = ""
    Public Property PrecinctsReporting As Integer?
    Public Property PrecinctsTotal As Integer?
    Public Property TotalVotes As Long?
    Public Property WorstSeverity As String = ""
    Public ReadOnly Property WarningCodes As New List(Of String)
    Public ReadOnly Property Candidates As New List(Of ApiResultCandidate)
End Class

Public Class ApiCounty
    Public Property County As String = ""
    Public Property Status As String = ""
    Public Property StatusLabel As String = ""
    Public Property ElectionLabel As String = ""
    Public Property FetchedAt As String = ""
    Public Property LastAttemptAt As String = ""
    Public Property ContentChangedAt As String = ""
    Public Property ContentHash As String = ""
    Public Property CountyTimestamp As String = ""
    Public Property SourceUrl As String = ""
    Public Property ErrorText As String = ""
    Public Property Notice As String = ""
    Public ReadOnly Property Warnings As New List(Of ApiWarning)
    Public ReadOnly Property Results As New List(Of ApiContestResult)

    ''' <summary>Counties whose numbers are real: votes above zero and past the monitor's checks.</summary>
    Public ReadOnly Property HasLiveNumbers As Boolean
        Get
            Return Status = "partial" OrElse Status = "final" OrElse Status = "certified"
        End Get
    End Property

    ''' <summary>Warnings that apply to the whole county (contest_id null), at error severity.</summary>
    Public ReadOnly Property CountyErrors As List(Of ApiWarning)
        Get
            Return Warnings.Where(Function(w) w.IsError AndAlso w.ContestId = "").ToList()
        End Get
    End Property
End Class

Public Class ApiResults
    Public Property SchemaVersion As Integer
    Public Property GeneratedAt As String = ""
    Public Property ElectionDate As String = ""
    Public Property BallotVersion As Integer
    Public Property ContentHash As String = ""
    Public ReadOnly Property Counties As New List(Of ApiCounty)
End Class
