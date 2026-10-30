Option Strict On
Option Infer On

Imports System.IO
Imports System.Net
Imports System.Text

''' <summary>Gets feed text from a file or an http(s) URL, then reads it.</summary>
Public Module FeedFetcher

    Private Const MaxBytes As Integer = 100 * 1024 * 1024

    Public Function IsUrl(source As String) As Boolean
        Return source.StartsWith("http://", StringComparison.OrdinalIgnoreCase) OrElse source.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
    End Function

    ''' <param name="headers">Optional, "Name: value" pairs separated by semicolons (web feeds only).</param>
    Public Function FetchText(source As String, headers As String, Optional timeoutMs As Integer = 20000) As String
        If source Is Nothing OrElse source.Trim() = "" Then Throw New ArgumentException("No feed file or URL given.")
        Dim s = source.Trim()
        If Not IsUrl(s) Then
            Using fs As New FileStream(s, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
                Return CsvParser.DecodeText(ReadAll(fs))
            End Using
        End If

        Try
            ServicePointManager.SecurityProtocol = ServicePointManager.SecurityProtocol Or SecurityProtocolType.Tls12
        Catch
        End Try
        Dim req = DirectCast(WebRequest.Create(s), HttpWebRequest)
        req.Timeout = timeoutMs
        req.ReadWriteTimeout = timeoutMs
        req.UserAgent = "Mozilla/5.0 (compatible; ElectionRaceSetup)"
        req.Accept = "application/json, application/xml, text/xml, */*"
        req.AutomaticDecompression = DecompressionMethods.GZip Or DecompressionMethods.Deflate
        For Each h In If(headers, "").Split(";"c)
            Dim colon = h.IndexOf(":"c)
            If colon > 0 Then req.Headers(h.Substring(0, colon).Trim()) = h.Substring(colon + 1).Trim()
        Next
        Using resp = req.GetResponse()
            Using st = resp.GetResponseStream()
                Return CsvParser.DecodeText(ReadAll(st))
            End Using
        End Using
    End Function

    Private Function ReadAll(st As Stream) As Byte()
        Using ms As New MemoryStream
            Dim buf(81919) As Byte
            Do
                Dim n = st.Read(buf, 0, buf.Length)
                If n <= 0 Then Exit Do
                ms.Write(buf, 0, n)
                If ms.Length > MaxBytes Then Throw New IOException("The feed is larger than 100 MB.")
            Loop
            Return ms.ToArray()
        End Using
    End Function

    ''' <summary>Fetch and read in one step; <paramref name="profilePath"/> is optional.</summary>
    Public Function Load(source As String, headers As String, profilePath As String) As List(Of FeedContest)
        Dim profile As FeedProfile = Nothing
        If Not String.IsNullOrWhiteSpace(profilePath) Then
            profile = FeedProfile.Parse(CsvParser.DecodeText(File.ReadAllBytes(profilePath.Trim())))
        End If
        Return FeedReader.Read(FetchText(source, headers), profile)
    End Function
End Module
