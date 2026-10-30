Option Strict On
Option Infer On

Imports System.Globalization
Imports System.Text

''' <summary>Every CSV column the importer knows how to use.</summary>
Public Enum RaceField
    RaceId
    FeedId
    Office
    Level
    State
    County
    City
    District
    RaceParty
    RaceType
    SeatName
    SeatNumber
    Winners
    PrecinctsTotal
    CandFullName
    CandFirst
    CandMiddle
    CandLast
    CandSuffix
    CandParty
    Incumbent
    CandId
End Enum

Public Class FieldDef
    Public ReadOnly Property Field As RaceField
    Public ReadOnly Property Label As String
    Public ReadOnly Property Aliases As String()

    Public Sub New(field As RaceField, label As String, ParamArray aliases As String())
        Me.Field = field
        Me.Label = label
        Me.Aliases = aliases
    End Sub
End Class

Public Class ImportResult
    Public ReadOnly Property Races As New List(Of Race)
    Public ReadOnly Property Messages As New List(Of String)
End Class

''' <summary>Turns CSV rows (one row per candidate) into races with candidates.</summary>
Public Module RaceImporter

    ''' <summary>Fields in the order they are shown in the column-mapping dialog. Aliases are tried in order.</summary>
    Public ReadOnly Fields As FieldDef() = {
        New FieldDef(RaceField.RaceId, "Race ID", "raceid", "contestid", "id", "race_id"),
        New FieldDef(RaceField.FeedId, "Feed ID (race ID in the results feed)", "feedid", "feedraceid", "feedcontestid", "externalid", "resultsid"),
        New FieldDef(RaceField.Office, "Office / race title", "office", "officename", "racename", "race", "contest", "contestname", "title", "position"),
        New FieldDef(RaceField.Level, "Level", "level", "racelevel", "officelevel"),
        New FieldDef(RaceField.State, "State", "state", "statepostal", "statecode", "statepostalcode", "st"),
        New FieldDef(RaceField.County, "County", "county", "countyname"),
        New FieldDef(RaceField.City, "City", "city", "cityname", "municipality"),
        New FieldDef(RaceField.District, "District", "district", "districtname", "dist"),
        New FieldDef(RaceField.RaceParty, "Race party (primaries)", "raceparty", "primaryparty", "racepartyabbr"),
        New FieldDef(RaceField.RaceType, "Race type", "racetype", "contesttype", "type"),
        New FieldDef(RaceField.SeatName, "Seat name", "seatname", "seat", "seatdescription"),
        New FieldDef(RaceField.SeatNumber, "Seat number", "seatnumber", "seatnum", "seatno"),
        New FieldDef(RaceField.Winners, "Number of winners", "winners", "numberofwinners", "numwinners", "votefor", "seats", "numberelected"),
        New FieldDef(RaceField.PrecinctsTotal, "Total precincts", "precinctstotal", "totalprecincts", "numprecincts", "precincts"),
        New FieldDef(RaceField.CandFullName, "Candidate full name", "candidate", "candidatename", "candidatefullname", "fullname", "name"),
        New FieldDef(RaceField.CandFirst, "Candidate first name", "first", "firstname", "candidatefirst", "candidatefirstname"),
        New FieldDef(RaceField.CandMiddle, "Candidate middle name", "middle", "middlename", "middleinitial", "candidatemiddle"),
        New FieldDef(RaceField.CandLast, "Candidate last name", "last", "lastname", "candidatelast", "candidatelastname"),
        New FieldDef(RaceField.CandSuffix, "Candidate suffix", "suffix", "namesuffix", "candidatesuffix"),
        New FieldDef(RaceField.CandParty, "Candidate party", "party", "candidateparty", "candparty", "partyabbr"),
        New FieldDef(RaceField.Incumbent, "Incumbent (Y/N)", "incumbent", "isincumbent", "inc"),
        New FieldDef(RaceField.CandId, "Candidate ID (in the results feed)", "candidateid", "candid", "candidatefeedid")
    }

    Private ReadOnly StateCodes As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase) From {
        {"alabama", "AL"}, {"alaska", "AK"}, {"arizona", "AZ"}, {"arkansas", "AR"}, {"california", "CA"},
        {"colorado", "CO"}, {"connecticut", "CT"}, {"delaware", "DE"}, {"district of columbia", "DC"},
        {"florida", "FL"}, {"georgia", "GA"}, {"hawaii", "HI"}, {"idaho", "ID"}, {"illinois", "IL"},
        {"indiana", "IN"}, {"iowa", "IA"}, {"kansas", "KS"}, {"kentucky", "KY"}, {"louisiana", "LA"},
        {"maine", "ME"}, {"maryland", "MD"}, {"massachusetts", "MA"}, {"michigan", "MI"}, {"minnesota", "MN"},
        {"mississippi", "MS"}, {"missouri", "MO"}, {"montana", "MT"}, {"nebraska", "NE"}, {"nevada", "NV"},
        {"new hampshire", "NH"}, {"new jersey", "NJ"}, {"new mexico", "NM"}, {"new york", "NY"},
        {"north carolina", "NC"}, {"north dakota", "ND"}, {"ohio", "OH"}, {"oklahoma", "OK"}, {"oregon", "OR"},
        {"pennsylvania", "PA"}, {"rhode island", "RI"}, {"south carolina", "SC"}, {"south dakota", "SD"},
        {"tennessee", "TN"}, {"texas", "TX"}, {"utah", "UT"}, {"vermont", "VT"}, {"virginia", "VA"},
        {"washington", "WA"}, {"west virginia", "WV"}, {"wisconsin", "WI"}, {"wyoming", "WY"}
    }

    Private Function Normalize(header As String) As String
        Dim sb As New StringBuilder
        For Each c In header.ToLowerInvariant()
            If Char.IsLetterOrDigit(c) Then sb.Append(c)
        Next
        Return sb.ToString()
    End Function

    ''' <summary>Guesses which CSV column feeds which field, using the header names.</summary>
    Public Function AutoMap(headers As String()) As Dictionary(Of RaceField, Integer)
        Dim map As New Dictionary(Of RaceField, Integer)
        Dim used As New HashSet(Of Integer)
        Dim norm = headers.Select(Function(h) Normalize(If(h, ""))).ToArray()

        For Each def In Fields
            For Each a In def.Aliases
                Dim idx = Array.FindIndex(norm, Function(n) n = a)
                If idx >= 0 AndAlso Not used.Contains(idx) Then
                    map(def.Field) = idx
                    used.Add(idx)
                    Exit For
                End If
            Next
        Next
        Return map
    End Function

    ''' <summary>Describes what the map still needs before races can be built (empty when it is usable).</summary>
    Public Function MissingRequired(map As Dictionary(Of RaceField, Integer)) As List(Of String)
        Dim missing As New List(Of String)
        If Not map.ContainsKey(RaceField.Office) Then missing.Add("Office / race title")
        If Not map.ContainsKey(RaceField.CandFullName) AndAlso Not map.ContainsKey(RaceField.CandLast) Then
            missing.Add("Candidate full name (or last name)")
        End If
        Return missing
    End Function

    Private Function Cell(row As String(), map As Dictionary(Of RaceField, Integer), field As RaceField) As String
        Dim idx As Integer
        If Not map.TryGetValue(field, idx) OrElse idx < 0 OrElse idx >= row.Length Then Return ""
        Return If(row(idx), "").Trim()
    End Function

    Private Function IsYes(value As String) As Boolean
        Select Case value.Trim().ToLowerInvariant()
            Case "y", "yes", "true", "1", "x", "inc", "i", "incumbent"
                Return True
            Case Else
                Return False
        End Select
    End Function

    Public Function NormalizeState(value As String) As String
        Dim code As String = Nothing
        If StateCodes.TryGetValue(value.Trim(), code) Then Return code
        Return value.Trim().ToUpperInvariant()
    End Function

    ''' <summary>Maps common wording to the levels Election Manager understands; returns "" if unrecognised.</summary>
    Public Function NormalizeLevel(value As String) As String
        Select Case value.Trim().ToLowerInvariant()
            Case "national", "federal", "nation", "us", "u.s.", "presidential"
                Return "NATIONAL"
            Case "state", "statewide"
                Return "STATE"
            Case "county"
                Return "COUNTY"
            Case "city", "municipal", "town", "local"
                Return "CITY"
            Case "district"
                Return "DISTRICT"
            Case Else
                Return ""
        End Select
    End Function

    Private Function InferLevel(r As Race) As String
        If r.City <> "" Then Return "CITY"
        If r.County <> "" Then Return "COUNTY"
        If r.District <> "" Then Return "DISTRICT"
        Return "STATE"
    End Function

    Private Function RaceKey(r As Race) As String
        Return String.Join("|", {r.Office, r.SeatName, r.SeatNumber, r.Party, r.RaceType, r.Level, r.State, r.County, r.City, r.District}.
                           Select(Function(s) s.ToLowerInvariant()))
    End Function

    ''' <param name="rows">Row 0 is the header row; the rest are candidate rows.</param>
    Public Function Build(rows As List(Of String()), map As Dictionary(Of RaceField, Integer)) As ImportResult
        Dim result As New ImportResult
        Dim byKey As New Dictionary(Of String, Race)(StringComparer.OrdinalIgnoreCase)
        Dim missing = MissingRequired(map)
        If missing.Count > 0 Then
            result.Messages.Add("Missing required column(s): " & String.Join(", ", missing))
            Return result
        End If

        For rowNum = 1 To rows.Count - 1
            Dim row = rows(rowNum)
            If row.All(Function(s) String.IsNullOrWhiteSpace(s)) Then Continue For

            Dim race As New Race With {
                .ID = Cell(row, map, RaceField.RaceId),
                .FeedId = Cell(row, map, RaceField.FeedId),
                .Office = Cell(row, map, RaceField.Office),
                .Level = Cell(row, map, RaceField.Level),
                .State = NormalizeState(Cell(row, map, RaceField.State)),
                .County = Cell(row, map, RaceField.County),
                .City = Cell(row, map, RaceField.City),
                .District = Cell(row, map, RaceField.District),
                .Party = Cell(row, map, RaceField.RaceParty),
                .RaceType = Cell(row, map, RaceField.RaceType).ToUpperInvariant(),
                .SeatName = Cell(row, map, RaceField.SeatName),
                .SeatNumber = Cell(row, map, RaceField.SeatNumber)
            }
            Dim winnersText = Cell(row, map, RaceField.Winners)
            Dim precinctsText = Cell(row, map, RaceField.PrecinctsTotal)

            If race.Office = "" Then
                result.Messages.Add($"Row {rowNum + 1}: no office/race title, row skipped.")
                Continue For
            End If

            Dim lvl = NormalizeLevel(race.Level)
            If race.Level <> "" AndAlso lvl = "" Then
                result.Messages.Add($"Row {rowNum + 1}: level ""{race.Level}"" is not NATIONAL/STATE/COUNTY/CITY/DISTRICT.")
                lvl = race.Level.ToUpperInvariant()
            End If
            race.Level = lvl

            ' Find the race this row belongs to (by Race ID when given, otherwise by identical race fields).
            Dim key = If(race.ID <> "", "ID|" & race.ID, "KEY|" & RaceKey(race))
            Dim target As Race = Nothing
            If Not byKey.TryGetValue(key, target) Then
                target = race
                byKey(key) = target
                result.Races.Add(target)
                target.Winners = 1
                If winnersText <> "" Then
                    Dim w As Integer
                    If Integer.TryParse(winnersText, w) AndAlso w >= 1 Then
                        target.Winners = w
                    Else
                        result.Messages.Add($"Row {rowNum + 1}: winners ""{winnersText}"" is not a positive number, using 1.")
                    End If
                End If
                If precinctsText <> "" Then
                    Dim p As Integer
                    If Integer.TryParse(precinctsText.Replace(",", ""), NumberStyles.Integer, CultureInfo.InvariantCulture, p) AndAlso p >= 0 Then
                        target.PrecinctsTotal = p
                    Else
                        result.Messages.Add($"Row {rowNum + 1}: precinct total ""{precinctsText}"" is not a number, using 0.")
                    End If
                End If
            Else
                MergeRaceFields(target, race, rowNum + 1)
            End If

            Dim cand As New Candidate With {
                .FullName = Cell(row, map, RaceField.CandFullName),
                .FirstName = Cell(row, map, RaceField.CandFirst),
                .MiddleName = Cell(row, map, RaceField.CandMiddle),
                .LastName = Cell(row, map, RaceField.CandLast),
                .Suffix = Cell(row, map, RaceField.CandSuffix),
                .Party = Cell(row, map, RaceField.CandParty),
                .Incumbent = IsYes(Cell(row, map, RaceField.Incumbent)),
                .FeedId = Cell(row, map, RaceField.CandId)
            }
            If cand.DisplayName <> "" Then target.Candidates.Add(cand)
        Next

        FinishRaces(result)
        Return result
    End Function

    ''' <summary>Later rows of the same race may fill in blanks; a different non-blank value is flagged.</summary>
    Private Sub MergeRaceFields(target As Race, other As Race, rowNumber As Integer)
        Dim pairs = {
            Tuple.Create("level", target.Level, other.Level),
            Tuple.Create("state", target.State, other.State),
            Tuple.Create("county", target.County, other.County),
            Tuple.Create("city", target.City, other.City),
            Tuple.Create("district", target.District, other.District),
            Tuple.Create("race party", target.Party, other.Party),
            Tuple.Create("race type", target.RaceType, other.RaceType),
            Tuple.Create("seat name", target.SeatName, other.SeatName),
            Tuple.Create("seat number", target.SeatNumber, other.SeatNumber),
            Tuple.Create("office", target.Office, other.Office)
        }
        For Each p In pairs
            If p.Item3 <> "" AndAlso p.Item2 <> "" AndAlso Not String.Equals(p.Item2, p.Item3, StringComparison.OrdinalIgnoreCase) Then
                target.Warnings.Add($"Row {rowNumber}: {p.Item1} ""{p.Item3}"" differs from ""{p.Item2}"" on the race's first row (first row used).")
            End If
        Next
        If target.FeedId = "" Then target.FeedId = other.FeedId
        If target.Level = "" Then target.Level = other.Level
        If target.State = "" Then target.State = other.State
        If target.County = "" Then target.County = other.County
        If target.City = "" Then target.City = other.City
        If target.District = "" Then target.District = other.District
        If target.Party = "" Then target.Party = other.Party
        If target.RaceType = "" Then target.RaceType = other.RaceType
        If target.SeatName = "" Then target.SeatName = other.SeatName
        If target.SeatNumber = "" Then target.SeatNumber = other.SeatNumber
    End Sub

    Private Sub FinishRaces(result As ImportResult)
        Dim taken As New HashSet(Of String)(result.Races.Where(Function(r) r.ID <> "").Select(Function(r) r.ID), StringComparer.OrdinalIgnoreCase)
        Dim seq = 0

        For Each r In result.Races
            If r.ID = "" Then
                Do
                    seq += 1
                Loop While taken.Contains("R" & seq.ToString("000"))
                r.ID = "R" & seq.ToString("000")
                taken.Add(r.ID)
            End If

            If r.Level = "" Then
                r.Level = InferLevel(r)
                r.Warnings.Add($"No level given; using {r.Level}.")
            End If

            If r.Candidates.Count = 0 Then r.Errors.Add("No candidates.")
            If r.Candidates.Count > 0 AndAlso r.Winners > r.Candidates.Count Then
                r.Warnings.Add($"{r.Winners} winners but only {r.Candidates.Count} candidate(s).")
            End If

            Dim seen As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
            For Each c In r.Candidates
                If Not seen.Add(c.DisplayName & "|" & c.Party) Then
                    r.Warnings.Add($"Candidate ""{c.DisplayName}"" appears more than once.")
                End If
            Next
        Next
    End Sub

End Module
