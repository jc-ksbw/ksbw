Option Strict On
Option Infer On

Imports System.Globalization

''' <summary>
''' Reads the monitor API in either format. The JSON and XML carry the same data, so one reader
''' handles both: a JSON array becomes repeated child nodes, and an XML scalar becomes an attribute,
''' and <see cref="FeedNode"/> already hides that difference.
''' </summary>
Public Module MonitorReader

    ''' <summary>Looks like /api/results of the schema this app understands?</summary>
    Public Function LooksLikeResults(root As FeedNode) As Boolean
        If root Is Nothing Then Return False
        Return root.Value("schema_version") <> "" AndAlso
               (root.Child("counties") IsNot Nothing OrElse root.Child("county") IsNot Nothing)
    End Function

    Public Function LooksLikeContests(root As FeedNode) As Boolean
        If root Is Nothing Then Return False
        Return root.Value("version") <> "" AndAlso
               (root.Child("contests") IsNot Nothing OrElse root.Child("contest") IsNot Nothing)
    End Function

    ''' <summary>Children named either the JSON key (plural) or the XML element (singular).</summary>
    Private Function Kids(node As FeedNode, jsonName As String, xmlName As String) As List(Of FeedNode)
        Dim hits As New List(Of FeedNode)
        For Each c In node.Children
            Dim n = FeedNode.Norm(c.Name)
            If n = FeedNode.Norm(jsonName) OrElse n = FeedNode.Norm(xmlName) Then hits.Add(c)
        Next
        Return hits
    End Function

    ''' <summary>Both formats write null as an absent value, so blank means "not reported".</summary>
    Private Function NullableInt(node As FeedNode, path As String) As Integer?
        Dim raw = node.Value(path)
        If raw = "" Then Return Nothing
        Dim n As Integer
        If Integer.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, n) Then Return n
        Return Nothing
    End Function

    Private Function NullableLong(node As FeedNode, path As String) As Long?
        Dim raw = node.Value(path)
        If raw = "" Then Return Nothing
        Dim n As Long
        If Long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, n) Then Return n
        Dim d As Double
        If Double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, d) AndAlso d = Math.Floor(d) Then Return CLng(d)
        Return Nothing
    End Function

    Private Function PlainInt(node As FeedNode, path As String) As Integer
        Return If(NullableInt(node, path), 0)
    End Function

    Private Function ReadWarnings(owner As FeedNode, into As List(Of ApiWarning)) As List(Of ApiWarning)
        For Each w In Kids(owner, "warnings", "warning")
            into.Add(New ApiWarning With {
                .Code = w.Value("code"), .Severity = w.Value("severity"),
                .ContestId = w.Value("contest_id"), .CandidateId = w.Value("candidate_id"),
                .Message = If(w.Value("message") <> "", w.Value("message"), w.ScalarText())})
        Next
        Return into
    End Function

    ' ---------------------------------------------------------------------------------------------

    Public Function ReadDefinition(text As String) As ApiDefinition
        Dim root = FeedNode.ParseAny(text)
        If Not LooksLikeContests(root) Then Throw New FormatException("This is not an /api/contests response (no version and contests).")

        Dim d As New ApiDefinition With {
            .Version = PlainInt(root, "version"),
            .UpdatedAt = root.Value("updated_at"),
            .ElectionDate = root.Value("election_date")}
        For Each e In Kids(root, "override_errors", "override_error")
            If e.ScalarText() <> "" Then d.OverrideErrors.Add(e.ScalarText())
        Next

        For Each cn In Kids(root, "contests", "contest")
            Dim c As New ApiContest With {
                .Id = cn.Value("id"), .ContestType = cn.Value("contest_type"), .Status = cn.Value("status"),
                .Level = cn.Value("level"), .Jurisdiction = cn.Value("jurisdiction"),
                .JurisdictionType = cn.Value("jurisdiction_type"), .Office = cn.Value("office"),
                .SeatType = cn.Value("seat_type"), .SeatNumber = cn.Value("seat_number"),
                .TermYears = NullableInt(cn, "term_years"),
                .IsShortTerm = String.Equals(cn.Value("is_short_term"), "true", StringComparison.OrdinalIgnoreCase),
                .VoteFor = NullableInt(cn, "vote_for"),
                .MeasureLetter = cn.Value("measure_letter"), .MeasureTitle = cn.Value("measure_title"),
                .Threshold = cn.Value("threshold"), .RetentionName = cn.Value("retention_name")}
            If c.Id = "" Then Continue For

            For Each jc In Kids(cn, "jurisdiction_counties", "jurisdiction_county")
                If jc.ScalarText() <> "" Then c.JurisdictionCounties.Add(jc.ScalarText())
            Next
            For Each s In Kids(cn, "sources", "source")
                c.Sources.Add(New ApiSource With {.County = s.Value("county"), .SourceId = s.Value("source_id"), .Name = s.Value("source_name")})
            Next
            For Each kn In Kids(cn, "candidates", "candidate")
                Dim k As New ApiCandidate With {
                    .Id = kn.Value("id"), .CandidateType = kn.Value("candidate_type"),
                    .CandidateStatus = kn.Value("candidate_status"), .BallotName = kn.Value("ballot_name"),
                    .Label = kn.Value("label"), .First = kn.Value("first"), .Middle = kn.Value("middle"),
                    .Last = kn.Value("last"), .Suffix = kn.Value("suffix"), .Nickname = kn.Value("nickname"),
                    .Party = kn.Value("party"), .PersonId = kn.Value("person_id")}
                If k.Id = "" Then Continue For
                If k.CandidateType = "" Then k.CandidateType = "ballot"
                For Each s In Kids(kn, "sources", "source")
                    k.Sources.Add(New ApiSource With {.County = s.Value("county"), .Name = s.Value("ballot_name")})
                Next
                c.Candidates.Add(k)
            Next
            ReadWarnings(cn, c.Warnings)
            d.Contests.Add(c)
        Next
        Return d
    End Function

    ' ---------------------------------------------------------------------------------------------

    Public Function ReadResults(text As String) As ApiResults
        Dim root = FeedNode.ParseAny(text)
        If Not LooksLikeResults(root) Then Throw New FormatException("This is not an /api/results response (no schema_version and counties).")

        Dim r As New ApiResults With {
            .SchemaVersion = PlainInt(root, "schema_version"),
            .GeneratedAt = root.Value("generated_at"),
            .ElectionDate = root.Value("election_date"),
            .BallotVersion = PlainInt(root, "ballot_version"),
            .ContentHash = root.Value("content_hash")}

        For Each cn In Kids(root, "counties", "county")
            ' In the XML the element is <county county="Monterey">, so the name is an attribute, not the node.
            Dim county As New ApiCounty With {
                .County = If(cn.Value("county") <> "", cn.Value("county"), cn.ScalarText()),
                .Status = cn.Value("status"), .StatusLabel = cn.Value("status_label"),
                .ElectionLabel = cn.Value("election_label"), .FetchedAt = cn.Value("fetched_at"),
                .LastAttemptAt = cn.Value("last_attempt_at"), .ContentChangedAt = cn.Value("content_changed_at"),
                .ContentHash = cn.Value("content_hash"), .CountyTimestamp = cn.Value("county_timestamp"),
                .SourceUrl = cn.Value("source_url"), .ErrorText = cn.Value("error"), .Notice = cn.Value("notice")}
            ReadWarnings(cn, county.Warnings)

            For Each rn In Kids(cn, "results", "result")
                Dim res As New ApiContestResult With {
                    .ContestId = rn.Value("contest_id"), .SourceId = rn.Value("source_id"),
                    .TotalVotes = NullableLong(rn, "total_votes"),
                    .WorstSeverity = rn.Value("worst_severity")}
                If res.ContestId = "" Then Continue For

                ' JSON nests precincts; the XML puts them on the result as attributes.
                Dim prec = rn.Child("precincts")
                If prec IsNot Nothing Then
                    res.PrecinctsReporting = NullableInt(prec, "reporting")
                    res.PrecinctsTotal = NullableInt(prec, "total")
                Else
                    res.PrecinctsReporting = NullableInt(rn, "precincts_reporting")
                    res.PrecinctsTotal = NullableInt(rn, "precincts_total")
                End If

                Dim codes = rn.Value("warning_codes")
                If codes <> "" Then
                    ' XML: one space-separated attribute.
                    For Each code In codes.Split({" "c}, StringSplitOptions.RemoveEmptyEntries)
                        res.WarningCodes.Add(code)
                    Next
                Else
                    ' JSON: repeated child nodes.
                    For Each wc In Kids(rn, "warning_codes", "warning_code")
                        If wc.ScalarText() <> "" Then res.WarningCodes.Add(wc.ScalarText())
                    Next
                End If

                For Each kn In Kids(rn, "candidates", "candidate")
                    Dim id = kn.Value("candidate_id")
                    If id = "" Then Continue For
                    res.Candidates.Add(New ApiResultCandidate With {.CandidateId = id, .Votes = NullableLong(kn, "votes")})
                Next
                county.Results.Add(res)
            Next
            If county.County <> "" Then r.Counties.Add(county)
        Next
        Return r
    End Function
End Module
