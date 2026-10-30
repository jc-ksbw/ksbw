Option Strict On
Option Infer On

Imports System.IO
Imports System.Net
Imports System.Text.RegularExpressions

Public Class FetchResult
    Public Property StatusCode As Integer
    Public Property Text As String = ""
    Public Property ETag As String = ""

    Public ReadOnly Property NotModified As Boolean
        Get
            Return StatusCode = 304
        End Get
    End Property
End Class

''' <summary>
''' Fetches the monitor's two endpoints, using the change signals the API publishes so a poll that
''' finds nothing new costs one request: an ETag on /api/results, and ballot_version to decide when
''' the ballot definition needs re-reading.
''' </summary>
Public Class MonitorClient

    Public Property ResultsUrl As String = ""
    Public Property ContestsUrl As String = ""
    Public Property Headers As String = ""
    Public Property TimeoutMs As Integer = 20000

    Private _resultsETag As String = ""
    Private _definition As ApiDefinition
    Private _definitionVersion As Integer = -1

    Public ReadOnly Property Definition As ApiDefinition
        Get
            Return _definition
        End Get
    End Property

    ''' <summary>Works out both endpoints from whatever the user typed.</summary>
    Public Shared Function Endpoints(source As String) As KeyValuePair(Of String, String)
        Dim s = If(source, "").Trim().TrimEnd("/"c)
        Dim m = Regex.Match(s, "^(?<base>.*)/api/(?<which>results|contests)(?<ext>\.json|\.xml)?$", RegexOptions.IgnoreCase)
        If m.Success Then
            Dim root = m.Groups("base").Value
            Dim ext = m.Groups("ext").Value
            Return New KeyValuePair(Of String, String)(root & "/api/results" & ext, root & "/api/contests" & ext)
        End If
        Return New KeyValuePair(Of String, String)(s & "/api/results", s & "/api/contests")
    End Function

    Public Sub SetSource(source As String)
        Dim pair = Endpoints(source)
        ResultsUrl = pair.Key
        ContestsUrl = pair.Value
        _resultsETag = ""
        _definition = Nothing
        _definitionVersion = -1
    End Sub

    ''' <summary>Forget the cached ETag and definition so the next poll re-reads everything.</summary>
    Public Sub Reset()
        _resultsETag = ""
        _definition = Nothing
        _definitionVersion = -1
    End Sub

    Public Function Fetch(url As String, Optional ifNoneMatch As String = "") As FetchResult
        If Not FeedFetcher.IsUrl(url) Then
            ' A saved copy on disk; no ETag, always read.
            Return New FetchResult With {.StatusCode = 200, .Text = CsvParser.DecodeText(File.ReadAllBytes(url))}
        End If

        Try
            ServicePointManager.SecurityProtocol = ServicePointManager.SecurityProtocol Or SecurityProtocolType.Tls12
        Catch
        End Try

        Dim req = DirectCast(WebRequest.Create(url), HttpWebRequest)
        req.Timeout = TimeoutMs
        req.ReadWriteTimeout = TimeoutMs
        req.UserAgent = "ElectionRaceSetup"
        req.Accept = "application/json, application/xml, text/xml, */*"
        req.AutomaticDecompression = DecompressionMethods.GZip Or DecompressionMethods.Deflate
        If ifNoneMatch <> "" Then req.Headers(HttpRequestHeader.IfNoneMatch) = ifNoneMatch
        For Each h In If(Headers, "").Split(";"c)
            Dim colon = h.IndexOf(":"c)
            If colon > 0 Then req.Headers(h.Substring(0, colon).Trim()) = h.Substring(colon + 1).Trim()
        Next

        Try
            Using resp = DirectCast(req.GetResponse(), HttpWebResponse)
                Using sr As New StreamReader(resp.GetResponseStream(), New Text.UTF8Encoding(False))
                    Return New FetchResult With {.StatusCode = CInt(resp.StatusCode), .Text = sr.ReadToEnd(), .ETag = resp.Headers("ETag")}
                End Using
            End Using
        Catch ex As WebException
            Dim http = TryCast(ex.Response, HttpWebResponse)
            If http IsNot Nothing AndAlso CInt(http.StatusCode) = 304 Then
                Dim tag = http.Headers("ETag")
                http.Close()
                Return New FetchResult With {.StatusCode = 304, .ETag = tag}
            End If
            If http IsNot Nothing Then
                Dim code = CInt(http.StatusCode)
                http.Close()
                Throw New IOException($"The API answered {code} for {url}.", ex)
            End If
            Throw
        End Try
    End Function

    Public Class Snapshot
        Public Property Results As ApiResults
        Public Property Definition As ApiDefinition
        Public Property Unchanged As Boolean
        Public ReadOnly Property Notes As New List(Of String)
    End Class

    ''' <summary>
    ''' One poll. Returns Unchanged when the API says nothing has moved; otherwise the current
    ''' results together with a definition new enough to describe them.
    ''' </summary>
    Public Function Poll(Optional force As Boolean = False) As Snapshot
        Dim snap As New Snapshot
        Dim got = Fetch(ResultsUrl, If(force, "", _resultsETag))
        If got.NotModified Then
            snap.Unchanged = True
            snap.Results = Nothing
            snap.Definition = _definition
            Return snap
        End If
        If got.ETag <> "" Then _resultsETag = got.ETag

        Dim results = MonitorReader.ReadResults(got.Text)
        If results.SchemaVersion <> MonitorSchema.SupportedSchemaVersion Then
            snap.Notes.Add($"The API reports schema version {results.SchemaVersion}; this app was written for version {MonitorSchema.SupportedSchemaVersion}. Check the fields before sending.")
        End If

        ' The definition only needs re-reading when its version moves.
        If _definition Is Nothing OrElse results.BallotVersion <> _definitionVersion Then
            Dim defText = Fetch(ContestsUrl)
            Dim fresh = MonitorReader.ReadDefinition(defText.Text)
            _definition = fresh
            _definitionVersion = fresh.Version
            If fresh.OverrideErrors.Count > 0 Then
                snap.Notes.Add("The API reports problems in its override files: " & String.Join("; ", fresh.OverrideErrors))
            End If
            If results.BallotVersion <> fresh.Version Then
                snap.Notes.Add($"The results were built against ballot version {results.BallotVersion}; the definition is now version {fresh.Version}.")
            End If
        End If

        snap.Results = results
        snap.Definition = _definition
        Return snap
    End Function

    ''' <summary>Reads the ballot definition on its own, for setting races up.</summary>
    Public Function LoadDefinition() As ApiDefinition
        Dim got = Fetch(ContestsUrl)
        Dim fresh = MonitorReader.ReadDefinition(got.Text)
        _definition = fresh
        _definitionVersion = fresh.Version
        Return fresh
    End Function
End Class
