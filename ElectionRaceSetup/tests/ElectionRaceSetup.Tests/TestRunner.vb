Option Strict On
Option Infer On

Imports System.IO
Imports System.Net
Imports System.Net.Sockets
Imports System.Text
Imports System.Threading
Imports System.Xml

    Public Module TestRunner

        Private _failures As Integer
        Private _checks As Integer

        Private Sub Check(condition As Boolean, what As String)
            _checks += 1
            If condition Then
                Console.WriteLine("  ok    " & what)
            Else
                _failures += 1
                Console.WriteLine("  FAIL  " & what)
            End If
        End Sub

        Private Sub Eq(Of T)(actual As T, expected As T, what As String)
            Dim same = EqualityComparer(Of T).Default.Equals(actual, expected)
            Check(same, what & If(same, "", $"  (expected [{expected}], got [{actual}])"))
        End Sub

        Private Function SampleCsv() As String
            Return SampleFile("sample_races.csv")
        End Function

        Private Function SampleFile(fileName As String) As String
            Dim dir = AppContext.BaseDirectory
            For i = 0 To 8
                Dim p = Path.Combine(dir, "sample", fileName)
                If File.Exists(p) Then Return p
                dir = Path.GetFullPath(Path.Combine(dir, ".."))
            Next
            Throw New FileNotFoundException(fileName & " not found above " & AppContext.BaseDirectory)
        End Function

        Private Function Load(path As String) As ImportResult
            Dim rows = CsvParser.ReadFile(path)
            Return RaceImporter.Build(rows, RaceImporter.AutoMap(rows(0)))
        End Function

        Public Function Main() As Integer
            TestCsvParser()
            TestImporterSample()
            TestImporterNoIds()
            TestImporterProblems()
            TestWireXml()
            TestSocket()
            TestJsonAndTree()
            TestFeedAutoDetect()
            TestFeedMapping()
            TestMatching()
            TestEngine()
            TestResultsWire()
            TestFetcher()
            TestMontereyFeed()
            Console.WriteLine()
            Console.WriteLine($"{_checks - _failures}/{_checks} checks passed")
            Return If(_failures = 0, 0, 1)
        End Function

        Private Sub TestCsvParser()
            Console.WriteLine("CSV parser")
            Dim rows = CsvParser.Parse("a,b,c" & vbCrLf & """x, y"",""say """"hi"""""",z" & vbCrLf & """multi" & vbLf & "line"",2,3" & vbCrLf)
            Eq(rows.Count, 3, "three records")
            Eq(rows(1)(0), "x, y", "quoted comma kept")
            Eq(rows(1)(1), "say ""hi""", "doubled quote unescaped")
            Eq(rows(2)(0), "multi" & vbLf & "line", "newline inside quotes kept")

            Eq(CsvParser.Parse("a;b;c" & vbLf & "1;2;3")(1)(2), "3", "semicolon delimiter detected")
            Eq(CsvParser.Parse("a" & vbTab & "b" & vbLf & "1" & vbTab & "2")(1)(1), "2", "tab delimiter detected")
            Eq(CsvParser.Parse("a,b" & vbLf & "1,")(1).Length, 2, "trailing empty field kept")

            Dim bom As Byte() = New Byte() {&HEF, &HBB, &HBF}.Concat(Encoding.UTF8.GetBytes("Office,Candidate" & vbLf & "Mayor,José")).ToArray()
            Dim text = CsvParser.DecodeText(bom)
            Eq(text.StartsWith("Office"), True, "UTF-8 BOM stripped")
            Check(text.EndsWith("José"), "UTF-8 accents decoded")

            Dim ansi As Byte() = New Byte() {&H4A, &H6F, &H73, &HE9}   ' "Jos" + 0xE9, invalid UTF-8
            Check(CsvParser.DecodeText(ansi).StartsWith("Jos"), "non-UTF-8 file falls back instead of throwing")
        End Sub

        Private Sub TestImporterSample()
            Console.WriteLine("Importer - sample CSV")
            Dim res = Load(SampleCsv())
            Eq(res.Races.Count, 6, "6 races")
            Eq(res.Races.Sum(Function(r) r.Candidates.Count), 14, "14 candidates")
            Eq(res.Messages.Count, 0, "no import messages")
            Eq(res.Races.Where(Function(r) r.Errors.Count > 0).Count(), 0, "no race has errors")

            Dim gov = res.Races.Single(Function(r) r.ID = "CA-GOV")
            Eq(gov.Office, "Governor", "office")
            Eq(gov.Level, "STATE", "level")
            Eq(gov.PrecinctsTotal, 24000, "precincts total")
            Eq(gov.Candidates(1).Suffix, "Jr.", "suffix")
            Eq(gov.Candidates(0).Incumbent, True, "incumbent Y")
            Eq(gov.Candidates(1).Incumbent, False, "incumbent blank")

            Dim cc = res.Races.Single(Function(r) r.ID = "SLO-CC")
            Eq(cc.Winners, 2, "winners = 2")
            Eq(cc.City, "San Luis Obispo", "city")
            Eq(cc.Candidates(1).FullName, "Smith & Sons, Inc. (write-in)", "quoted name with comma/ampersand")

            Eq(res.Races.Single(Function(r) r.ID = "CA-PROP1").RaceType, "PROPOSITION", "type upper-cased")
            Eq(res.Races.Single(Function(r) r.ID = "CA-D24").District, "24", "district")
            Eq(res.Races.Single(Function(r) r.ID = "SLO-MAYOR").Candidates(1).FullName, "Pat ""Skip"" Delaney", "embedded quotes")
        End Sub

        Private Sub TestImporterNoIds()
            Console.WriteLine("Importer - no Race ID column, state names, level inference")
            Dim csv = "Race,State,County,Candidate,Party" & vbLf &
                      "Governor,California,,A One,D" & vbLf &
                      "Governor,California,,B Two,R" & vbLf &
                      "Sheriff,CA,Kern,C Three,R" & vbLf &
                      "Sheriff,CA,Kern,D Four,D" & vbLf &
                      ",CA,,Orphan row,R" & vbLf &
                      "Governor,California,,B Two,R" & vbLf
            Dim rows = CsvParser.Parse(csv)
            Dim map = RaceImporter.AutoMap(rows(0))
            Eq(RaceImporter.MissingRequired(map).Count, 0, "headers Race/Candidate/Party auto-mapped")
            Eq(map(RaceField.CandParty), 4, "Party maps to candidate party, not race party")
            Dim res = RaceImporter.Build(rows, map)
            Eq(res.Races.Count, 2, "rows grouped into 2 races")
            Eq(res.Races(0).ID, "R001", "generated ID 1")
            Eq(res.Races(1).ID, "R002", "generated ID 2")
            Eq(res.Races(0).State, "CA", "state name -> postal code")
            Eq(res.Races(0).Level, "STATE", "level inferred for statewide race")
            Eq(res.Races(1).Level, "COUNTY", "level inferred from county")
            Check(res.Races(0).Warnings.Any(Function(w) w.Contains("appears more than once")), "duplicate candidate flagged")
            Check(res.Messages.Any(Function(m) m.Contains("no office")), "row without office reported")
        End Sub

        Private Sub TestImporterProblems()
            Console.WriteLine("Importer - problems")
            Dim noCols = RaceImporter.AutoMap(CsvParser.Parse("foo,bar" & vbLf & "1,2")(0))
            Eq(RaceImporter.MissingRequired(noCols).Count, 2, "unknown headers -> office + candidate missing")

            Dim res = RaceImporter.Build(CsvParser.Parse("Office,Candidate,Winners" & vbLf & "Mayor,Solo,3" & vbLf & "Judge,,1"), RaceImporter.AutoMap({"Office", "Candidate", "Winners"}))
            Eq(res.Races.Count, 2, "race-only row still creates a race")
            Check(res.Races.Single(Function(r) r.Office = "Judge").Errors.Count = 1, "race with no candidates is an error")
            Check(res.Races.Single(Function(r) r.Office = "Mayor").Warnings.Any(Function(w) w.Contains("3 winners")), "winners > candidates warned")

            Dim conflict = RaceImporter.Build(CsvParser.Parse("Race ID,Office,Candidate" & vbLf & "X1,Mayor,A" & vbLf & "X1,Governor,B"), RaceImporter.AutoMap({"Race ID", "Office", "Candidate"}))
            Eq(conflict.Races.Count, 1, "same Race ID = one race")
            Check(conflict.Races(0).Warnings.Any(Function(w) w.Contains("differs")), "conflicting office on same Race ID flagged")
        End Sub

        Private Sub TestWireXml()
            Console.WriteLine("Wire XML")
            Dim r As New Race With {.ID = "X-1", .Office = "Mayor & ""Co"" <1>", .Level = "CITY", .State = "CA", .City = "Town", .Winners = 1, .PrecinctsTotal = 12,
                                    .SeatName = "Seat", .SeatNumber = "5"}
            r.Candidates.Add(New Candidate With {.FullName = "Ann O'Neil", .Party = "D", .Incumbent = True})
            r.Candidates.Add(New Candidate With {.FirstName = "Bob", .LastName = "Ray" & vbLf & "Jr", .Party = "R"})

            Dim xml = WireBuilder.BuildRaceXml(r, "11/3/2026", "CSV", True)
            Dim doc As New XmlDocument
            doc.LoadXml(xml)
            Eq(doc.SelectSingleNode("/RACE/ELECTION").InnerText, "11/3/2026", "election date")
            Eq(doc.SelectSingleNode("/RACE/SOURCE").InnerText, "CSV", "source")
            Eq(doc.SelectSingleNode("/RACE/ID").InnerText, "X-1", "id")
            Eq(doc.SelectSingleNode("/RACE/TEST").InnerText, "YES", "test flag")
            Eq(doc.SelectSingleNode("/RACE/OFFICE").InnerText, "Mayor & ""Co"" <1>", "special characters survive an XML round trip")
            Eq(doc.SelectSingleNode("/RACE/PRECINCTS_REPORTING").InnerText, "0", "precincts reporting 0")
            Eq(doc.SelectSingleNode("/RACE/PRECINCTS_TOTAL").InnerText, "12", "precincts total")
            Eq(doc.SelectNodes("/RACE/CANDIDATES/CANDIDATE").Count, 2, "two candidates")
            Eq(doc.SelectSingleNode("/RACE/CANDIDATES/CANDIDATE[1]/FULLNAME").InnerText, "Ann O'Neil", "full name")
            Eq(doc.SelectSingleNode("/RACE/CANDIDATES/CANDIDATE[1]/INCUMBENT").InnerText, "YES", "incumbent tag")
            Eq(doc.SelectSingleNode("/RACE/CANDIDATES/CANDIDATE[1]/VOTES").InnerText, "0", "votes 0")
            Check(doc.SelectSingleNode("/RACE/CANDIDATES/CANDIDATE[2]/INCUMBENT") Is Nothing, "no incumbent tag when not incumbent")
            Eq(doc.SelectSingleNode("/RACE/CANDIDATES/CANDIDATE[2]/LAST").InnerText, "Ray Jr", "newline in a name flattened")
            Check(doc.SelectSingleNode("/RACE/SUBRACES") IsNot Nothing, "empty SUBRACES present")
            Eq(WireBuilder.BuildRaceXml(r, "11/3/2026", "CSV", False).Contains("<TEST>NO</TEST>"), True, "test flag NO")

            Dim msg = WireBuilder.BuildMessage(xml, New Date(2026, 11, 3, 18, 5, 0))
            Check(msg.StartsWith("$(ELECTION:CLIENT:UPDATE)="), "message starts with the command")
            Check(msg.Contains("=WIREDATA=<RACE>"), "WIREDATA marker followed by RACE")
            Check(msg.EndsWith("</RACE>" & vbCrLf & vbCrLf), "message ends with CRLF CRLF like the sample")
        End Sub

        Private Sub TestSocket()
            Console.WriteLine("MosClient over a real TCP socket")
            Dim listener As New TcpListener(IPAddress.Loopback, 0)
            listener.Start()
            Dim port = CType(listener.LocalEndpoint, IPEndPoint).Port

            Dim received As New StringBuilder
            Dim accepted = 0
            Dim serverCts As New CancellationTokenSource
            Dim serverTask = Task.Run(Sub()
                                          While Not serverCts.IsCancellationRequested
                                              Dim c As TcpClient
                                              Try
                                                  c = listener.AcceptTcpClient()
                                              Catch
                                                  Exit While
                                              End Try
                                              Interlocked.Increment(accepted)
                                              Dim buf(8191) As Byte
                                              Dim s = c.GetStream()
                                              Dim got = 0
                                              Try
                                                  Do
                                                      Dim n = s.Read(buf, 0, buf.Length)
                                                      If n <= 0 Then Exit Do
                                                      SyncLock received
                                                          received.Append(Encoding.UTF8.GetString(buf, 0, n))
                                                      End SyncLock
                                                      ' First connection: hang up after the first chunk to force a reconnect.
                                                      If accepted = 1 Then
                                                          got += 1
                                                          If got >= 1 Then Exit Do
                                                      End If
                                                  Loop
                                              Catch
                                              End Try
                                              c.Close()
                                          End While
                                      End Sub)

            Dim res = Load(SampleCsv())
            Dim sentTags As New List(Of String)
            Dim client As New MosClient With {.SendDelayMs = 20, .HeartbeatSeconds = 1}
            AddHandler client.MessageSent, Sub(tag)
                                               SyncLock sentTags
                                                   sentTags.Add(Convert.ToString(tag))
                                               End SyncLock
                                           End Sub
            client.Start("127.0.0.1", port)

            For Each r In res.Races
                client.Enqueue(WireBuilder.BuildMessage(WireBuilder.BuildRaceXml(r, "11/3/2026", "CSV", False), Date.Now), r.ID)
            Next

            Dim deadline = DateTime.UtcNow.AddSeconds(15)
            While DateTime.UtcNow < deadline AndAlso client.PendingCount > 0
                Thread.Sleep(50)
            End While
            Thread.Sleep(1500)  ' allow a heartbeat
            Eq(client.PendingCount, 0, "queue drained")
            Eq(sentTags.Count, 6, "6 sent events")
            Check(accepted >= 2, "client reconnected after the server dropped the first connection (connections: " & accepted & ")")

            Dim all As String
            SyncLock received
                all = received.ToString()
            End SyncLock
            client.Dispose()
            serverCts.Cancel()
            listener.Stop()

            ' The dropped first connection may lose the message that was in flight, so allow a small number
            ' of lines lost to the kill and require the rest to be intact and well-formed.
            Dim msgs = all.Split({"$(ELECTION:CLIENT:UPDATE)="}, StringSplitOptions.RemoveEmptyEntries)
            Check(msgs.Length >= 5, $"server received {msgs.Length} wire messages")
            Dim wellFormed = 0
            For Each m In msgs
                Dim start = m.IndexOf("=WIREDATA=", StringComparison.Ordinal)
                Dim stop_ = m.LastIndexOf("</RACE>", StringComparison.Ordinal)
                If start >= 0 AndAlso stop_ > start Then
                    Try
                        Dim d As New XmlDocument
                        d.LoadXml(m.Substring(start + "=WIREDATA=".Length, stop_ + "</RACE>".Length - start - "=WIREDATA=".Length))
                        wellFormed += 1
                    Catch
                    End Try
                End If
            Next
            Eq(wellFormed, msgs.Length, "every received message carries well-formed RACE xml")
            Check(all.Contains("CA-SEN") AndAlso all.Contains("SLO-CC") AndAlso all.Contains("CA-D24"), "later races arrived after the reconnect")
        End Sub

        ' ---------------------------------------------------------------------------------------------
        ' Results feeds

        Private Function CountOf(Of T)(source As IEnumerable(Of T), predicate As Func(Of T, Boolean)) As Integer
            Return source.Where(predicate).Count()
        End Function

        Private Function SetupRaces() As List(Of Race)
            Return Load(SampleCsv()).Races
        End Function

        Private Function ReadSample(fileName As String) As List(Of FeedContest)
            Return FeedReader.Read(File.ReadAllText(SampleFile(fileName)))
        End Function

        Private Function VotesIn(item As ResultItem) As Long()
            Return item.Outgoing.Candidates.Select(Function(c) c.Votes).ToArray()
        End Function

        Private Sub TestJsonAndTree()
            Console.WriteLine("Feed tree (JSON and XML)")
            Dim j = FeedNode.ParseAny("{""a"": {""b"": [1, 2, {""c"": ""xé\n""}], ""t"": true, ""n"": null, ""big"": -1.5e3}}")
            Eq(j.SelectNodes("a/b").Count, 3, "JSON array becomes repeated nodes")
            Eq(j.Value("a/b"), "1", "first array item")
            Eq(j.Value("a/b/c"), "xé", "a path runs through every array item and finds the object inside")
            Eq(j.SelectNodes("a/b/c").Count, 1, "nested object inside array")
            Eq(j.SelectNodes("a/b/c")(0).Text, "xé" & vbLf, "unicode and newline escapes decoded")
            Eq(j.Value("a/t"), "true", "boolean literal")
            Eq(j.Value("a/n"), "", "null is blank")
            Eq(j.Value("a/big"), "-1.5e3", "number text kept as written")
            Eq(j.SelectNodes("**/c").Count, 1, "** reaches any depth")

            Dim esc = FeedNode.ParseAny("{""s"": ""café \""q\"" \\ \/ tab\t""}")
            Eq(esc.Value("s"), "café ""q"" \ / tab", "string escapes decoded")

            Dim arr = FeedNode.ParseAny("[{""id"":1},{""id"":2}]")
            Eq(arr.SelectNodes("item").Count, 2, "root array items are named item")

            For Each bad In {"{""a"": }", "{""a"": 1,}", "[1 2]", "{""a"": tru}", "{""a"": ""x}", "plain text", "{""a"": 1} extra"}
                Dim threw = False
                Try
                    FeedNode.ParseAny(bad)
                Catch ex As FormatException
                    threw = True
                End Try
                Check(threw, "malformed input rejected: " & bad)
            Next

            Dim x = FeedNode.ParseAny("<?xml version=""1.0""?><!DOCTYPE r [<!ENTITY e ""boom"">]><r id=""7""><a k=""v"">hi</a><a>there</a><b/></r>")
            Eq(x.Value("@id"), "7", "XML attribute on the current node")
            Eq(x.SelectNodes("a").Count, 2, "repeated XML elements")
            Eq(x.Value("a/@k"), "v", "attribute on a child path")
            Eq(x.Value("a"), "hi", "element text")
            Eq(FeedNode.Norm("Vote_Count "), "votecount", "names compare ignoring case and punctuation")
        End Sub

        Private Sub TestFeedAutoDetect()
            Console.WriteLine("Feed auto-detect")
            Dim js = ReadSample("sample_feed.json")
            Eq(js.Count, 7, "JSON: 7 contests found")
            Dim gov = js.Single(Function(c) c.FeedId = "9001")
            Eq(gov.Office, "Governor", "JSON: office from ""title""")
            Eq(gov.State, "CA", "JSON: state")
            Eq(gov.Level, "STATE", "JSON: level ""statewide"" understood")
            Eq(gov.PrecinctsReporting.Value, 12000, "JSON: precincts reporting")
            Eq(gov.PrecinctsTotal.Value, 24000, "JSON: precincts total")
            Eq(gov.Candidates.Count, 3, "JSON: three candidates")
            Eq(gov.Candidates(0).FullName, "Jane Alvarez", "JSON: name from ""ballotName""")
            Eq(gov.Candidates(0).Votes.Value, 1234567L, "JSON: votes from ""voteCount""")
            Eq(gov.Candidates(0).Incumbent, True, "JSON: incumbent true")
            Eq(js.Single(Function(c) c.FeedId = "9003").PrecinctsTotal.Value, 24000, "JSON: ""12000/24000"" gives both numbers")
            Eq(js.Single(Function(c) c.FeedId = "9006").Candidates(0).Winner, True, "JSON: winner flag")
            Eq(js.Single(Function(c) c.FeedId = "9004").City, "San Luis Obispo", "JSON: city")

            Dim xm = ReadSample("sample_feed.xml")
            Eq(xm.Count, 3, "XML: 3 contests found")
            Dim xgov = xm.Single(Function(c) c.FeedId = "X-GOV")
            Eq(xgov.Office, "Governor", "XML: office from the name attribute")
            Eq(xgov.Candidates(0).FullName, "Alvarez, Jane", "XML: name element")
            Eq(xgov.Candidates(0).Votes.Value, 1234567L, "XML: votes with thousands separators")
            Eq(xgov.PrecinctsReporting.Value, 12000, "XML: precincts from attributes")
            Eq(xm.Single(Function(c) c.FeedId = "X-D24").District, "24", "XML: district attribute")
            Eq(xm.Single(Function(c) c.FeedId = "X-D24").Candidates(0).Winner, True, "XML: winner X")

            Dim bad = FeedReader.Read("{""races"":[{""title"":""Mayor"",""candidates"":[{""name"":""A"",""votes"":""n/a""},{""name"":""B"",""votes"":-4},{""name"":""C"",""votes"":""""}]}]}")
            Eq(bad.Count, 1, "odd votes: contest still found")
            Check(Not bad(0).Candidates(0).Votes.HasValue, "text votes become unreadable, not 0")
            Check(Not bad(0).Candidates(1).Votes.HasValue, "negative votes become unreadable")
            Eq(bad(0).Candidates(2).Votes.Value, 0L, "blank votes count as 0")
            Eq(bad(0).Warnings.Count, 2, "a warning for each unreadable vote count")

            Eq(FeedReader.Read("{""hello"": ""world""}").Count, 0, "a feed with no contests gives an empty list")
        End Sub

        Private Sub TestFeedMapping()
            Console.WriteLine("Feed mapping file")
            Dim prof = FeedProfile.Parse(File.ReadAllText(SampleFile("sample_feed_mapping.json")))
            Dim viaProfile = FeedReader.Read(File.ReadAllText(SampleFile("sample_feed.xml")), prof)
            Dim viaAuto = ReadSample("sample_feed.xml")
            Eq(viaProfile.Count, 3, "mapping file: 3 contests")
            Eq(String.Join(",", viaProfile.Select(Function(c) c.FeedId)), String.Join(",", viaAuto.Select(Function(c) c.FeedId)), "same IDs as auto-detect")
            Eq(viaProfile(0).Candidates(1).Votes.Value, viaAuto(0).Candidates(1).Votes.Value, "same votes as auto-detect")
            Eq(viaProfile(2).District, "24", "mapped district attribute")

            Dim threw = False
            Try
                FeedProfile.Parse("{""contests"": ""x""}")
            Catch ex As FormatException
                threw = True
            End Try
            Check(threw, "mapping file without ""candidates"" is rejected")

            Dim custom = FeedProfile.Parse("{""contests"":""data/item"",""candidates"":""rows/row"",""fields"":{""office"":""label""},""candidateFields"":{""name"":""who"",""votes"":""n""}}")
            Dim r = FeedReader.Read("{""data"":{""item"":[{""label"":""Sheriff"",""rows"":{""row"":[{""who"":""A B"",""n"":5},{""who"":""C D"",""n"":9}]}}]}}", custom)
            Eq(r.Count, 1, "custom paths: one contest")
            Eq(r(0).Office, "Sheriff", "custom office path")
            Eq(r(0).Candidates.Count, 2, "custom candidate path")
            Eq(r(0).Candidates(1).Votes.Value, 9L, "custom votes path")
        End Sub

        Private Sub TestMatching()
            Console.WriteLine("Pairing races and candidates")
            Check(RaceMatcher.Tokens("U.S. Senate").SetEquals(RaceMatcher.Tokens("United States Senator")), "U.S. Senate = United States Senator")
            Check(RaceMatcher.Tokens("24th District").Contains("24"), "ordinals reduced to numbers")

            Dim races = SetupRaces()
            Dim d12 As New FeedContest With {.Office = "U.S. House", .District = "12", .State = "CA"}
            Eq(RaceMatcher.Score(d12, races.Single(Function(r) r.ID = "CA-D24")), 0.0, "different district numbers never match")
            Dim d24 As New FeedContest With {.Office = "U.S. House", .District = "24", .State = "CA"}
            Check(RaceMatcher.Score(d24, races.Single(Function(r) r.ID = "CA-D24")) >= 0.99, "same district scores about 1")
            Dim tx As New FeedContest With {.Office = "Governor", .State = "TX"}
            Eq(RaceMatcher.Score(tx, races.Single(Function(r) r.ID = "CA-GOV")), 0.0, "different state never matches")
            Dim rParty As New Race With {.Office = "Governor", .Party = "R", .State = "CA"}
            Dim dParty As New FeedContest With {.Office = "Governor", .Party = "DEM", .State = "CA"}
            Eq(RaceMatcher.Score(dParty, rParty), 0.0, "different primary party never matches")
            Check(RaceMatcher.PartiesMatch("DEM", "D") AndAlso RaceMatcher.PartiesMatch("REP", "R") AndAlso Not RaceMatcher.PartiesMatch("DEM", "R"), "DEM = D, REP = R")

            Dim m = RaceMatcher.FindRace(New FeedContest With {.Office = "Proposition 2", .State = "CA"}, races, Nothing)
            Check(m.Race Is Nothing, "Proposition 2 does not match Proposition 1")
            Dim byId = races.Single(Function(r) r.ID = "CA-GOV")
            byId.FeedId = "ZZ-1"
            Dim m2 = RaceMatcher.FindRace(New FeedContest With {.FeedId = "zz-1", .Office = "Something else entirely"}, races, Nothing)
            Eq(m2.Race.ID, "CA-GOV", "Feed ID column wins over the text")
            byId.FeedId = ""

            Dim clearCut = New List(Of Race) From {
                New Race With {.ID = "A", .Office = "School Board"},
                New Race With {.ID = "B", .Office = "School Board Trustee"}
            }
            Eq(RaceMatcher.FindRace(New FeedContest With {.Office = "School Board"}, clearCut, Nothing).Race.ID, "A", "the clearly better of two similar races wins")

            Dim ambiguous = New List(Of Race) From {
                New Race With {.ID = "S1", .Office = "School Board", .SeatNumber = "1"},
                New Race With {.ID = "S2", .Office = "School Board", .SeatNumber = "2"}
            }
            Dim amb = RaceMatcher.FindRace(New FeedContest With {.Office = "School Board"}, ambiguous, Nothing)
            Check(amb.Race Is Nothing AndAlso amb.Suggestion <> "", "two equally good races: no automatic pick, a suggestion is given")

            Dim dog As New FeedContest With {.Office = "Dog Catcher"}
            Dim cw As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase) From {{dog.Key, "CA-PROP1"}}
            Eq(RaceMatcher.FindRace(dog, races, cw).Race.ID, "CA-PROP1", "saved hand pairing is used")

            ' candidates
            Dim setup As New List(Of Candidate) From {
                New Candidate With {.FirstName = "Jane", .LastName = "Smith", .Party = "D"},
                New Candidate With {.FirstName = "John", .MiddleName = "A.", .LastName = "Smith", .Party = "R"},
                New Candidate With {.FullName = "María Ñúñez"},
                New Candidate With {.FullName = "Pat ""Skip"" O'Hara Jr."}
            }
            Dim feed As New List(Of FeedCandidate) From {
                New FeedCandidate With {.FullName = "Smith, Jane"},
                New FeedCandidate With {.FullName = "John Smith III"},
                New FeedCandidate With {.FullName = "Maria Nunez"},
                New FeedCandidate With {.FullName = "Patrick O'Hara"},
                New FeedCandidate With {.FullName = "Nobody Here"}
            }
            Dim map = RaceMatcher.MatchCandidates(feed, setup)
            Eq(map(0), 0, """Smith, Jane"" matches Jane Smith")
            Eq(map(1), 1, """John Smith III"" matches John A. Smith")
            Eq(map(2), 2, "accents ignored")
            Eq(map(3), 3, "Patrick matches Pat (prefix of the first name); nickname in quotes ignored")
            Eq(map(4), -1, "unknown name stays unmatched")

            Dim twoSmiths As New List(Of Candidate) From {
                New Candidate With {.FirstName = "Jane", .LastName = "Smith"}, New Candidate With {.FirstName = "John", .LastName = "Smith"}}
            Eq(RaceMatcher.MatchCandidates({New FeedCandidate With {.FullName = "Smith"}}, twoSmiths)(0), -1, "last name alone is not guessed between two Smiths")
            Eq(RaceMatcher.MatchCandidates({New FeedCandidate With {.FullName = "J. Smith", .Party = "R"}}, New List(Of Candidate) From {
                New Candidate With {.FirstName = "Jane", .LastName = "Smith", .Party = "D"}, New Candidate With {.FirstName = "John", .LastName = "Smith", .Party = "R"}})(0), 1, "party breaks a tie between equal matches")
            Dim nick = RaceMatcher.MatchCandidates({New FeedCandidate With {.FullName = "Mike Smith"}, New FeedCandidate With {.FullName = "Bob Jones"}},
                                                   New List(Of Candidate) From {New Candidate With {.FullName = "Michael Smith"}, New Candidate With {.FullName = "Robert Jones"}})
            Eq(nick(0), 0, "Mike matches Michael")
            Eq(nick(1), 1, "Bob matches Robert")
            Eq(RaceMatcher.MatchCandidates({New FeedCandidate With {.FullName = "Michael Smith"}}, New List(Of Candidate) From {New Candidate With {.FullName = "Mark Smith"}})(0), -1, "Michael is never matched to Mark just for sharing a last name and initial")
            Check(RaceMatcher.IsWriteIn("Write-in") AndAlso RaceMatcher.IsWriteIn("Scattering") AndAlso Not RaceMatcher.IsWriteIn("Jane Doe"), "write-in lines recognised")
        End Sub

        Private Sub TestEngine()
            Console.WriteLine("Results engine")
            Dim races = SetupRaces()
            Dim engine As New ResultsEngine
            Dim items = engine.Evaluate(ReadSample("sample_feed.json"), races, "11/3/2026", "CSV", False)

            Eq(CountOf(items, Function(i) i.Status = ResultStatus.Ready), 6, "JSON feed: 6 races ready")
            Eq(CountOf(items, Function(i) i.Status = ResultStatus.Unmatched), 1, "JSON feed: the dog catcher is unmatched")
            Eq(items.Single(Function(i) i.Status = ResultStatus.Unmatched).Contest.Office, "County Dog Catcher", "the unmatched one is the dog catcher")

            Dim gov = items.Single(Function(i) i.Contest.FeedId = "9001")
            Eq(gov.Race.ID, "CA-GOV", "Governor paired with CA-GOV")
            Eq(String.Join(",", VotesIn(gov)), "1234567,1200345,55012", "votes land on the right setup candidates")
            Eq(gov.Outgoing.Candidates(0).FirstName, "Jane", "outgoing names are the setup names")
            Eq(gov.Outgoing.PrecinctsReporting, 12000, "precincts reporting sent")
            Check(gov.Xml.Contains("<ID>CA-GOV</ID>") AndAlso gov.Xml.Contains("<SOURCE>CSV</SOURCE>") AndAlso gov.Xml.Contains("<TEST>NO</TEST>"), "id, source and test flag come from the setup race and window")

            Dim sen = items.Single(Function(i) i.Contest.FeedId = "9002")
            Eq(sen.Race.ID, "CA-SEN", "U.S. Senator paired with U.S. Senate")
            Check(sen.Notes.Any(Function(n) n.Contains("Write-in")), "write-in line ignored with a note")
            Eq(String.Join(",", VotesIn(sen)), "1301220,1188004", "senate votes")

            Dim cc = items.Single(Function(i) i.Contest.FeedId = "9005")
            Eq(cc.Race.ID, "SLO-CC", "City Council paired")
            Eq(cc.MatchedCandidates, 3, "all three council candidates matched, including the odd name")
            Eq(items.Single(Function(i) i.Contest.FeedId = "9004").Race.ID, "SLO-MAYOR", "mayor race paired despite the longer title")
            Eq(items.Single(Function(i) i.Contest.FeedId = "9006").Race.ID, "CA-D24", "U.S. Representative, District 24 paired")
            Check(items.Single(Function(i) i.Contest.FeedId = "9006").Xml.Contains("<WINNER>YES</WINNER>"), "winner flag reaches the XML")

            ' sending marks races, and nothing changes -> nothing to send
            For Each i In items.Where(Function(x) x.Status = ResultStatus.Ready).ToList()
                engine.MarkSent(i)
            Next
            Dim again = engine.Evaluate(ReadSample("sample_feed.json"), races, "11/3/2026", "CSV", False)
            Eq(CountOf(again, Function(i) i.Status = ResultStatus.Unchanged), 6, "re-reading the same feed: 6 unchanged")
            Eq(CountOf(again, Function(i) i.Status = ResultStatus.Ready), 0, "nothing new to send")

            ' votes go up -> ready; go down -> held
            Dim up = ReadSample("sample_feed.json")
            up.Single(Function(c) c.FeedId = "9001").Candidates(0).Votes = 1300000
            Dim afterUp = engine.Evaluate(up, races, "11/3/2026", "CSV", False)
            Eq(afterUp.Single(Function(i) i.Contest.FeedId = "9001").Status, ResultStatus.Ready, "more votes: ready to send")
            Eq(CountOf(afterUp, Function(i) i.Status = ResultStatus.Unchanged), 5, "other races stay unchanged")

            Dim down = ReadSample("sample_feed.json")
            down.Single(Function(c) c.FeedId = "9001").Candidates(1).Votes = 1000
            Dim afterDown = engine.Evaluate(down, races, "11/3/2026", "CSV", False)
            Dim heldGov = afterDown.Single(Function(i) i.Contest.FeedId = "9001")
            Eq(heldGov.Status, ResultStatus.Held, "fewer votes than last sent: held")
            Check(heldGov.Notes.Any(Function(n) n.Contains("went down")), "the note says which candidate went down")

            ' test flag change makes everything different again
            Eq(engine.Evaluate(ReadSample("sample_feed.json"), races, "11/3/2026", "CSV", True).Where(Function(i) i.Status = ResultStatus.Ready).Count(), 6, "changing TEST/LIVE re-queues every race")

            ' unknown candidate -> held
            Dim extra = ReadSample("sample_feed.json")
            extra.Single(Function(c) c.FeedId = "9002").Candidates.Add(New FeedCandidate With {.FullName = "Zed Zelenko", .Votes = 5})
            Dim heldSen = engine.Evaluate(extra, races, "11/3/2026", "CSV", False).Single(Function(i) i.Contest.FeedId = "9002")
            Eq(heldSen.Status, ResultStatus.Held, "a feed candidate not in the setup race: held")
            Check(heldSen.Notes.Any(Function(n) n.Contains("Zed Zelenko")), "the note names the candidate")

            ' bad numbers -> held
            Dim bad = ReadSample("sample_feed.json")
            bad.Single(Function(c) c.FeedId = "9004").PrecinctsReporting = 99
            Eq(engine.Evaluate(bad, races, "11/3/2026", "CSV", False).Single(Function(i) i.Contest.FeedId = "9004").Status, ResultStatus.Held, "precincts reporting above total: held")
            Dim unreadable = ReadSample("sample_feed.json")
            unreadable.Single(Function(c) c.FeedId = "9001").Candidates(2).Votes = Nothing
            Eq(engine.Evaluate(unreadable, races, "11/3/2026", "CSV", False).Single(Function(i) i.Contest.FeedId = "9001").Status, ResultStatus.Held, "unreadable votes: held")

            ' two feed contests for one race -> second held
            Dim dup = ReadSample("sample_feed.json")
            dup.Add(New FeedContest With {.FeedId = "9001b", .Office = "Governor", .State = "CA"})
            Dim dupItems = New ResultsEngine().Evaluate(dup, races, "11/3/2026", "CSV", False)
            Check(dupItems.Where(Function(i) i.Notes.Any(Function(n) n.Contains("also paired"))).Count() = 1, "a race claimed twice: the second claim is flagged")

            ' manual pairing
            Dim manual As New ResultsEngine
            Dim dogContest = ReadSample("sample_feed.json").Single(Function(c) c.FeedId = "9099")
            manual.Crosswalk(dogContest.Key) = "SLO-CC"
            Dim paired = manual.Evaluate({dogContest}, races, "11/3/2026", "CSV", False)(0)
            Eq(paired.Race.ID, "SLO-CC", "a hand pairing is honoured")
            Eq(paired.Status, ResultStatus.Held, "...but its candidates do not match, so it is held, not sent")

            ' XML feed through the Feed ID column
            Dim xmlRaces = SetupRaces()
            xmlRaces.Single(Function(r) r.ID = "CA-GOV").FeedId = "X-GOV"
            Dim xi = New ResultsEngine().Evaluate(ReadSample("sample_feed.xml"), xmlRaces, "11/3/2026", "CSV", True)
            Eq(CountOf(xi, Function(i) i.Status = ResultStatus.Ready), 3, "XML feed: 3 ready")
            Eq(xi.Single(Function(i) i.Contest.FeedId = "X-GOV").How, "Feed ID column", "XML governor paired through the Feed ID column")
            Eq(String.Join(",", VotesIn(xi.Single(Function(i) i.Contest.FeedId = "X-GOV"))), "1234567,1200345,55012", "XML votes with ""Last, First"" names and a short first name")
            Eq(String.Join(",", VotesIn(xi.Single(Function(i) i.Contest.FeedId = "X-D24"))), "131000,101000", "XML house votes")

            ' saved pairings
            Dim tmp = Path.Combine(Path.GetTempPath(), "crosswalk-" & Guid.NewGuid().ToString("N") & ".ini")
            CrosswalkStore.Save(tmp, New Dictionary(Of String, String) From {{"a|b", "R1"}, {"9001", "CA-GOV"}})
            Dim back = CrosswalkStore.Load(tmp)
            Eq(back("a|b"), "R1", "saved pairings round-trip")
            Eq(back("9001"), "CA-GOV", "second pairing round-trips")
            File.Delete(tmp)
        End Sub

        Private Sub TestResultsWire()
            Console.WriteLine("Wire XML with results")
            Dim r As New Race With {.ID = "R1", .Office = "Mayor", .Level = "CITY", .State = "CA", .Winners = 1, .PrecinctsTotal = 40, .PrecinctsReporting = 12}
            r.Candidates.Add(New Candidate With {.FullName = "A One", .Votes = 1234, .Winner = True, .Incumbent = True})
            r.Candidates.Add(New Candidate With {.FullName = "B Two", .Votes = 999})
            Dim xml = WireBuilder.BuildRaceXml(r, "11/3/2026", "CSV", False)
            Dim doc As New Xml.XmlDocument
            doc.LoadXml(xml)
            Eq(doc.SelectSingleNode("/RACE/PRECINCTS_REPORTING").InnerText, "12", "precincts reporting")
            Eq(doc.SelectSingleNode("/RACE/CANDIDATES/CANDIDATE[1]/VOTES").InnerText, "1234", "votes written")
            Eq(doc.SelectSingleNode("/RACE/CANDIDATES/CANDIDATE[1]/WINNER").InnerText, "YES", "winner written")
            Check(doc.SelectSingleNode("/RACE/CANDIDATES/CANDIDATE[2]/WINNER") Is Nothing, "no winner tag for the others")
            Dim c1 = xml.IndexOf("<WINNER>", StringComparison.Ordinal)
            Check(c1 > xml.IndexOf("<PARTY></PARTY>", xml.IndexOf("<CANDIDATE>", StringComparison.Ordinal), StringComparison.Ordinal) AndAlso
                  c1 < xml.IndexOf("<INCUMBENT>", StringComparison.Ordinal) AndAlso
                  xml.IndexOf("<INCUMBENT>", StringComparison.Ordinal) < xml.IndexOf("<VOTES>", StringComparison.Ordinal), "tag order matches the sample: PARTY, WINNER, INCUMBENT, VOTES")
        End Sub

        Private Sub TestFetcher()
            Console.WriteLine("Feed fetching")
            Dim jsonPath = SampleFile("sample_feed.json")
            Eq(FeedFetcher.Load(jsonPath, "", "").Count, 7, "load from a file")
            Eq(FeedFetcher.Load(SampleFile("sample_feed.xml"), "", SampleFile("sample_feed_mapping.json")).Count, 3, "load a file with a mapping file")

            Dim missing = False
            Try
                FeedFetcher.FetchText(Path.Combine(Path.GetTempPath(), "no-such-feed-" & Guid.NewGuid().ToString("N") & ".json"), "")
            Catch ex As IOException
                missing = True
            End Try
            Check(missing, "a missing file raises an error")

            Dim probe As New TcpListener(IPAddress.Loopback, 0)
            probe.Start()
            Dim port = CType(probe.LocalEndpoint, IPEndPoint).Port
            probe.Stop()

            Dim http As New HttpListener
            http.Prefixes.Add($"http://127.0.0.1:{port}/")
            http.Start()
            Dim seenKey As String = ""
            Dim body = File.ReadAllText(jsonPath)
            Dim serverTask = Task.Run(Sub()
                                          For i = 1 To 2
                                              Dim ctx = http.GetContext()
                                              If ctx.Request.Url.AbsolutePath = "/fail" Then
                                                  ctx.Response.StatusCode = 500
                                              Else
                                                  seenKey = ctx.Request.Headers("X-Api-Key")
                                                  Dim bytes = Encoding.UTF8.GetBytes(body)
                                                  ctx.Response.ContentType = "application/json"
                                                  ctx.Response.OutputStream.Write(bytes, 0, bytes.Length)
                                              End If
                                              ctx.Response.Close()
                                          Next
                                      End Sub)

            Eq(FeedFetcher.Load($"http://127.0.0.1:{port}/feed", "X-Api-Key: abc123; Accept-Language: en", "").Count, 7, "load from a URL")
            Eq(seenKey, "abc123", "custom header sent")
            Dim failed = False
            Try
                FeedFetcher.FetchText($"http://127.0.0.1:{port}/fail", "")
            Catch ex As WebException
                failed = True
            End Try
            Check(failed, "an HTTP 500 raises an error")
            serverTask.Wait(3000)
            http.Stop()
        End Sub

        Private Sub TestMontereyFeed()
            Console.WriteLine("Multi-county feed (Monterey + Santa Cruz + San Benito)")
            Dim races = Load(SampleFile("monterey_setup_races.csv")).Races
            Dim contests = FeedReader.Read(File.ReadAllText(SampleFile("monterey_feed_excerpt.xml")))
            Eq(contests.Count, 66, "66 contests read: 36 + 26 + 4")
            Eq(contests.Where(Function(c) c.Area = "San Benito").Count(), 4, "county names are read from the feed")
            Eq(contests(0).Office, "Governor", "(Vote for 1) is taken off the contest name")
            Eq(contests(0).Winners.Value, 1, "...and kept as the number of winners")
            Eq(contests(0).Area, "Monterey", "area from the county element")
            Eq(contests(0).PrecinctsReporting.Value, 10, "precincts from ""10 of 208""")
            Eq(contests(0).PrecinctsTotal.Value, 208, "total from ""10 of 208""")
            Check(contests.Where(Function(c) c.Area = "Santa Cruz").All(Function(c) Not c.PrecinctsReporting.HasValue), "Santa Cruz gives no precinct counts")
            Check(contests.SelectMany(Function(c) c.Candidates).Any(Function(k) k.WriteIn), "write_in flags are read")

            Dim engine As New ResultsEngine
            Dim items = engine.Evaluate(contests, races, "11/3/2026", "CSV", True, {"San Benito"}, True)
            Eq(engine.SkippedCount, 4, "4 San Benito contests skipped")

            Dim unmatched = items.Where(Function(i) i.Status = ResultStatus.Unmatched).ToList()
            Dim notReady = String.Join(" | ", items.Where(Function(i) i.Status <> ResultStatus.Ready).Select(Function(i) $"{i.Status}: {i.Contest.Office} [{i.Areas}] {String.Join(" / ", i.Notes)}"))
            Dim readyCount = items.Where(Function(i) i.Status = ResultStatus.Ready).Count()
            Check(unmatched.Count = 1 AndAlso unmatched(0).Contest.Office.StartsWith("G - Scotts Valley"), "only the Scotts Valley measure (no setup race) is unmatched" & If(unmatched.Count = 1, "", "  -> " & notReady))
            Check(readyCount = 44, $"all 44 setup races are ready (got {readyCount})" & If(readyCount = 44, "", "  -> " & notReady))
            Eq(items.Where(Function(i) i.Status = ResultStatus.Held).Count(), 0, "nothing held")
            Eq(items.Where(Function(i) i.Race IsNot Nothing).Select(Function(i) i.Race.ID).Distinct().Count(), 44, "each setup race got exactly one result row")

            Dim byId = items.Where(Function(i) i.Race IsNot Nothing).ToDictionary(Function(i) i.Race.ID, StringComparer.OrdinalIgnoreCase)
            If byId.Count < 44 Then Return

            Dim gov = byId("CA-GOV")
            Eq(gov.Contests.Count, 2, "Governor: Monterey and Santa Cruz added together")
            Eq(gov.Areas, "Monterey + Santa Cruz", "areas listed")
            Eq(String.Join(",", VotesIn(gov)), "1500,1506", "Governor votes are Monterey + Santa Cruz")
            Eq(gov.Outgoing.PrecinctsReporting, 10, "precincts reporting added up")
            Eq(gov.Outgoing.PrecinctsTotal, 208, "precincts total added up")
            Eq(gov.Outgoing.SubRaces.Count, 2, "two county sub-races")
            Eq(gov.Outgoing.SubRaces(0).Votes, "1000\1003", "Monterey sub-race votes, in setup candidate order")
            Eq(gov.Outgoing.SubRaces(0).Precincts, "10\208", "Monterey sub-race precincts")
            Eq(gov.Outgoing.SubRaces(1).Votes, "500\503", "Santa Cruz sub-race votes")
            Check(gov.Xml.Contains("<SUBRACE>") AndAlso gov.Xml.Contains("<LOCATION>Santa Cruz</LOCATION>"), "sub-races written to the XML")
            Check(gov.Notes.Any(Function(n) n.Contains("Added together")), "the note says counties were added")
            Check(gov.Notes.Any(Function(n) n.Contains("Precinct counts not given")), "the note says Santa Cruz gave no precincts")

            Eq(byId("CA-AD28").Contests.Count, 1, "Assembly 28 is Santa Cruz only")
            Eq(byId("CA-AD29").Contests.Count, 2, "Assembly 29 in both counties despite different wording")
            Eq(byId("CA-TREASURER").Contests.Count, 2, """State Treasurer"" and ""Treasurer"" pair with one race")
            Eq(byId("CA-BOE2").Contests.Count, 2, "different spellings of Board of Equalization District 2")
            Eq(byId("CA-CD18").Contests.Count, 2, "U.S. Representative District 18 / 18th Congressional District")
            Eq(byId("CA-CD19").Contests.Count, 2, "District 19, both counties")
            Eq(byId("CA-PROP1").Contests.Count, 2, """Proposition 1"" and ""1 - The Veterans And Affordable Housing Bond Act""")
            Check(VotesIn(byId("CA-PROP1")).All(Function(v) v > 0), "BONDS YES / BONDS NO count as Yes / No")
            Eq(byId("CA-PROP2").Contests.Count, 2, "Proposition 2: no mix-up with other numbers")
            Eq(byId("CA-PROP37").Contests.Count, 2, "Proposition 37")
            Eq(byId("CA-GROBAN").Contests.Count, 2, "Justice Groban in both wordings")
            Eq(byId("CA-WILSON").Contests.Count, 2, "Justice Wilson, not Chung")
            Eq(byId("CA-CHUNG").Contests.Count, 2, "Justice Chung")

            Eq(byId("KC-UNION").Contests(0).Office, "King City Union School District Governing Board Member", "King City Union Elementary pairs with its own contest")
            Eq(byId("KC-JOINT").Contests(0).Office, "King City Joint Unified School District Governing Board Member", "King City Joint Unified pairs with its own contest")
            Eq(byId("GUSD").Contests(0).Office, "Greenfield Unified School District Governing Board Member", "Greenfield Unified is not Greenfield Union")
            Eq(byId("GUESD-1").Contests(0).Office, "Greenfield Union School District Governing Board Member Trustee Area 1", "Greenfield Union trustee area 1")
            Eq(byId("SMC-JUHSD").Contests(0).Office, "South Monterey County Joint Union High School District Governing Board Member", "South Monterey County JUHSD")

            Eq(byId("SAL-MAYOR").Contests(0).Office, "City of Salinas Mayor", "Salinas mayor")
            Eq(byId("SAL-D2").Contests(0).Office, "City of Salinas Member, City Council District 2", "Salinas council district 2, not district 3")
            Eq(byId("MRY-MAYOR").Contests(0).Office, "City of Monterey Mayor", "Monterey mayor")
            Eq(byId("CARMEL-CC").Contests(0).Office, "City of Carmel-By-The-Sea Member, City Council", "Carmel council")
            Eq(byId("WAT-D3").Contests(0).Area, "Santa Cruz", "Watsonville district 3 (Santa Cruz)")
            Eq(byId("SCZ-MAYOR").Contests(0).Office, "City of Santa Cruz Mayor", "Santa Cruz mayor")
            Eq(byId("CAP-CC").Contests(0).Office, "City of Capitola Councilmember", "Capitola councilmember")
            Eq(byId("PAJARO-4").Contests(0).Area, "Santa Cruz", "Pajaro Valley trustee area 4")
            Eq(byId("MRY-MEASE").Contests(0).Area, "Monterey", "Measure E Monterey County, not Santa Cruz's Measure E")
            Eq(byId("SC-MEASE").Contests(0).Area, "Santa Cruz", "Measure E Santa Cruz County")
            Eq(byId("SC-MEASD").Contests(0).Area, "Santa Cruz", "Measure D Santa Cruz County")
            Eq(byId("SAL-MEASG").Contests(0).Office, "Measure G: City of Salinas", "Measure G Salinas")
            Eq(byId("SAL-MEASH").Contests(0).Office, "Measure H: City of Salinas", "Measure H Salinas")
            Eq(byId("CARMEL-USD-I").Contests(0).Office, "Measure I: Carmel Unified School District", "Measure I Carmel USD")

            ' names that differ: Don / DONALD P. (DON) WAGNER, Peter Verbica / PETER COE VERBICA, Chuck / CHARLES "CHUCK", DODGE, JR
            Eq(byId("CA-SOS").MatchedCandidates, 4, "Wagner and Weber matched in both counties")
            Eq(byId("CA-CD19").MatchedCandidates, 4, "Peter Verbica matched to PETER COE VERBICA")
            Eq(byId("PGUSD-1").MatchedCandidates, 3, "Chuck Brinker matched to CHARLES ""CHUCK"" BRINKER")
            Eq(byId("PAJARO-4").MatchedCandidates, 2, """DANIEL DODGE, JR"" and ""ERIBERTO ESTRADA, IV"" matched")
            Check(byId("SAND-CC").Notes.Any(Function(n) n.Contains("SALVATORE M. CARBONE")), "flagged named write-ins are ignored, not held")
            Check(byId("CAP-CC").Notes.Any(Function(n) n.Contains("Unqualified Write In")), "Unqualified Write In lines are ignored")

            ' not skipping San Benito: its 50-candidate primary ballot must never be sent automatically
            Dim everything = New ResultsEngine().Evaluate(contests, races, "11/3/2026", "CSV", True)
            Dim sbItems = everything.Where(Function(i) i.Contests.Any(Function(c) c.Area = "San Benito")).ToList()
            Eq(sbItems.Count, 4, "the four San Benito contests join the four races they belong to")
            Check(sbItems.All(Function(i) i.Status = ResultStatus.Held), "if San Benito is not skipped, those races are HELD (its primary ballot lists candidates not in the setup races)")
            Check(sbItems.All(Function(i) i.Notes.Any(Function(n) n.Contains("(San Benito)"))), "the notes name San Benito")

            Eq(New ResultsEngine().Evaluate(contests, races, "11/3/2026", "CSV", True, {"San Benito"}).Single(Function(i) i.Race IsNot Nothing AndAlso i.Race.ID = "CA-GOV").Outgoing.SubRaces.Count, 0, "sub-races are off unless asked for")

            Dim csv = PairingReport.Build(items, races)
            Check(csv.StartsWith("Status,Areas,Feed contest"), "pairing report has a header")
            Check(csv.Contains("G - Scotts Valley TOT") AndAlso csv.Contains("Closest 1"), "pairing report lists the unmatched contest")
        End Sub

    End Module

