Option Strict On
Option Infer On

Imports System.Collections.Concurrent
Imports System.Globalization
Imports System.Text
Imports System.Text.RegularExpressions

''' <summary>Pairs feed contests with setup races, and feed candidates with setup candidates.</summary>
Public Module RaceMatcher

    ''' <summary>A match is only taken automatically at or above this score...</summary>
    Public Const AcceptScore As Double = 0.7

    ''' <summary>...and when the runner-up scores at least this much lower.</summary>
    Public Const AcceptMargin As Double = 0.1

    Private ReadOnly StopWords As New HashSet(Of String) From {
        "the", "of", "for", "a", "an", "and", "to", "in", "no", "number", "nr", "member", "members", "congressional", "vote"}

    Private ReadOnly Synonyms As New Dictionary(Of String, String) From {
        {"rep", "house"}, {"reps", "house"}, {"representative", "house"}, {"representatives", "house"},
        {"congressman", "house"}, {"congresswoman", "house"}, {"congress", "house"},
        {"senator", "senate"}, {"senators", "senate"},
        {"gov", "governor"}, {"lt", "lieutenant"}, {"dist", "district"}, {"distr", "district"},
        {"prop", "proposition"}, {"measure", "proposition"}, {"atty", "attorney"}, {"supt", "superintendent"},
        {"assemblymember", "assembly"}, {"assemblyman", "assembly"}, {"assemblywoman", "assembly"}, {"assm", "assembly"},
        {"councilmember", "council"}, {"councilmembers", "council"}, {"councilman", "council"}, {"councilwoman", "council"}, {"councilmen", "council"},
        {"appellate", "appeal"}, {"appeals", "appeal"}, {"equal", "equalization"},
        {"comm", "community"}, {"elem", "elementary"}, {"sch", "school"}, {"jt", "joint"},
        {"first", "1"}, {"second", "2"}, {"third", "3"}, {"fourth", "4"}, {"fifth", "5"},
        {"sixth", "6"}, {"seventh", "7"}, {"eighth", "8"}, {"ninth", "9"}, {"tenth", "10"}
    }

    ''' <summary>Abbreviations that stand for several words.</summary>
    Private ReadOnly Expansions As New Dictionary(Of String, String) From {
        {"usd", "unified school district"}, {"uhsd", "union high school district"}, {"ccd", "community college district"},
        {"cc", "community college"}, {"hs", "high school"}, {"boe", "board equalization"}
    }

    Private ReadOnly TokenCache As New ConcurrentDictionary(Of String, HashSet(Of String))
    Private ReadOnly NameCache As New ConcurrentDictionary(Of String, List(Of String))

    Private Function StripAccents(s As String) As String
        Dim sb As New StringBuilder(s.Length)
        For Each c In s.Normalize(NormalizationForm.FormD)
            If CharUnicodeInfo.GetUnicodeCategory(c) <> UnicodeCategory.NonSpacingMark Then sb.Append(c)
        Next
        Return sb.ToString()
    End Function

    ''' <summary>
    ''' Word tokens of an office/location description with abbreviations unified. Also removes a trailing
    ''' "(Vote for N)", reads "1 - Some Bond Act" as "Proposition 1" and "D - Some Tax" as "Measure D".
    ''' The returned set is shared; do not change it.
    ''' </summary>
    Public Function Tokens(text As String) As HashSet(Of String)
        Dim key = If(text, "")
        Dim cached As HashSet(Of String) = Nothing
        If TokenCache.TryGetValue(key, cached) Then Return cached

        Dim t = StripAccents(key).ToLowerInvariant()
        t = t.Replace("'", "").Replace(ChrW(&H2019), "")
        t = Regex.Replace(t, "\(\s*vote\s+for\s+\d+\s*\)", " ")
        Dim lead = Regex.Match(t, "^\s*(\d{1,3})\s+-\s+")
        If lead.Success Then
            t = "proposition " & lead.Groups(1).Value
        Else
            Dim letter = Regex.Match(t, "^\s*([a-z]{1,2}(?:-\d+)?)\s+-\s+(.*)$")
            If letter.Success Then t = "proposition " & letter.Groups(1).Value & " " & letter.Groups(2).Value
        End If
        t = Regex.Replace(t, "\bu\.\s*s\.?", " us ")
        t = t.Replace("united states", " us ")
        t = Regex.Replace(t, "(\d+)(st|nd|rd|th)\b", "$1")
        t = Regex.Replace(t, "[^a-z0-9]+", " ")

        Dim result As New HashSet(Of String)
        For Each w In t.Split(" "c)
            If w = "" OrElse StopWords.Contains(w) Then Continue For
            Dim expanded As String = Nothing
            If Expansions.TryGetValue(w, expanded) Then
                For Each x In expanded.Split(" "c)
                    result.Add(x)
                Next
                Continue For
            End If
            Dim mapped As String = Nothing
            Dim word = If(Synonyms.TryGetValue(w, mapped), mapped, w)
            If Not StopWords.Contains(word) Then result.Add(word)
        Next
        If TokenCache.Count > 50000 Then TokenCache.Clear()
        TokenCache(key) = result
        Return result
    End Function

    ' Word weights: a word found in few setup races (a name, a place) says more than one found in many ("district").
    Private _weights As Dictionary(Of String, Double)

    ''' <summary>Learns how common each word is across the setup races. Call before scoring a batch of contests.</summary>
    Public Sub Prepare(races As IEnumerable(Of Race))
        Dim df As New Dictionary(Of String, Integer)
        Dim n = 0
        For Each r In races
            n += 1
            For Each t In Tokens(RaceText(r))
                df(t) = If(df.ContainsKey(t), df(t) + 1, 1)
            Next
        Next
        Dim w As New Dictionary(Of String, Double)
        For Each kv In df
            w(kv.Key) = Math.Log((n + 1.0) / (kv.Value + 0.5)) + 0.2
        Next
        _weights = w
    End Sub

    Private Function Weight(token As String) As Double
        If _weights Is Nothing Then Return 1
        Dim v As Double
        Return If(_weights.TryGetValue(token, v), v, 0.3)
    End Function

    Private Function WeightOf(tokens As IEnumerable(Of String)) As Double
        Dim sum = 0.0
        For Each t In tokens
            sum += Weight(t)
        Next
        Return sum
    End Function

    Private Function Numbers(tokens As HashSet(Of String)) As HashSet(Of String)
        Return New HashSet(Of String)(tokens.Where(Function(t) t.All(AddressOf Char.IsDigit)))
    End Function

    Private Function PlaceTokens(place As String) As HashSet(Of String)
        Dim x = New HashSet(Of String)(Tokens(place))
        x.Remove("county")
        x.Remove("city")
        x.Remove("town")
        Return x
    End Function

    Private Function SamePlace(a As String, b As String) As Boolean
        Return PlaceTokens(a).SetEquals(PlaceTokens(b))
    End Function

    ' ---------------------------------------------------------------------------------------------
    ' Contests and races as comparable text

    Private Function ContestText(c As FeedContest) As String
        Return c.Office & " " & c.SeatName & " " & c.SeatNumber & If(c.District <> "", " district " & c.District, "") &
               " " & c.City & If(c.City = "" AndAlso c.Area = "", " " & c.County, "")
    End Function

    Private Function RaceText(r As Race) As String
        Return r.Office & " " & r.SeatName & " " & r.SeatNumber & If(r.District <> "", " district " & r.District, "") &
               " " & r.City & If(r.City = "", " " & r.County, "")
    End Function

    ''' <summary>0 to 1 from the wording alone.</summary>
    Private Function TextScore(contest As FeedContest, race As Race) As Double
        Dim a = Tokens(ContestText(contest))
        Dim b = Tokens(RaceText(race))
        If a.Count = 0 OrElse b.Count = 0 Then Return 0

        ' The county a contest was reported under counts as part of its wording only when the setup race names it too.
        If contest.Area <> "" Then
            Dim withArea = New HashSet(Of String)(a)
            For Each t In PlaceTokens(contest.Area)
                If b.Contains(t) Then withArea.Add(t)
            Next
            a = withArea
        End If

        Dim na = Numbers(a)
        Dim nb = Numbers(b)
        Dim numberFactor = 1.0
        If na.Count > 0 AndAlso nb.Count > 0 Then
            If Not (na.IsSubsetOf(nb) OrElse nb.IsSubsetOf(na)) Then Return 0
            If Not na.SetEquals(nb) Then numberFactor = 0.85
        ElseIf (na.Count > 0) <> (nb.Count > 0) Then
            numberFactor = 0.6
        End If

        Dim sharedWeight = WeightOf(a.Intersect(b))
        Dim weightA = WeightOf(a)
        Dim weightB = WeightOf(b)
        Dim jaccard = sharedWeight / (weightA + weightB - sharedWeight)
        Dim containment = sharedWeight / Math.Min(weightA, weightB)
        Dim result = (jaccard + containment) / 2 * numberFactor

        ' A city race has to name the city, somewhere in the contest's own wording.
        If race.City <> "" AndAlso contest.City = "" Then
            Dim city = PlaceTokens(race.City)
            If city.Count > 0 AndAlso Not city.IsSubsetOf(a) Then result *= 0.5
        End If
        Return result
    End Function

    ' ---------------------------------------------------------------------------------------------
    ' Candidate evidence

    Private Function IsYesNo(name As String) As Boolean
        Dim t = NameTokens(name)
        Return t.Count = 1 AndAlso (t(0) = "yes" OrElse t(0) = "no")
    End Function

    Private Function NamedFeed(c As FeedContest) As List(Of FeedCandidate)
        Return c.Candidates.Where(Function(f) Not f.WriteIn AndAlso Not IsWriteIn(f.FullName) AndAlso Not IsYesNo(f.FullName)).ToList()
    End Function

    Private Function NamedSetup(r As Race) As List(Of Candidate)
        Return r.Candidates.Where(Function(s) Not IsWriteIn(s.DisplayName) AndAlso Not IsYesNo(s.DisplayName)).ToList()
    End Function

    ''' <summary>Hard conflicts (different state, party, county or city) make a pairing impossible.</summary>
    Private Function Conflicts(contest As FeedContest, race As Race) As Boolean
        If contest.State <> "" AndAlso race.State <> "" AndAlso Not String.Equals(contest.State, race.State, StringComparison.OrdinalIgnoreCase) Then Return True
        If contest.Party <> "" AndAlso race.Party <> "" AndAlso Not PartiesMatch(contest.Party, race.Party) Then Return True
        If contest.County <> "" AndAlso race.County <> "" AndAlso Not SamePlace(contest.County, race.County) Then Return True
        If contest.City <> "" AndAlso race.City <> "" AndAlso Not SamePlace(contest.City, race.City) Then Return True
        Return False
    End Function

    ''' <summary>
    ''' 0 to 1: how likely the feed contest and the setup race are the same race. 0 means incompatible.
    ''' Two kinds of evidence: the wording, and the candidates. The same people on the ballot is strong
    ''' evidence even when the two sides word the office very differently; when both sides have named
    ''' candidates and not one of them is shared, the pairing is almost certainly wrong.
    ''' </summary>
    Public Function Score(contest As FeedContest, race As Race) As Double
        If Conflicts(contest, race) Then Return 0
        Dim text = TextScore(contest, race)

        Dim feedNamed = NamedFeed(contest)
        Dim setupNamed = NamedSetup(race)
        If feedNamed.Count = 0 OrElse setupNamed.Count = 0 Then Return text

        Dim map = MatchCandidates(feedNamed, setupNamed)
        Dim matched = map.Where(Function(i) i >= 0).Count()
        If matched = 0 Then Return text * 0.3

        Dim overlap = matched / CDbl(feedNamed.Count)
        Dim coverage = matched / CDbl(setupNamed.Count)
        Dim byPeople = If(overlap >= 0.67, 0.5 + 0.5 * Math.Min(overlap, coverage), 0.0)
        Return Math.Max(text, byPeople)
    End Function

    ''' <summary>D matches DEM, R matches REP, and so on (first letter).</summary>
    Public Function PartiesMatch(a As String, b As String) As Boolean
        Dim x = FeedNode.Norm(a)
        Dim y = FeedNode.Norm(b)
        If x = "" OrElse y = "" Then Return True
        Return x = y OrElse x(0) = y(0)
    End Function

    Public Class RaceMatch
        Public Property Race As Race
        Public Property Score As Double
        Public Property How As String = ""
        Public Property Suggestion As String = ""
    End Class

    ''' <summary>The best-scoring setup races for a feed contest, best first (zero scores left out).</summary>
    Public Function Rank(contest As FeedContest, races As IEnumerable(Of Race), take As Integer) As List(Of KeyValuePair(Of Race, Double))
        Return races.Select(Function(r) New KeyValuePair(Of Race, Double)(r, Score(contest, r))).
                     Where(Function(kv) kv.Value > 0).OrderByDescending(Function(kv) kv.Value).Take(take).ToList()
    End Function

    ''' <summary>
    ''' Order of preference: the Feed ID column in the setup CSV, a saved manual pairing, then the best
    ''' automatic match. Returns a result with Race = Nothing when nothing is safe to pick.
    ''' </summary>
    Public Function FindRace(contest As FeedContest, races As IEnumerable(Of Race), crosswalk As Dictionary(Of String, String)) As RaceMatch
        Dim list = races.ToList()

        If contest.FeedId <> "" Then
            Dim byId = list.Where(Function(r) r.FeedId <> "" AndAlso String.Equals(r.FeedId, contest.FeedId, StringComparison.OrdinalIgnoreCase)).ToList()
            If byId.Count = 1 Then Return New RaceMatch With {.Race = byId(0), .Score = 1, .How = "Feed ID column"}
        End If

        Dim savedId As String = Nothing
        If crosswalk IsNot Nothing AndAlso crosswalk.TryGetValue(contest.Key, savedId) Then
            Dim saved = list.FirstOrDefault(Function(r) String.Equals(r.ID, savedId, StringComparison.OrdinalIgnoreCase))
            If saved IsNot Nothing Then Return New RaceMatch With {.Race = saved, .Score = 1, .How = "paired by hand"}
        End If

        Dim ranked = Rank(contest, list, 2)
        If ranked.Count = 0 Then Return New RaceMatch()

        Dim best = ranked(0)
        Dim second = If(ranked.Count > 1, ranked(1).Value, 0.0)
        Dim sug = $"closest: {best.Key.ID} ({best.Key.Office}), score {best.Value:0.00}"
        If best.Value >= AcceptScore AndAlso best.Value - second >= AcceptMargin Then
            Return New RaceMatch With {.Race = best.Key, .Score = best.Value, .How = $"auto, score {best.Value:0.00}"}
        End If
        If best.Value >= 0.4 Then Return New RaceMatch With {.Suggestion = sug, .Score = best.Value}
        Return New RaceMatch()
    End Function

    ' ---------------------------------------------------------------------------------------------
    ' Candidate names

    Private ReadOnly NameSuffixes As New HashSet(Of String) From {"jr", "sr", "ii", "iii", "iv"}

    ''' <summary>Name tokens for comparing people: no accents, punctuation, nicknames in quotes, suffixes, or "Last, First" order.</summary>
    Public Function NameTokens(name As String) As List(Of String)
        Dim key = If(name, "")
        Dim cached As List(Of String) = Nothing
        If NameCache.TryGetValue(key, cached) Then Return cached

        Dim t = key.Trim()
        t = Regex.Replace(t, """[^""]*""|" & ChrW(&H201C) & "[^" & ChrW(&H201D) & "]*" & ChrW(&H201D) & "|\([^)]*\)", " ")
        Dim comma = t.IndexOf(","c)
        If comma > 0 Then
            Dim after = t.Substring(comma + 1).Trim()
            Dim afterTokens = Regex.Split(after.ToLowerInvariant().Replace(".", ""), "\s+")
            If afterTokens.Length = 1 AndAlso NameSuffixes.Contains(afterTokens(0)) Then
                t = t.Substring(0, comma) & " " & after
            Else
                t = after & " " & t.Substring(0, comma)
            End If
        End If
        t = StripAccents(t).ToLowerInvariant().Replace("'", "").Replace(ChrW(&H2019), "")
        t = Regex.Replace(t, "[^a-z0-9]+", " ")
        Dim result = t.Split(" "c).Where(Function(w) w <> "" AndAlso Not NameSuffixes.Contains(w)).ToList()
        ' "BONDS YES" / "BONDS NO" are just Yes and No
        If result.Count = 2 AndAlso result(0) = "bonds" AndAlso (result(1) = "yes" OrElse result(1) = "no") Then result = New List(Of String) From {result(1)}
        If NameCache.Count > 50000 Then NameCache.Clear()
        NameCache(key) = result
        Return result
    End Function

    Private ReadOnly Nicknames As New Dictionary(Of String, String) From {
        {"bill", "william"}, {"will", "william"}, {"billy", "william"}, {"bob", "robert"}, {"rob", "robert"}, {"bobby", "robert"},
        {"mike", "michael"}, {"jim", "james"}, {"jimmy", "james"}, {"dick", "richard"}, {"rick", "richard"}, {"rich", "richard"},
        {"tom", "thomas"}, {"tommy", "thomas"}, {"tony", "anthony"}, {"joe", "joseph"}, {"chris", "christopher"},
        {"dave", "david"}, {"dan", "daniel"}, {"danny", "daniel"}, {"steve", "steven"}, {"stephen", "steven"},
        {"matt", "matthew"}, {"nick", "nicholas"}, {"ed", "edward"}, {"eddie", "edward"}, {"ted", "edward"},
        {"kate", "katherine"}, {"kathy", "katherine"}, {"catherine", "katherine"}, {"liz", "elizabeth"}, {"beth", "elizabeth"},
        {"jen", "jennifer"}, {"jenny", "jennifer"}, {"sue", "susan"}, {"andy", "andrew"}, {"drew", "andrew"}, {"greg", "gregory"},
        {"ken", "kenneth"}, {"ron", "ronald"}, {"tim", "timothy"}, {"jeff", "jeffrey"}, {"larry", "lawrence"},
        {"sam", "samuel"}, {"ben", "benjamin"}, {"alex", "alexander"}, {"pete", "peter"}, {"charlie", "charles"}, {"chuck", "charles"}
    }

    Private Function CanonicalFirst(first As String) As String
        Dim full As String = Nothing
        Return If(Nicknames.TryGetValue(first, full), full, first)
    End Function

    ' Same first name, a known nickname of it, one a prefix of the other (Pat/Patrick), or an initial (J/Jane).
    Private Function FirstNamesCompatible(a As String, b As String) As Boolean
        If a = b Then Return True
        Dim x = CanonicalFirst(a)
        Dim y = CanonicalFirst(b)
        Return x = y OrElse x.StartsWith(y, StringComparison.Ordinal) OrElse y.StartsWith(x, StringComparison.Ordinal)
    End Function

    Private Function SetupNameText(c As Candidate) As String
        If c.FullName <> "" Then Return c.FullName
        Return String.Join(" ", {c.FirstName, c.MiddleName, c.LastName}.Where(Function(s) s <> ""))
    End Function

    ''' <summary>
    ''' The name as tokens, plus (when the name has a nickname in quotes or brackets) a version where the
    ''' nickname replaces the first name: TROY "TJ" WELCH gives [troy welch] and [tj welch].
    ''' </summary>
    Private Function NameVariants(name As String) As List(Of List(Of String))
        Dim result As New List(Of List(Of String)) From {NameTokens(name)}
        Dim m = Regex.Match(If(name, ""), """([^""]+)""|" & ChrW(&H201C) & "([^" & ChrW(&H201D) & "]+)" & ChrW(&H201D) & "|\(([^)]+)\)")
        If m.Success Then
            Dim nick = If(m.Groups(1).Success, m.Groups(1).Value, If(m.Groups(2).Success, m.Groups(2).Value, m.Groups(3).Value))
            Dim nickTokens = NameTokens(nick)
            Dim baseTokens = result(0)
            If nickTokens.Count >= 1 AndAlso nickTokens.Count <= 2 AndAlso baseTokens.Count >= 2 AndAlso Not IsWriteIn(nick) Then
                Dim alt As New List(Of String)(nickTokens)
                alt.AddRange(baseTokens.Skip(1))
                result.Add(alt)
            End If
        End If
        Return result
    End Function

    ''' <summary>
    ''' For each feed candidate, the index of the setup candidate it is (or -1). Four passes from strictest
    ''' to loosest: same name; same first and last; same last name and a compatible first name (nickname,
    ''' prefix or initial); same last name when one side gives only a last name. A pass never guesses
    ''' between two equally good setup candidates.
    ''' </summary>
    Public Function MatchCandidates(feed As IList(Of FeedCandidate), setup As IList(Of Candidate)) As Integer()
        Dim result(feed.Count - 1) As Integer
        For i = 0 To result.Length - 1
            result(i) = -1
        Next
        Dim used As New HashSet(Of Integer)

        ' When both sides carry candidate IDs (the monitor API gives one), those settle it outright.
        Dim byId As New Dictionary(Of String, Integer)(StringComparer.Ordinal)
        For j = 0 To setup.Count - 1
            Dim id = If(setup(j).FeedId, "")
            If id <> "" AndAlso Not byId.ContainsKey(id) Then byId(id) = j
        Next
        If byId.Count > 0 Then
            For i = 0 To feed.Count - 1
                Dim id = If(feed(i).CandidateId, "")
                Dim j As Integer
                If id <> "" AndAlso byId.TryGetValue(id, j) AndAlso Not used.Contains(j) Then
                    result(i) = j
                    used.Add(j)
                End If
            Next
            ' Every feed line carried an ID, so names are not consulted at all.
            If feed.All(Function(f) f.CandidateId <> "") Then Return result
        End If
        Dim fVar = feed.Select(Function(f) NameVariants(f.FullName)).ToList()
        Dim sVar = setup.Select(Function(s) NameVariants(SetupNameText(s))).ToList()

        For pass = 1 To 4
            For i = 0 To feed.Count - 1
                If result(i) >= 0 OrElse fVar(i)(0).Count = 0 Then Continue For
                Dim hits As New List(Of Integer)
                For j = 0 To setup.Count - 1
                    If used.Contains(j) OrElse sVar(j)(0).Count = 0 Then Continue For
                    Dim ok = False
                    For Each f In fVar(i)
                        For Each s In sVar(j)
                            Select Case pass
                                Case 1 : ok = ok OrElse f.SequenceEqual(s)
                                Case 2 : ok = ok OrElse (f.Count >= 2 AndAlso s.Count >= 2 AndAlso f(0) = s(0) AndAlso f.Last() = s.Last())
                                Case 3 : ok = ok OrElse (f.Count >= 2 AndAlso s.Count >= 2 AndAlso f.Last() = s.Last() AndAlso FirstNamesCompatible(f(0), s(0)))
                                Case Else : ok = ok OrElse (f.Count > 0 AndAlso s.Count > 0 AndAlso f.Last() = s.Last() AndAlso (f.Count = 1 OrElse s.Count = 1))
                            End Select
                        Next
                    Next
                    If ok Then hits.Add(j)
                Next
                If hits.Count > 1 Then
                    Dim feedParty = feed(i).Party
                    Dim byParty = hits.Where(Function(k) feedParty <> "" AndAlso setup(k).Party <> "" AndAlso PartiesMatch(feedParty, setup(k).Party)).ToList()
                    If byParty.Count = 1 Then hits = byParty
                End If
                If hits.Count = 1 Then
                    result(i) = hits(0)
                    used.Add(hits(0))
                End If
            Next
        Next
        Return result
    End Function

    ''' <summary>"Write-in", "Unqualified Write In 2", "Scattering" and similar lines the setup CSV usually does not list.</summary>
    Public Function IsWriteIn(name As String) As Boolean
        Dim t = String.Join(" ", NameTokens(name))
        Return t.StartsWith("write in", StringComparison.Ordinal) OrElse t.StartsWith("writein", StringComparison.Ordinal) OrElse
               t.StartsWith("unqualified write", StringComparison.Ordinal) OrElse t.StartsWith("scattering", StringComparison.Ordinal) OrElse
               t = "other" OrElse t = "others"
    End Function

End Module
