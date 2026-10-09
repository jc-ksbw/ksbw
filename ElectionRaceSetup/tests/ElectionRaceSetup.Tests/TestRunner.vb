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
            Dim dir = AppContext.BaseDirectory
            For i = 0 To 8
                Dim p = Path.Combine(dir, "sample", "sample_races.csv")
                If File.Exists(p) Then Return p
                dir = Path.GetFullPath(Path.Combine(dir, ".."))
            Next
            Throw New FileNotFoundException("sample_races.csv not found above " & AppContext.BaseDirectory)
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

    End Module

