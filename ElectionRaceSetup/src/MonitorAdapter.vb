Option Strict On
Option Infer On

Imports System.Globalization
Imports System.Text.RegularExpressions

''' <summary>How much of the API's write-in detail is sent on to Election Manager.</summary>
''' <remarks>The order matches the dropdown the user picks from, so the two cannot drift apart.</remarks>
Public Enum WriteInMode
    ''' <summary>Named certified write-ins as candidates, and the rest as one "Write-in" line.</summary>
    NamedPlusTotal = 0
    ''' <summary>One "Write-in" line holding every write-in vote.</summary>
    TotalOnly = 1
    ''' <summary>Ballot candidates only. Votes will not add up to the county's printed total.</summary>
    Exclude = 2
End Enum

Public Class MonitorOptions
    Public Property WriteIns As WriteInMode = WriteInMode.NamedPlusTotal

    ''' <summary>Counties to leave out entirely.</summary>
    Public ReadOnly Property SkipCounties As New List(Of String)

    ''' <summary>Hold a race when the API flags this county's copy of it as unreliable.</summary>
    Public Property HoldOnContestError As Boolean = True

    ''' <summary>
    ''' Hold a county's numbers when the API raises a county-wide error, which is how it reports
    ''' votes retrieved before polls closed and a report for the wrong election.
    ''' </summary>
    Public Property BlockOnCountyError As Boolean = True

    ''' <summary>The state code to send; the monitor covers California only.</summary>
    Public Property StateCode As String = "CA"
End Class

