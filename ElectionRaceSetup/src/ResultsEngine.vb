Option Strict On
Option Infer On

Public Enum ResultStatus
    ''' <summary>No setup race could be paired with this feed contest. Never sent.</summary>
    Unmatched
    ''' <summary>Paired, but something looks wrong. Not sent automatically; can be sent by hand.</summary>
    Held
    ''' <summary>Paired, checked and different from what was last sent.</summary>
    Ready
    ''' <summary>Same as what was last sent.</summary>
    Unchanged
    ''' <summary>Written to the socket in this session.</summary>
    Sent
End Enum

Public Class ResultItem
    Public Property Contest As FeedContest
    Public Property Race As Race
    Public Property Outgoing As Race
    Public Property Status As ResultStatus = ResultStatus.Unmatched
    Public Property How As String = ""
    Public Property Xml As String = ""
    Public Property TotalVotes As Long
    Public Property MatchedCandidates As Integer
    Public Property PendingKey As String = ""
    Public ReadOnly Property Notes As New List(Of String)
    Friend Property Snapshot As Long()
End Class

''' <summary>
''' Pairs a parsed feed with the setup races, builds the results to send, and decides what is safe
''' to send automatically. Results always use the setup race's ID and the setup candidates' own names,
''' because that is what Election Manager linked the race and candidates to.
''' </summary>
Public Class ResultsEngine

    Public ReadOnly Property Crosswalk As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)

    Private ReadOnly _lastXml As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
    Private ReadOnly _lastVotes As New Dictionary(Of String, Long())(StringComparer.OrdinalIgnoreCase)

    ''' <summary>Forget what has been sent, so the next evaluation treats every race as new.</summary>
    Public Sub Reset()
        _lastXml.Clear()
        _lastVotes.Clear()
    End Sub

    Public Sub MarkSent(item As ResultItem)
        If item.Race Is Nothing Then Return
        _lastXml(item.Race.ID) = item.Xml
        If item.Snapshot IsNot Nothing Then _lastVotes(item.Race.ID) = item.Snapshot
        item.Status = ResultStatus.Sent
    End Sub

    Public Function Evaluate(contests As IEnumerable(Of FeedContest), setupRaces As IList(Of Race),
                             electionDate As String, source As String, test As Boolean) As List(Of ResultItem)
        Dim items As New List(Of ResultItem)
        Dim claimed As New Dictionary(Of String, ResultItem)(StringComparer.OrdinalIgnoreCase)

        For Each contest In contests
            Dim item As New ResultItem With {.Contest = contest}
            items.Add(item)
            For Each w In contest.Warnings
                item.Notes.Add(w)
            Next

            Dim m = RaceMatcher.FindRace(contest, setupRaces, Crosswalk)
            If m.Race Is Nothing Then
                item.Status = ResultStatus.Unmatched
                item.Notes.Add(If(m.Suggestion <> "", "No safe match; " & m.Suggestion & ". Pick the race by hand.", "No matching setup race. Pick one by hand."))
                Continue For
            End If

            item.Race = m.Race
            item.How = m.How
            Dim held = BuildOutgoing(item, contest)

            Dim other As ResultItem = Nothing
            If claimed.TryGetValue(m.Race.ID, other) Then
                held.Add($"Race {m.Race.ID} is also paired with another feed contest ({other.Contest.Office}).")
            Else
                claimed(m.Race.ID) = item
            End If

            item.Xml = WireBuilder.BuildRaceXml(item.Outgoing, electionDate, source, test)

            If held.Count > 0 Then
                item.Status = ResultStatus.Held
                item.Notes.AddRange(held)
            Else
                Dim prev As String = Nothing
                item.Status = If(_lastXml.TryGetValue(m.Race.ID, prev) AndAlso prev = item.Xml, ResultStatus.Unchanged, ResultStatus.Ready)
            End If
        Next
        Return items
    End Function

    ''' <summary>Fills item.Outgoing from the setup race plus feed numbers. Returns the reasons to hold it back.</summary>
    Private Function BuildOutgoing(item As ResultItem, contest As FeedContest) As List(Of String)
        Dim held As New List(Of String)
        Dim race = item.Race

        Dim o As New Race With {
            .ID = race.ID, .FeedId = race.FeedId, .Level = race.Level, .State = race.State, .County = race.County,
            .City = race.City, .District = race.District, .Office = race.Office, .Party = race.Party,
            .RaceType = race.RaceType, .SeatName = race.SeatName, .SeatNumber = race.SeatNumber,
            .Winners = race.Winners, .PrecinctsTotal = If(contest.PrecinctsTotal, race.PrecinctsTotal)
        }
        For Each c In race.Candidates
            o.Candidates.Add(New Candidate With {
                .FullName = c.FullName, .FirstName = c.FirstName, .MiddleName = c.MiddleName, .LastName = c.LastName,
                .Suffix = c.Suffix, .Party = c.Party, .Incumbent = c.Incumbent, .Votes = 0, .Winner = False})
        Next
        item.Outgoing = o

        o.PrecinctsReporting = If(contest.PrecinctsReporting, 0)
        If o.PrecinctsTotal > 0 AndAlso o.PrecinctsReporting > o.PrecinctsTotal Then
            held.Add($"Precincts reporting ({o.PrecinctsReporting}) is more than the total ({o.PrecinctsTotal}).")
        End If

        Dim map = RaceMatcher.MatchCandidates(contest.Candidates, race.Candidates)
        Dim snapshot(race.Candidates.Count - 1) As Long
        For i = 0 To contest.Candidates.Count - 1
            Dim fc = contest.Candidates(i)
            If map(i) < 0 Then
                If RaceMatcher.IsWriteIn(fc.FullName) Then
                    item.Notes.Add($"Ignored feed line ""{fc.FullName}"" (not in the setup race).")
                Else
                    held.Add($"Feed candidate ""{fc.FullName}"" matches no candidate in setup race {race.ID}.")
                End If
                Continue For
            End If
            If Not fc.Votes.HasValue Then
                held.Add($"Votes for {fc.FullName} could not be read as a whole number.")
                Continue For
            End If
            Dim target = o.Candidates(map(i))
            target.Votes = fc.Votes.Value
            target.Winner = fc.Winner
            snapshot(map(i)) = fc.Votes.Value
            item.TotalVotes += fc.Votes.Value
            item.MatchedCandidates += 1
        Next

        For j = 0 To race.Candidates.Count - 1
            If Not map.Contains(j) Then item.Notes.Add($"Setup candidate ""{race.Candidates(j).DisplayName}"" is not in the feed; sent with 0 votes.")
        Next

        Dim last As Long() = Nothing
        If _lastVotes.TryGetValue(race.ID, last) AndAlso last.Length = snapshot.Length Then
            For j = 0 To snapshot.Length - 1
                If snapshot(j) < last(j) Then
                    held.Add($"Votes for {race.Candidates(j).DisplayName} went down ({last(j):N0} to {snapshot(j):N0}).")
                End If
            Next
        End If
        item.Snapshot = snapshot
        Return held
    End Function
End Class
