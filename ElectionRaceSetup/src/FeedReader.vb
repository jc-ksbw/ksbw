Option Strict On
Option Infer On

Imports System.Text.RegularExpressions

''' <summary>
''' Optional mapping file for a feed whose layout auto-detection can't read. JSON, for example:
''' { "contests": "Contest", "candidates": "Candidates/Candidate",
'''   "fields": { "id": "@id", "office": "@name", "state": "@state" },
'''   "candidateFields": { "name": "Name", "votes": "Votes", "party": "Party" } }
''' Paths use the syntax of <see cref="FeedNode.SelectNodes"/>; fields left out fall back to auto-detection.
''' </summary>
Public Class FeedProfile
    Public Property ContestsPath As String = ""
    Public Property CandidatesPath As String = ""
    Public ReadOnly Property ContestFields As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
    Public ReadOnly Property CandidateFields As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)

    Public Shared Function Parse(text As String) As FeedProfile
        Dim root = FeedNode.ParseAny(text)
        Dim p As New FeedProfile With {.ContestsPath = root.Value("contests"), .CandidatesPath = root.Value("candidates")}
        If p.ContestsPath = "" Then Throw New FormatException("The mapping file needs a ""contests"" path.")
        If p.CandidatesPath = "" Then Throw New FormatException("The mapping file needs a ""candidates"" path.")
        Dim cf = root.Child("fields")
        If cf IsNot Nothing Then
            For Each c In cf.Children
                p.ContestFields(FeedNode.Norm(c.Name)) = c.ScalarText()
            Next
        End If
        Dim kf = root.Child("candidateFields")
        If kf IsNot Nothing Then
            For Each c In kf.Children
                p.CandidateFields(FeedNode.Norm(c.Name)) = c.ScalarText()
            Next
        End If
        Return p
    End Function
End Class

''' <summary>Turns a JSON or XML results feed into <see cref="FeedContest"/> objects.</summary>
Public Module FeedReader

    Private Function Aliases(ParamArray names As String()) As String()
        Return names
    End Function

    Private ReadOnly ContestAliases As New Dictionary(Of String, String()) From {
        {"id", Aliases("id", "raceid", "contestid", "racenumber", "contestnumber")},
        {"office", Aliases("office", "officename", "racename", "contestname", "contest", "racetitle", "title", "name", "description")},
        {"level", Aliases("level", "racelevel", "officelevel")},
        {"state", Aliases("state", "statepostal", "statecode", "statepostalcode", "stateabbr", "stateabbreviation", "statepostalabbreviation")},
        {"county", Aliases("county", "countyname")},
        {"city", Aliases("city", "cityname", "municipality")},
        {"district", Aliases("district", "districtname", "districtnumber", "districtno")},
        {"party", Aliases("raceparty", "primaryparty", "party")},
        {"type", Aliases("racetype", "contesttype", "type")},
        {"seatname", Aliases("seatname", "seat")},
        {"seatnumber", Aliases("seatnumber", "seatnum", "seatno")},
        {"precinctsreporting", Aliases("precinctsreporting", "precinctsreported", "reportingprecincts", "precinctsrpt", "precinctsin", "reporting")},
        {"precinctstotal", Aliases("precinctstotal", "totalprecincts", "numprecincts", "precincts")}
    }

    Private ReadOnly StrongOfficeAliases As String() = Aliases("office", "officename", "racename", "contestname", "contest", "racetitle", "title")

    Private ReadOnly CandidateAliases As New Dictionary(Of String, String()) From {
        {"name", Aliases("fullname", "candidatename", "ballotname", "displayname", "name", "candidate")},
        {"first", Aliases("firstname", "first", "ballotfirstname", "givenname")},
        {"middle", Aliases("middlename", "middle")},
        {"last", Aliases("lastname", "last", "ballotlastname", "surname")},
        {"suffix", Aliases("suffix", "namesuffix")},
        {"party", Aliases("party", "partyabbr", "partycode", "partyname", "politicalparty")},
        {"votes", Aliases("votes", "votecount", "totalvotes", "votetotal", "numvotes", "tally", "count")},
        {"winner", Aliases("winner", "iswinner", "elected", "won", "iselected")},
        {"incumbent", Aliases("incumbent", "isincumbent")},
        {"writein", Aliases("writein", "iswritein")}
    }

    Private ReadOnly AreaNodeNames As New HashSet(Of String) From {"county", "reportingunit", "jurisdiction", "area", "region"}

    Private ReadOnly Inheritable As New HashSet(Of String) From {"level", "state", "county", "city", "district"}

    ' ---------------------------------------------------------------------------------------------
    ' Field lookup

    Private Function Lookup(node As FeedNode, names As String()) As String
        For Each nm In names
            For Each c In node.Children
                If c.Children.Count = 0 AndAlso c.Text <> "" AndAlso FeedNode.Norm(c.Name) = nm Then Return c.Text.Trim()
            Next
            For Each kv In node.Attributes
                If kv.Value <> "" AndAlso FeedNode.Norm(kv.Key) = nm Then Return kv.Value.Trim()
            Next
        Next
        Return ""
    End Function

    Private Function HasField(node As FeedNode, names As String()) As Boolean
        For Each nm In names
            For Each c In node.Children
                If c.Children.Count = 0 AndAlso FeedNode.Norm(c.Name) = nm Then Return True
            Next
            For Each kv In node.Attributes
                If FeedNode.Norm(kv.Key) = nm Then Return True
            Next
        Next
        Return False
    End Function

    Private Function ContestField(node As FeedNode, key As String, profile As FeedProfile) As String
        If profile IsNot Nothing AndAlso profile.ContestFields.ContainsKey(key) Then
            Return node.Value(profile.ContestFields(key))
        End If
        Dim v = Lookup(node, ContestAliases(key))
        If v = "" AndAlso Inheritable.Contains(key) Then
            Dim p = node.Parent
            Dim hops = 0
            While p IsNot Nothing AndAlso hops < 4 AndAlso v = ""
                v = Lookup(p, ContestAliases(key))
                p = p.Parent
                hops += 1
            End While
        End If
        Return v
    End Function

    Private Function CandidateField(node As FeedNode, key As String, profile As FeedProfile) As String
        If profile IsNot Nothing AndAlso profile.CandidateFields.ContainsKey(key) Then
            Return node.Value(profile.CandidateFields(key))
        End If
        Return Lookup(node, CandidateAliases(key))
    End Function

    Private Function IsTruthy(value As String) As Boolean
        Select Case FeedNode.Norm(value)
            Case "x", "y", "yes", "true", "t", "1", "w", "winner", "won", "elected"
                Return True
            Case Else
                Return False
        End Select
    End Function

    ''' <summary>Whole number from text such as "1,234" or "1234.0"; blank counts as 0; anything else is Nothing.</summary>
    Private Function ParseCount(text As String) As Long?
        Dim t = text.Replace(",", "").Replace(" ", "").Trim()
        If t = "" Then Return 0
        Dim n As Long
        If Long.TryParse(t, Globalization.NumberStyles.Integer, Globalization.CultureInfo.InvariantCulture, n) Then
            Return If(n >= 0, CType(n, Long?), Nothing)
        End If
        Dim d As Double
        If Double.TryParse(t, Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture, d) AndAlso d >= 0 AndAlso d = Math.Floor(d) AndAlso d < 9.0E+18 Then
            Return CLng(d)
        End If
        Return Nothing
    End Function

    ''' <summary>Accepts "12", "12/42" or "12 of 42". Returns False for blank/unreadable.</summary>
    Private Function ParsePrecincts(text As String, ByRef first As Integer, ByRef second As Integer, ByRef hasSecond As Boolean) As Boolean
        hasSecond = False
        Dim t = text.Trim()
        If t = "" Then Return False
        Dim m = Regex.Match(t, "^(\d[\d,]*)\s*(?:/|of)\s*(\d[\d,]*)$", RegexOptions.IgnoreCase)
        If m.Success Then
            hasSecond = Integer.TryParse(m.Groups(2).Value.Replace(",", ""), second)
            Return Integer.TryParse(m.Groups(1).Value.Replace(",", ""), first)
        End If
        Return Integer.TryParse(t.Replace(",", ""), Globalization.NumberStyles.Integer, Globalization.CultureInfo.InvariantCulture, first)
    End Function

    ' ---------------------------------------------------------------------------------------------
    ' Building contests

    Private Function BuildContest(contestNode As FeedNode, candidateNodes As List(Of FeedNode), profile As FeedProfile) As FeedContest
        Dim c As New FeedContest With {
            .FeedId = ContestField(contestNode, "id", profile),
            .Office = ContestField(contestNode, "office", profile),
            .Level = RaceImporter.NormalizeLevel(ContestField(contestNode, "level", profile)),
            .State = RaceImporter.NormalizeState(ContestField(contestNode, "state", profile)),
            .County = ContestField(contestNode, "county", profile),
            .City = ContestField(contestNode, "city", profile),
            .District = ContestField(contestNode, "district", profile),
            .Party = ContestField(contestNode, "party", profile),
            .RaceType = ContestField(contestNode, "type", profile).ToUpperInvariant(),
            .SeatName = ContestField(contestNode, "seatname", profile),
            .SeatNumber = ContestField(contestNode, "seatnumber", profile)
        }

        ' "Governor (Vote for 1)" -> "Governor" and 1 winner
        Dim voteFor = Regex.Match(c.Office, "\s*\(\s*vote\s+for\s+(\d+)\s*\)\s*$", RegexOptions.IgnoreCase)
        If voteFor.Success Then
            c.Winners = Integer.Parse(voteFor.Groups(1).Value)
            c.Office = c.Office.Substring(0, voteFor.Index).Trim()
        End If

        ' which county / reporting area the contest sits under
        Dim up = contestNode.Parent
        While up IsNot Nothing
            If AreaNodeNames.Contains(FeedNode.Norm(up.Name)) Then
                c.Area = Lookup(up, Aliases("name", "countyname", "areaname", "county", "id"))
                If c.Area <> "" AndAlso c.County = "" AndAlso FeedNode.Norm(up.Name) = "county" Then c.County = c.Area
                Exit While
            End If
            up = up.Parent
        End While

        Dim rep As Integer, tot As Integer, hasTot As Boolean
        If ParsePrecincts(ContestField(contestNode, "precinctsreporting", profile), rep, tot, hasTot) Then
            c.PrecinctsReporting = rep
            If hasTot Then c.PrecinctsTotal = tot
        End If
        Dim total As Integer, ignore As Integer, ignoreFlag As Boolean
        If Not c.PrecinctsTotal.HasValue AndAlso ParsePrecincts(ContestField(contestNode, "precinctstotal", profile), total, ignore, ignoreFlag) Then
            c.PrecinctsTotal = total
        End If

        For Each cn In candidateNodes
            Dim name = CandidateField(cn, "name", profile)
            If name = "" Then
                name = String.Join(" ", {CandidateField(cn, "first", profile), CandidateField(cn, "middle", profile),
                                         CandidateField(cn, "last", profile), CandidateField(cn, "suffix", profile)}.Where(Function(s) s <> ""))
            End If
            If name = "" Then
                c.Warnings.Add("A candidate with no name was skipped.")
                Continue For
            End If
            Dim fc As New FeedCandidate With {
                .FullName = name,
                .Party = CandidateField(cn, "party", profile),
                .Winner = IsTruthy(CandidateField(cn, "winner", profile)),
                .Incumbent = IsTruthy(CandidateField(cn, "incumbent", profile)),
                .WriteIn = IsTruthy(CandidateField(cn, "writein", profile))
            }
            Dim rawVotes = CandidateField(cn, "votes", profile)
            fc.Votes = ParseCount(rawVotes)
            If Not fc.Votes.HasValue Then c.Warnings.Add($"Votes ""{rawVotes}"" for {name} is not a whole number.")
            c.Candidates.Add(fc)
        Next
        Return c
    End Function

    ' ---------------------------------------------------------------------------------------------
    ' Auto-detection

    Private Function IsCandidateLike(n As FeedNode) As Boolean
        Return HasField(n, CandidateAliases("votes")) AndAlso
               (HasField(n, CandidateAliases("name")) OrElse HasField(n, CandidateAliases("last")))
    End Function

    ''' <summary>Deepest nodes that look like a candidate (a name plus a vote count).</summary>
    Private Sub FindCandidates(n As FeedNode, result As List(Of FeedNode))
        Dim before = result.Count
        For Each c In n.Children
            FindCandidates(c, result)
        Next
        If result.Count = before AndAlso IsCandidateLike(n) Then result.Add(n)
    End Sub

    Private Function FindContestNode(groupParent As FeedNode) As FeedNode
        For Each set1 In {StrongOfficeAliases, ContestAliases("office")}
            Dim n = groupParent
            For hops = 0 To 5
                If n Is Nothing Then Exit For
                If Lookup(n, set1) <> "" Then Return n
                n = n.Parent
            Next
        Next
        Return groupParent
    End Function

    Private Function AutoDetect(root As FeedNode) As List(Of FeedContest)
        Dim candidates As New List(Of FeedNode)
        FindCandidates(root, candidates)

        Dim groups As New List(Of FeedNode)
        Dim byParent As New Dictionary(Of FeedNode, List(Of FeedNode))
        For Each cn In candidates
            Dim p = If(cn.Parent, root)
            If Not byParent.ContainsKey(p) Then
                byParent(p) = New List(Of FeedNode)
                groups.Add(p)
            End If
            byParent(p).Add(cn)
        Next

        Dim contestOrder As New List(Of FeedNode)
        Dim groupsByContest As New Dictionary(Of FeedNode, List(Of FeedNode))
        For Each g In groups
            Dim cNode = FindContestNode(g)
            If Not groupsByContest.ContainsKey(cNode) Then
                groupsByContest(cNode) = New List(Of FeedNode)
                contestOrder.Add(cNode)
            End If
            groupsByContest(cNode).Add(g)
        Next

        Dim result As New List(Of FeedContest)
        For Each cNode In contestOrder
            Dim gs = groupsByContest(cNode)
            Dim contest = BuildContest(cNode, byParent(gs(0)), Nothing)
            If gs.Count > 1 Then
                contest.Warnings.Add($"{gs.Count} result groups (for example sub-areas) were found in this contest; only the first was used. Add a mapping file to choose.")
            End If
            If contest.Office = "" Then contest.Warnings.Add("No office/title found for this contest.")
            result.Add(contest)
        Next
        Return result
    End Function

    Private Function FromProfile(root As FeedNode, profile As FeedProfile) As List(Of FeedContest)
        Dim result As New List(Of FeedContest)
        For Each cn In root.SelectNodes(profile.ContestsPath)
            result.Add(BuildContest(cn, cn.SelectNodes(profile.CandidatesPath), profile))
        Next
        Return result
    End Function

    ''' <summary>Reads feed text (JSON or XML). Pass a profile only if auto-detection reads the feed wrongly.</summary>
    Public Function Read(text As String, Optional profile As FeedProfile = Nothing) As List(Of FeedContest)
        Dim root = FeedNode.ParseAny(text)
        Return If(profile Is Nothing, AutoDetect(root), FromProfile(root, profile))
    End Function
End Module