''' <summary>
''' Turns the monitor API into the app's own race and result objects.
'''
''' Only the fields Election Manager's wire format can read are carried over: id, winners, level,
''' state, county, city, district, office, type, seat name and number, precincts, and each
''' candidate's name, party and votes. Everything else the API publishes - percentages, under and
''' over votes, turnout, content hashes, thresholds, measure titles, source URLs, county status
''' labels - is used inside this app for pairing and safety checks, or shown on screen, and is
''' never sent to Election Manager.
''' </summary>
Public Module MonitorAdapter

    Public Const WriteInId As String = "write_in_total"

    ' ---------------------------------------------------------------------------------------------
    ' Descriptive fields -> the wire format's race fields

    ''' <summary>NATIONAL, STATE, COUNTY, CITY or DISTRICT - the only levels the wire format takes.</summary>
    Public Function LevelOf(c As ApiContest) As String
        Select Case c.JurisdictionType
            Case "city" : Return "CITY"
            Case "county", "county_office_of_education" : Return "COUNTY"
            Case "school_district", "community_college_district", "special_district" : Return "DISTRICT"
        End Select
        If c.SeatType = "district" AndAlso c.SeatNumber <> "" AndAlso (c.Level = "federal" OrElse c.Level = "state") Then Return "DISTRICT"
        Select Case c.Level
            Case "federal" : Return "NATIONAL"
            Case "state" : Return "STATE"
            Case "county" : Return "COUNTY"
            Case "city" : Return "CITY"
            Case "district" : Return "DISTRICT"
        End Select
        Return "STATE"
    End Function

    Private Function Strip(name As String, ParamArray prefixes As String()) As String
        Dim s = If(name, "").Trim()
        For Each p In prefixes
            If s.StartsWith(p, StringComparison.OrdinalIgnoreCase) Then Return s.Substring(p.Length).Trim()
        Next
        Return s
    End Function

    ''' <summary>The office line that goes on air: the office, or the measure's letter or number.</summary>
    Public Function OfficeOf(c As ApiContest) As String
        If c.ContestType = "measure" Then
            If c.MeasureLetter = "" Then Return If(c.MeasureTitle <> "", c.MeasureTitle, "Measure")
            Dim isProposition = c.Level = "state" AndAlso Regex.IsMatch(c.MeasureLetter, "^\d+$")
            Return If(isProposition, "Proposition ", "Measure ") & c.MeasureLetter
        End If
        If c.Office <> "" Then Return c.Office
        If c.RetentionName <> "" Then Return c.RetentionName
        Return c.Id
    End Function

    ''' <summary>The wire format's optional TYPE. Blank for an ordinary candidate race.</summary>
    Public Function TypeOf_(c As ApiContest) As String
        Select Case c.ContestType
            Case "measure"
                Return If(c.Level = "state" AndAlso Regex.IsMatch(c.MeasureLetter, "^\d+$"), "PROPOSITION", "MEASURE")
            Case "retention"
                Return "RETENTION"
            Case Else
                Return ""
        End Select
    End Function

    Private ReadOnly SeatLabels As New Dictionary(Of String, String) From {
        {"trustee_area", "Trustee Area"}, {"district", "District"}, {"zone", "Zone"}, {"division", "Division"}}

    ''' <summary>Builds one setup race from a contest definition. Votes stay at zero.</summary>
    Public Function ToRace(c As ApiContest, options As MonitorOptions) As Race
        Dim level = LevelOf(c)
        Dim r As New Race With {
            .ID = c.Id, .FeedId = c.Id, .Level = level, .State = options.StateCode,
            .Office = OfficeOf(c), .RaceType = TypeOf_(c), .Winners = c.Winners}

        ' Where the race is. A legislative district carries its number; everything else carries a name.
        Dim legislative = c.SeatType = "district" AndAlso c.SeatNumber <> "" AndAlso (c.Level = "federal" OrElse c.Level = "state")
        Select Case level
            Case "CITY" : r.City = Strip(c.Jurisdiction, "City of ", "Town of ")
            Case "COUNTY" : r.County = Strip(c.Jurisdiction, "County of ").Replace(" County Office of Education", "")
            Case "DISTRICT" : r.District = If(legislative, c.SeatNumber, c.Jurisdiction)
        End Select
        ' A local race also names its county, so Measure E in one county is not Measure E in another.
        If level <> "STATE" AndAlso level <> "NATIONAL" AndAlso r.County = "" AndAlso c.JurisdictionCounties.Count = 1 Then
            r.County = c.JurisdictionCounties(0)
        End If

        ' Seat, for races that have one.
        If c.ContestType = "retention" Then
            r.SeatName = c.RetentionName
        ElseIf Not legislative AndAlso c.SeatNumber <> "" Then
            Dim label As String = Nothing
            r.SeatName = If(SeatLabels.TryGetValue(c.SeatType, label), label, "")
            r.SeatNumber = c.SeatNumber
        End If
        If c.IsShortTerm AndAlso c.TermYears.HasValue AndAlso r.SeatName = "" Then r.SeatName = c.TermYears.Value & " Year"

        For Each k In Candidates(c, options.WriteIns)
            r.Candidates.Add(k)
        Next
        Return r
    End Function

    ''' <summary>The candidate lines to set up and send, in ballot order.</summary>
    Private Function Candidates(c As ApiContest, mode As WriteInMode) As List(Of Candidate)
        Dim list As New List(Of Candidate)
        For Each k In c.Candidates
            Dim named = k.CandidateType = "ballot" OrElse (k.CandidateType = "qualified_write_in" AndAlso mode = WriteInMode.NamedPlusTotal)
            If Not named Then Continue For
            If k.CandidateStatus = "withdrawn" OrElse k.CandidateStatus = "deceased" Then Continue For
            list.Add(New Candidate With {
                .FeedId = k.Id,
                .FullName = If(k.BallotName <> "", k.BallotName, k.Label),
                .FirstName = k.First, .MiddleName = k.Middle, .LastName = k.Last, .Suffix = k.Suffix,
                .Party = k.Party})
        Next
        If mode <> WriteInMode.Exclude AndAlso c.Candidates.Any(Function(k) k.IsWriteInRow OrElse k.CandidateType = "qualified_write_in") Then
            list.Add(New Candidate With {.FeedId = WriteInId, .FullName = "Write-in"})
        End If
        Return list
    End Function

    ' ---------------------------------------------------------------------------------------------
    ' Results

    ''' <summary>
    ''' The write-in votes for one county: the contest total less the named lines that are sent
    ''' separately. This holds under both county rules - a write-in total row that already contains
    ''' the named write-ins, and named write-ins counted on top of an unqualified row.
    ''' </summary>
    Private Function WriteInVotes(contest As ApiContest, res As ApiContestResult, votes As Dictionary(Of String, Long?), mode As WriteInMode) As Long?
        Dim total As Long?
        If res.TotalVotes.HasValue Then
            total = res.TotalVotes
        Else
            ' The documented rule: ballot lines plus either the write-in total row or the itemised rows.
            Dim sum As Long = 0
            Dim haveTotalRow = False
            For Each k In contest.Candidates
                Dim v As Long? = Nothing
                votes.TryGetValue(k.Id, v)
                If Not v.HasValue Then Continue For
                If k.CandidateType = "write_in_total" Then
                    haveTotalRow = True
                    sum += v.Value
                ElseIf k.CandidateType = "ballot" Then
                    sum += v.Value
                End If
            Next
            If Not haveTotalRow Then
                For Each k In contest.Candidates
                    Dim v As Long? = Nothing
                    If k.CandidateType <> "ballot" AndAlso votes.TryGetValue(k.Id, v) AndAlso v.HasValue Then sum += v.Value
                Next
            End If
            total = sum
        End If

        Dim named As Long = 0
        For Each k In contest.Candidates
            Dim counted = k.CandidateType = "ballot" OrElse (k.CandidateType = "qualified_write_in" AndAlso mode = WriteInMode.NamedPlusTotal)
            If Not counted Then Continue For
            Dim v As Long? = Nothing
            If votes.TryGetValue(k.Id, v) AndAlso v.HasValue Then named += v.Value
        Next
        Dim remainder = total.Value - named
        ' A negative remainder means the county's own rows contradict each other, which is a
        ' reason to stop and look rather than to invent a number.
        Return If(remainder >= 0, CType(remainder, Long?), Nothing)
    End Function

    ''' <summary>
    ''' One <see cref="FeedContest"/> per county copy of each contest, so the existing results engine
    ''' can pair, add the counties together and send them.
    ''' </summary>
    Public Function ToFeedContests(def As ApiDefinition, results As ApiResults, options As MonitorOptions) As List(Of FeedContest)
        Dim list As New List(Of FeedContest)
        Dim skip As New HashSet(Of String)(options.SkipCounties.Select(Function(s) FeedNode.Norm(s)))

        For Each county In results.Counties
            If skip.Contains(FeedNode.Norm(county.County)) Then Continue For

            ' Nothing is loaded for this county, so it has no numbers to send.
            If county.Status = "not_started" Then Continue For

            Dim countyBlockers As New List(Of String)
            If options.BlockOnCountyError Then
                For Each w In county.CountyErrors
                    countyBlockers.Add($"{county.County}: {w.Message}")
                Next
            End If

            For Each res In county.Results
                Dim c = def.ContestById(res.ContestId)
                If c Is Nothing Then Continue For                       ' a mid-build ID the definition has not caught up with
                If c.Status = "no_election" Then Continue For

                Dim fc As New FeedContest With {
                    .FeedId = c.Id, .Office = OfficeOf(c), .ContestType = c.ContestType,
                    .Level = LevelOf(c), .State = options.StateCode,
                    .Area = county.County, .CountyStatus = county.Status,
                    .Winners = c.Winners, .RaceType = TypeOf_(c),
                    .PrecinctsReporting = res.PrecinctsReporting, .PrecinctsTotal = res.PrecinctsTotal,
                    .WorstSeverity = res.WorstSeverity}
                fc.Blockers.AddRange(countyBlockers)
                If county.Status = "zero_report" Then fc.Warnings.Add($"{county.County} is still a zero report ({county.StatusLabel}).")
                If options.HoldOnContestError AndAlso String.Equals(res.WorstSeverity, "error", StringComparison.OrdinalIgnoreCase) Then
                    fc.Blockers.Add($"{county.County} flags this contest as unreliable ({String.Join(", ", res.WarningCodes)}).")
                End If

                Dim votes As New Dictionary(Of String, Long?)(StringComparer.Ordinal)
                For Each rc In res.Candidates
                    votes(rc.CandidateId) = rc.Votes
                Next

                For Each k In c.Candidates
                    Dim counted = k.CandidateType = "ballot" OrElse
                                  (k.CandidateType = "qualified_write_in" AndAlso options.WriteIns = WriteInMode.NamedPlusTotal)
                    If Not counted Then Continue For
                    If k.CandidateStatus = "withdrawn" OrElse k.CandidateStatus = "deceased" Then Continue For
                    Dim v As Long? = Nothing
                    votes.TryGetValue(k.Id, v)
                    fc.Candidates.Add(New FeedCandidate With {
                        .CandidateId = k.Id,
                        .FullName = If(k.BallotName <> "", k.BallotName, k.Label),
                        .Party = k.Party, .Votes = v})
                Next

                If options.WriteIns <> WriteInMode.Exclude AndAlso
                   c.Candidates.Any(Function(k) k.IsWriteInRow OrElse k.CandidateType = "qualified_write_in") Then
                    fc.Candidates.Add(New FeedCandidate With {
                        .CandidateId = WriteInId, .FullName = "Write-in", .WriteIn = True,
                        .Votes = WriteInVotes(c, res, votes, options.WriteIns)})
                End If

                list.Add(fc)
            Next
        Next
        Return list
    End Function

    ''' <summary>Contests worth setting up: everything the API knows about, minus races not being held.</summary>
    Public Function SelectableContests(def As ApiDefinition) As List(Of ApiContest)
        Return def.Contests.Where(Function(c) c.Status <> "no_election").ToList()
    End Function

    ''' <summary>m/d/yyyy for an API date of the form YYYY-MM-DD; blank when it cannot be read.</summary>
    Public Function ElectionDateText(apiDate As String) As String
        Dim d As Date
        If Date.TryParseExact(If(apiDate, "").Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, d) Then
            Return d.ToString("M/d/yyyy", CultureInfo.InvariantCulture)
        End If
        Return ""
    End Function
End Module
