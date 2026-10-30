Option Strict On
Option Infer On

Imports System.Text

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
    ''' <summary>The first (or only) feed contest behind this row.</summary>
    Public Property Contest As FeedContest

    ''' <summary>Every feed contest paired with this race (one per county when a race spans counties).</summary>
    Public ReadOnly Property Contests As New List(Of FeedContest)

    Public Property Race As Race
    Public Property Outgoing As Race
    Public Property Status As ResultStatus = ResultStatus.Unmatched
    Public Property How As String = ""
    Public Property Xml As String = ""
    Public Property TotalVotes As Long
    Public Property MatchedCandidates As Integer
    Public Property PendingKey As String = ""
    Public Property Areas As String = ""
    Public ReadOnly Property Notes As New List(Of String)
    Friend Property Snapshot As Long()
End Class

''' <summary>
''' Pairs a parsed feed with the setup races, builds the results to send, and decides what is safe
''' to send automatically. Results always use the setup race's ID and the setup candidates' own names,
''' because that is what Election Manager linked the race and candidates to. When a feed reports the
''' same race under several counties, the counties are added together (and can be sent as sub-races).
''' </summary>
Public Class ResultsEngine

    Public ReadOnly Property Crosswalk As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)

    ''' <summary>How many feed contests the last evaluation left out because of the skip list.</summary>
    Public Property SkippedCount As Integer

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

    ''' <param name="skipAreas">Counties (or other areas) to leave out, e.g. "San Benito".</param>
    ''' <param name="includeSubRaces">Also send each county's own numbers when a race spans two or more counties.</param>
    Public Function Evaluate(contests As IEnumerable(Of FeedContest), setupRaces As IList(Of Race),
                             electionDate As String, source As String, test As Boolean,
                             Optional skipAreas As IEnumerable(Of String) = Nothing,
                             Optional includeSubRaces As Boolean = False) As List(Of ResultItem)
        Dim skip As New HashSet(Of String)
        If skipAreas IsNot Nothing Then
            For Each a In skipAreas
                Dim n = FeedNode.Norm(If(a, "").Replace("County", "").Replace("county", ""))
                If n <> "" Then skip.Add(n)
            Next
        End If
        SkippedCount = 0
        RaceMatcher.Prepare(setupRaces)

        Dim items As New List(Of ResultItem)
        Dim byRace As New Dictionary(Of String, ResultItem)(StringComparer.OrdinalIgnoreCase)

        For Each contest In contests
            If contest.Area <> "" AndAlso skip.Contains(FeedNode.Norm(contest.Area.Replace("County", "").Replace("county", ""))) Then
                SkippedCount += 1
                Continue For
            End If

            Dim m = RaceMatcher.FindRace(contest, setupRaces, Crosswalk)
            If m.Race Is Nothing Then
                Dim lone As New ResultItem With {.Contest = contest, .Status = ResultStatus.Unmatched, .Areas = contest.Area}
                lone.Contests.Add(contest)
                For Each w In contest.Warnings
                    lone.Notes.Add(w)
                Next
                lone.Notes.Add(If(m.Suggestion <> "", "No safe match; " & m.Suggestion & ". Pick the race by hand.", "No matching setup race. Pick one by hand."))
                items.Add(lone)
                Continue For
            End If

            Dim item As ResultItem = Nothing
            If Not byRace.TryGetValue(m.Race.ID, item) Then
                item = New ResultItem With {.Contest = contest, .Race = m.Race, .How = m.How}
                byRace(m.Race.ID) = item
                items.Add(item)
            End If
            item.Contests.Add(contest)
        Next

        For Each item In items
            If item.Race Is Nothing Then Continue For
            Dim held = BuildOutgoing(item, includeSubRaces)
            item.Xml = WireBuilder.BuildRaceXml(item.Outgoing, electionDate, source, test)
            If held.Count > 0 Then
                item.Status = ResultStatus.Held
                item.Notes.AddRange(held)
            Else
                Dim prev As String = Nothing
                item.Status = If(_lastXml.TryGetValue(item.Race.ID, prev) AndAlso prev = item.Xml, ResultStatus.Unchanged, ResultStatus.Ready)
            End If
        Next
        Return items
    End Function

    Private Shared Function Where_(c As FeedContest) As String
        Return If(c.Area <> "", c.Area, c.Office)
    End Function

    ''' <summary>Fills item.Outgoing from the setup race plus the feed numbers. Returns the reasons to hold it back.</summary>
    Private Function BuildOutgoing(item As ResultItem, includeSubRaces As Boolean) As List(Of String)
        Dim held As New List(Of String)
        Dim race = item.Race

        Dim o As New Race With {
            .ID = race.ID, .FeedId = race.FeedId, .Level = race.Level, .State = race.State, .County = race.County,
            .City = race.City, .District = race.District, .Office = race.Office, .Party = race.Party,
            .RaceType = race.RaceType, .SeatName = race.SeatName, .SeatNumber = race.SeatNumber,
            .Winners = race.Winners, .PrecinctsTotal = race.PrecinctsTotal
        }
        For Each c In race.Candidates
            o.Candidates.Add(New Candidate With {
                .FullName = c.FullName, .FirstName = c.FirstName, .MiddleName = c.MiddleName, .LastName = c.LastName,
                .Suffix = c.Suffix, .Party = c.Party, .Incumbent = c.Incumbent, .Votes = 0, .Winner = False})
        Next
        item.Outgoing = o
        item.Areas = String.Join(" + ", item.Contests.Select(Function(c) c.Area).Where(Function(a) a <> "").Distinct())

        ' the same area twice means two feed contests really are competing for this race
        Dim seenAreas As New HashSet(Of String)
        For Each c In item.Contests
            If Not seenAreas.Add(FeedNode.Norm(c.Area)) Then
                held.Add($"Race {race.ID} is also paired with another feed contest from the same area ({Where_(c)}).")
            End If
        Next

        ' precincts: add the areas together
        Dim reporting = 0, total = 0
        Dim haveTotal = False
        Dim noPrecincts As New List(Of String)
        For Each c In item.Contests
            If c.PrecinctsReporting.HasValue Then reporting += c.PrecinctsReporting.Value Else noPrecincts.Add(Where_(c))
            If c.PrecinctsTotal.HasValue Then
                total += c.PrecinctsTotal.Value
                haveTotal = True
            End If
        Next
        o.PrecinctsReporting = reporting
        If haveTotal Then o.PrecinctsTotal = total
        If noPrecincts.Count > 0 AndAlso item.Contests.Count > 1 AndAlso noPrecincts.Count < item.Contests.Count Then
            item.Notes.Add("Precinct counts not given for: " & String.Join(", ", noPrecincts) & ".")
        End If
        If o.PrecinctsTotal > 0 AndAlso o.PrecinctsReporting > o.PrecinctsTotal Then
            held.Add($"Precincts reporting ({o.PrecinctsReporting}) is more than the total ({o.PrecinctsTotal}).")
        End If

        ' votes: add the areas together, candidate by candidate
        Dim snapshot(race.Candidates.Count - 1) As Long
        Dim seenInFeed As New HashSet(Of Integer)
        Dim ignored As New List(Of String)
        Dim subRaces As New List(Of SubRaceInfo)

        For Each c In item.Contests
            For Each w In c.Warnings
                item.Notes.Add(w)
            Next
            For Each b In c.Blockers
                held.Add(b)
            Next
            If c.Winners.HasValue AndAlso c.Winners.Value <> race.Winners Then
                item.Notes.Add($"Feed says vote for {c.Winners.Value}; the setup race has {race.Winners} winner(s).")
            End If

            Dim map = RaceMatcher.MatchCandidates(c.Candidates, race.Candidates)
            Dim areaVotes(race.Candidates.Count - 1) As Long
            For i = 0 To c.Candidates.Count - 1
                Dim fc = c.Candidates(i)
                If map(i) < 0 Then
                    If fc.WriteIn OrElse RaceMatcher.IsWriteIn(fc.FullName) Then
                        If Not ignored.Contains(fc.FullName) Then ignored.Add(fc.FullName)
                    Else
                        held.Add($"Feed candidate ""{fc.FullName}""{If(c.Area <> "", " (" & c.Area & ")", "")} matches no candidate in setup race {race.ID}.")
                    End If
                    Continue For
                End If
                If Not fc.Votes.HasValue Then
                    Dim whose = If(c.Area <> "", " (" & c.Area & ")", "")
                    If fc.WriteIn Then
                        held.Add($"Write-in votes{whose} do not add up: the named write-ins come to more than the contest total.")
                    Else
                        held.Add($"Votes for {fc.FullName}{whose} could not be read as a whole number.")
                    End If
                    Continue For
                End If
                seenInFeed.Add(map(i))
                areaVotes(map(i)) += fc.Votes.Value
                snapshot(map(i)) += fc.Votes.Value
                item.TotalVotes += fc.Votes.Value
                item.MatchedCandidates += 1
                If item.Contests.Count = 1 Then o.Candidates(map(i)).Winner = fc.Winner
            Next

            If includeSubRaces AndAlso item.Contests.Count > 1 AndAlso c.Area <> "" Then
                subRaces.Add(New SubRaceInfo With {
                    .SubType = "COUNTY",
                    .Location = c.Area,
                    .Votes = String.Join("\", areaVotes),
                    .Precincts = If(c.PrecinctsReporting.HasValue, c.PrecinctsReporting.Value & "\" & If(c.PrecinctsTotal.HasValue, c.PrecinctsTotal.Value.ToString(), "0"), "")
                })
            End If
        Next
        For j = 0 To race.Candidates.Count - 1
            o.Candidates(j).Votes = snapshot(j)
        Next
        o.SubRaces.AddRange(subRaces)

        If ignored.Count > 0 Then
            item.Notes.Add("Ignored write-in line(s) not in the setup race: " & String.Join(", ", ignored.Take(3)) & If(ignored.Count > 3, $" and {ignored.Count - 3} more", "") & ".")
        End If
        For j = 0 To race.Candidates.Count - 1
            If Not seenInFeed.Contains(j) Then item.Notes.Add($"Setup candidate ""{race.Candidates(j).DisplayName}"" is not in the feed; sent with 0 votes.")
        Next
        If item.Contests.Count > 1 Then item.Notes.Insert(0, "Added together: " & String.Join(" + ", item.Contests.Select(AddressOf Where_)) & ".")

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

''' <summary>A CSV for finding out why a feed contest was or was not paired.</summary>
Public Module PairingReport

    Private Function Q(s As String) As String
        Return """" & If(s, "").Replace("""", """""") & """"
    End Function

    Public Function Build(items As IEnumerable(Of ResultItem), races As IList(Of Race)) As String
        Dim sb As New StringBuilder
        sb.AppendLine("Status,Areas,Feed contest,Feed ID,Feed candidates,Paired race ID,Paired race office,How paired,Notes,Closest 1,Closest 2,Closest 3")
        Dim paired As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)

        For Each it In items
            For Each c In it.Contests
                Dim names = String.Join("; ", c.Candidates.Where(Function(x) Not x.WriteIn).Take(5).Select(Function(x) x.FullName))
                Dim suggestions As New List(Of String)
                If it.Race Is Nothing OrElse it.Status = ResultStatus.Held Then
                    For Each kv In RaceMatcher.Rank(c, races, 3)
                        suggestions.Add($"{kv.Key.ID} | {kv.Key.Office} | {kv.Value:0.00}")
                    Next
                End If
                While suggestions.Count < 3
                    suggestions.Add("")
                End While
                sb.AppendLine(String.Join(",", {
                    Q(it.Status.ToString()), Q(c.Area), Q(c.Office), Q(c.FeedId), Q(names),
                    Q(If(it.Race IsNot Nothing, it.Race.ID, "")), Q(If(it.Race IsNot Nothing, it.Race.Office, "")),
                    Q(it.How), Q(String.Join("  ", it.Notes)), Q(suggestions(0)), Q(suggestions(1)), Q(suggestions(2))}))
            Next
            If it.Race IsNot Nothing Then paired.Add(it.Race.ID)
        Next
        For Each r In races
            If Not paired.Contains(r.ID) Then
                sb.AppendLine(String.Join(",", {Q("No feed data"), Q(""), Q(""), Q(""), Q(String.Join("; ", r.Candidates.Take(5).Select(Function(x) x.DisplayName))),
                                               Q(r.ID), Q(r.Office), Q(""), Q("This setup race got no results from the feed."), Q(""), Q(""), Q("")}))
            End If
        Next
        Return sb.ToString()
    End Function
End Module
