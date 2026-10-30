Option Strict On
Option Infer On

Imports System.Globalization
Imports System.Text
Imports System.Text.RegularExpressions

''' <summary>Pairs feed contests with setup races, and feed candidates with setup candidates.</summary>
Public Module RaceMatcher

    ''' <summary>A match is only taken automatically at or above this score...</summary>
    Public Const AcceptScore As Double = 0.7

    ''' <summary>...and when the runner-up scores at least this much lower.</summary>
    Public Const AcceptMargin As Double = 0.1

    Private ReadOnly StopWords As New HashSet(Of String) From {"the", "of", "for", "a", "an", "and", "to", "in", "no", "number", "nr"}

    Private ReadOnly Synonyms As New Dictionary(Of String, String) From {
        {"rep", "house"}, {"reps", "house"}, {"representative", "house"}, {"representatives", "house"},
        {"congressman", "house"}, {"congresswoman", "house"},
        {"senator", "senate"}, {"senators", "senate"},
        {"gov", "governor"}, {"lt", "lieutenant"}, {"dist", "district"}, {"distr", "district"},
        {"prop", "proposition"}, {"measure", "proposition"}, {"atty", "attorney"}, {"supt", "superintendent"}
    }

    Private Function StripAccents(s As String) As String
        Dim sb As New StringBuilder(s.Length)
        For Each c In s.Normalize(NormalizationForm.FormD)
            If CharUnicodeInfo.GetUnicodeCategory(c) <> UnicodeCategory.NonSpacingMark Then sb.Append(c)
        Next
        Return sb.ToString()
    End Function

    ''' <summary>Lower-case word tokens of an office/location description, with common abbreviations unified.</summary>
    Public Function Tokens(text As String) As HashSet(Of String)
        Dim t = StripAccents(If(text, "")).ToLowerInvariant()
        t = t.Replace("'", "").Replace(ChrW(&H2019), "")
        t = Regex.Replace(t, "\bu\.\s*s\.?", " us ")
        t = t.Replace("united states", " us ")
        t = Regex.Replace(t, "(\d+)(st|nd|rd|th)\b", "$1")
        t = Regex.Replace(t, "[^a-z0-9]+", " ")
        Dim result As New HashSet(Of String)
        For Each w In t.Split(" "c)
            If w = "" OrElse StopWords.Contains(w) Then Continue For
            Dim mapped As String = Nothing
            result.Add(If(Synonyms.TryGetValue(w, mapped), mapped, w))
        Next
        Return result
    End Function

    Private Function RaceText(office As String, seatName As String, seatNumber As String, district As String, county As String, city As String) As String
        Return office & " " & seatName & " " & seatNumber & If(district <> "", " district " & district, "") & " " & county & " " & city
    End Function

    Private Function Numbers(tokens As HashSet(Of String)) As HashSet(Of String)
        Return New HashSet(Of String)(tokens.Where(Function(t) t.All(AddressOf Char.IsDigit)))
    End Function

    Private Function SamePlace(a As String, b As String) As Boolean
        Dim x = Tokens(a)
        Dim y = Tokens(b)
        x.Remove("county")
        y.Remove("county")
        x.Remove("city")
        y.Remove("city")
        Return x.SetEquals(y)
    End Function

    ''' <summary>0 to 1: how likely the feed contest and the setup race are the same race. 0 means incompatible.</summary>
    Public Function Score(contest As FeedContest, race As Race) As Double
        If contest.State <> "" AndAlso race.State <> "" AndAlso Not String.Equals(contest.State, race.State, StringComparison.OrdinalIgnoreCase) Then Return 0
        If contest.Party <> "" AndAlso race.Party <> "" AndAlso Not PartiesMatch(contest.Party, race.Party) Then Return 0
        If contest.County <> "" AndAlso race.County <> "" AndAlso Not SamePlace(contest.County, race.County) Then Return 0
        If contest.City <> "" AndAlso race.City <> "" AndAlso Not SamePlace(contest.City, race.City) Then Return 0

        Dim a = Tokens(RaceText(contest.Office, contest.SeatName, contest.SeatNumber, contest.District, contest.County, contest.City))
        Dim b = Tokens(RaceText(race.Office, race.SeatName, race.SeatNumber, race.District, race.County, race.City))
        If a.Count = 0 OrElse b.Count = 0 Then Return 0

        Dim na = Numbers(a)
        Dim nb = Numbers(b)
        If na.Count > 0 AndAlso nb.Count > 0 AndAlso Not na.SetEquals(nb) Then Return 0

        Dim shared_ = a.Intersect(b).Count()
        Dim jaccard = shared_ / CDbl(a.Union(b).Count())
        Dim containment = shared_ / CDbl(Math.Min(a.Count, b.Count))
        Dim result = (jaccard + containment) / 2

        If (na.Count > 0) <> (nb.Count > 0) Then result *= 0.6
        If (contest.Party <> "") <> (race.Party <> "") Then result *= 0.9
        Return result
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

    ''' <summary>
    ''' Order of preference: the Feed ID column in the setup CSV, a saved manual pairing, then the best
    ''' automatic text match. Returns a result with Race = Nothing when nothing is safe to pick.
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

        Dim scored = list.Select(Function(r) New With {.Race = r, .Score = RaceMatcher.Score(contest, r)}).
                          OrderByDescending(Function(x) x.Score).ToList()
        If scored.Count = 0 OrElse scored(0).Score <= 0 Then Return New RaceMatch()

        Dim best = scored(0)
        Dim second = If(scored.Count > 1, scored(1).Score, 0.0)
        Dim sug = $"closest: {best.Race.ID} ({best.Race.Office}), score {best.Score:0.00}"
        If best.Score >= AcceptScore AndAlso best.Score - second >= AcceptMargin Then
            Return New RaceMatch With {.Race = best.Race, .Score = best.Score, .How = $"auto, score {best.Score:0.00}"}
        End If
        If best.Score >= 0.4 Then Return New RaceMatch With {.Suggestion = sug, .Score = best.Score}
        Return New RaceMatch()
    End Function

    ' ---------------------------------------------------------------------------------------------
    ' Candidates

    Private ReadOnly NameSuffixes As New HashSet(Of String) From {"jr", "sr", "ii", "iii", "iv"}

    ''' <summary>Name tokens for comparing people: no accents, punctuation, nicknames in quotes, suffixes, or "Last, First" order.</summary>
    Public Function NameTokens(name As String) As List(Of String)
        Dim t = If(name, "").Trim()
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
        Return t.Split(" "c).Where(Function(w) w <> "" AndAlso Not NameSuffixes.Contains(w)).ToList()
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

    Private Function SetupName(c As Candidate) As List(Of String)
        If c.FullName <> "" Then Return NameTokens(c.FullName)
        Return NameTokens(String.Join(" ", {c.FirstName, c.MiddleName, c.LastName}.Where(Function(s) s <> "")))
    End Function

    ''' <summary>
    ''' For each feed candidate, the index of the setup candidate it is (or -1). Four passes from strictest
    ''' to loosest: same name; same first and last; same last name and a compatible first name (nickname,
    ''' prefix or initial); same last name when one side gives only a last name. A pass never guesses between two equally good setup candidates.
    ''' </summary>
    Public Function MatchCandidates(feed As IList(Of FeedCandidate), setup As IList(Of Candidate)) As Integer()
        Dim result(feed.Count - 1) As Integer
        For i = 0 To result.Length - 1
            result(i) = -1
        Next
        Dim used As New HashSet(Of Integer)
        Dim fTok = feed.Select(Function(f) NameTokens(f.FullName)).ToList()
        Dim sTok = setup.Select(AddressOf SetupName).ToList()

        For pass = 1 To 4
            For i = 0 To feed.Count - 1
                If result(i) >= 0 OrElse fTok(i).Count = 0 Then Continue For
                Dim f = fTok(i)
                Dim hits As New List(Of Integer)
                For j = 0 To setup.Count - 1
                    If used.Contains(j) OrElse sTok(j).Count = 0 Then Continue For
                    Dim s = sTok(j)
                    Dim ok As Boolean
                    Select Case pass
                        Case 1 : ok = f.SequenceEqual(s)
                        Case 2 : ok = f.Count >= 2 AndAlso s.Count >= 2 AndAlso f(0) = s(0) AndAlso f.Last() = s.Last()
                        Case 3 : ok = f.Count >= 2 AndAlso s.Count >= 2 AndAlso f.Last() = s.Last() AndAlso FirstNamesCompatible(f(0), s(0))
                        Case Else : ok = f.Last() = s.Last() AndAlso (f.Count = 1 OrElse s.Count = 1)
                    End Select
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

    ''' <summary>"Write-in", "Scattering" and similar lines the setup CSV usually does not list.</summary>
    Public Function IsWriteIn(name As String) As Boolean
        Dim t = String.Join(" ", NameTokens(name))
        Return t.StartsWith("write in", StringComparison.Ordinal) OrElse t.StartsWith("writein", StringComparison.Ordinal) OrElse
               t.StartsWith("scattering", StringComparison.Ordinal) OrElse t = "other" OrElse t = "others"
    End Function

End Module
