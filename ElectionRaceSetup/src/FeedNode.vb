Option Strict On
Option Infer On

Imports System.IO
Imports System.Text
Imports System.Text.RegularExpressions
Imports System.Xml
Imports System.Xml.Linq

''' <summary>
''' One shape for both JSON and XML feeds. JSON objects and XML elements become nodes; a JSON array
''' becomes repeated sibling nodes with the property's name (just like repeated XML elements);
''' XML attributes are kept in <see cref="Attributes"/>.
''' </summary>
Public Class FeedNode
    Public Property Name As String = ""
    Public Property Text As String = ""
    Public Property Parent As FeedNode
    Public ReadOnly Property Children As New List(Of FeedNode)
    Public ReadOnly Property Attributes As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)

    Public Function Add(child As FeedNode) As FeedNode
        child.Parent = Me
        Children.Add(child)
        Return child
    End Function

    Public Function Child(childName As String) As FeedNode
        For Each c In Children
            If String.Equals(c.Name, childName, StringComparison.OrdinalIgnoreCase) Then Return c
        Next
        Return Nothing
    End Function

    ''' <summary>The node's own value; empty for a node that has children.</summary>
    Public Function ScalarText() As String
        Return If(Children.Count = 0, Text, "")
    End Function

    Private Sub CollectSelfAndDescendants(into As List(Of FeedNode))
        into.Add(Me)
        For Each c In Children
            c.CollectSelfAndDescendants(into)
        Next
    End Sub

    ''' <summary>
    ''' Path = names separated by "/", case-insensitive. "*" = any child, "**" = this node and all
    ''' below it, ".." = parent. Example: "Contests/Contest/Candidates/Candidate".
    ''' </summary>
    Public Function SelectNodes(path As String) As List(Of FeedNode)
        Dim current As New List(Of FeedNode) From {Me}
        For Each seg In path.Split("/"c)
            Dim s = seg.Trim()
            If s = "" OrElse s = "." Then Continue For
            Dim [next] As New List(Of FeedNode)
            For Each n In current
                If s = "**" Then
                    n.CollectSelfAndDescendants([next])
                ElseIf s = ".." Then
                    If n.Parent IsNot Nothing Then [next].Add(n.Parent)
                Else
                    For Each c In n.Children
                        If s = "*" OrElse String.Equals(c.Name, s, StringComparison.OrdinalIgnoreCase) Then [next].Add(c)
                    Next
                End If
            Next
            current = [next]
        Next
        Return current
    End Function

    ''' <summary>Value at a path, trimmed. The last segment may be "@attribute". "" when missing.</summary>
    Public Function Value(path As String) As String
        Dim p = path.Trim()
        If p = "" Then Return ""
        Dim slash = p.LastIndexOf("/"c)
        Dim last = If(slash >= 0, p.Substring(slash + 1), p)

        If last.StartsWith("@", StringComparison.Ordinal) Then
            Dim owners = If(slash >= 0, SelectNodes(p.Substring(0, slash)), New List(Of FeedNode) From {Me})
            For Each o In owners
                Dim v As String = Nothing
                If o.Attributes.TryGetValue(last.Substring(1), v) Then Return v.Trim()
            Next
            Return ""
        End If

        Dim hits = SelectNodes(p)
        If hits.Count > 0 Then Return hits(0).ScalarText().Trim()

        If slash < 0 Then
            Dim a As String = Nothing
            If Attributes.TryGetValue(p, a) Then Return a.Trim()
        End If
        Return ""
    End Function

    ''' <summary>Lower-case letters and digits only, so "vote_count", "VoteCount" and "vote count" compare equal.</summary>
    Public Shared Function Norm(s As String) As String
        If s Is Nothing Then Return ""
        Dim sb As New StringBuilder(s.Length)
        For Each c In s
            If Char.IsLetterOrDigit(c) Then sb.Append(Char.ToLowerInvariant(c))
        Next
        Return sb.ToString()
    End Function

    ''' <summary>Reads JSON or XML text (decided by the first character) into a tree.</summary>
    Public Shared Function ParseAny(text As String) As FeedNode
        Dim t = If(text, "").TrimStart(ChrW(&HFEFF), " "c, ControlChars.Tab, ControlChars.Cr, ControlChars.Lf)
        If t.StartsWith("{", StringComparison.Ordinal) OrElse t.StartsWith("[", StringComparison.Ordinal) Then
            Return JsonTree.Parse(t)
        ElseIf t.StartsWith("<", StringComparison.Ordinal) Then
            Return XmlTree.Parse(t)
        End If
        Throw New FormatException("The feed is neither JSON (starts with { or [) nor XML (starts with <).")
    End Function
End Class

''' <summary>Small JSON reader that builds a <see cref="FeedNode"/> tree (no external libraries).</summary>
Public Module JsonTree

    Public Function Parse(text As String) As FeedNode
        Return New JsonParser(text).ParseDocument()
    End Function

    Private Class JsonParser
        Private ReadOnly _s As String
        Private _i As Integer
        Private _depth As Integer

        Public Sub New(s As String)
            _s = s
        End Sub

        Private Sub Fail(msg As String)
            Throw New FormatException($"JSON error at position {_i}: {msg}")
        End Sub

        Private Sub SkipWs()
            While _i < _s.Length AndAlso (_s(_i) = " "c OrElse _s(_i) = ControlChars.Tab OrElse _s(_i) = ControlChars.Cr OrElse _s(_i) = ControlChars.Lf OrElse _s(_i) = ChrW(&HFEFF))
                _i += 1
            End While
        End Sub

        Public Function ParseDocument() As FeedNode
            SkipWs()
            Dim root As FeedNode
            If _i < _s.Length AndAlso _s(_i) = "["c Then
                root = New FeedNode With {.Name = "root"}
                For Each n In ParseValue("item")
                    root.Add(n)
                Next
            Else
                Dim nodes = ParseValue("root")
                If nodes.Count <> 1 Then Fail("expected one JSON value")
                root = nodes(0)
            End If
            SkipWs()
            If _i < _s.Length Then Fail("unexpected text after the JSON value")
            Return root
        End Function

        ''' <summary>One node for a value, or one node per item for an array (each named <paramref name="name"/>).</summary>
        Private Function ParseValue(name As String) As List(Of FeedNode)
            _depth += 1
            If _depth > 200 Then Fail("nested too deeply")
            SkipWs()
            If _i >= _s.Length Then Fail("unexpected end of text")
            Dim result As New List(Of FeedNode)
            Dim c = _s(_i)
            If c = "{"c Then
                result.Add(ParseObject(name))
            ElseIf c = "["c Then
                _i += 1
                SkipWs()
                If _i < _s.Length AndAlso _s(_i) = "]"c Then
                    _i += 1
                Else
                    Do
                        result.AddRange(ParseValue(name))
                        SkipWs()
                        If _i >= _s.Length Then Fail("unterminated array")
                        If _s(_i) = ","c Then
                            _i += 1
                        ElseIf _s(_i) = "]"c Then
                            _i += 1
                            Exit Do
                        Else
                            Fail("expected , or ]")
                        End If
                    Loop
                End If
            ElseIf c = """"c Then
                result.Add(New FeedNode With {.Name = name, .Text = ParseString()})
            Else
                result.Add(New FeedNode With {.Name = name, .Text = ParseLiteral()})
            End If
            _depth -= 1
            Return result
        End Function

        Private Function ParseObject(name As String) As FeedNode
            Dim node As New FeedNode With {.Name = name}
            _i += 1
            SkipWs()
            If _i < _s.Length AndAlso _s(_i) = "}"c Then
                _i += 1
                Return node
            End If
            Do
                SkipWs()
                If _i >= _s.Length OrElse _s(_i) <> """"c Then Fail("expected a property name")
                Dim key = ParseString()
                SkipWs()
                If _i >= _s.Length OrElse _s(_i) <> ":"c Then Fail("expected :")
                _i += 1
                For Each v In ParseValue(key)
                    node.Add(v)
                Next
                SkipWs()
                If _i >= _s.Length Then Fail("unterminated object")
                If _s(_i) = ","c Then
                    _i += 1
                ElseIf _s(_i) = "}"c Then
                    _i += 1
                    Exit Do
                Else
                    Fail("expected , or }")
                End If
            Loop
            Return node
        End Function

        Private Function ParseString() As String
            _i += 1
            Dim sb As New StringBuilder
            While _i < _s.Length
                Dim c = _s(_i)
                _i += 1
                If c = """"c Then Return sb.ToString()
                If c <> "\"c Then
                    sb.Append(c)
                    Continue While
                End If
                If _i >= _s.Length Then Exit While
                Dim e = _s(_i)
                _i += 1
                Select Case e
                    Case """"c, "\"c, "/"c : sb.Append(e)
                    Case "b"c : sb.Append(ControlChars.Back)
                    Case "f"c : sb.Append(ControlChars.FormFeed)
                    Case "n"c : sb.Append(ControlChars.Lf)
                    Case "r"c : sb.Append(ControlChars.Cr)
                    Case "t"c : sb.Append(ControlChars.Tab)
                    Case "u"c
                        If _i + 4 > _s.Length Then Fail("bad \u escape")
                        Dim code As Integer
                        If Not Integer.TryParse(_s.Substring(_i, 4), Globalization.NumberStyles.HexNumber, Globalization.CultureInfo.InvariantCulture, code) Then Fail("bad \u escape")
                        sb.Append(ChrW(code))
                        _i += 4
                    Case Else
                        Fail("bad escape \" & e)
                End Select
            End While
            Fail("unterminated string")
            Return ""
        End Function

        Private Function ParseLiteral() As String
            Dim start = _i
            While _i < _s.Length AndAlso Not ",}] " .Contains(_s(_i)) AndAlso Not Char.IsWhiteSpace(_s(_i))
                _i += 1
            End While
            Dim lit = _s.Substring(start, _i - start)
            If lit = "null" Then Return ""
            If lit = "true" OrElse lit = "false" Then Return lit
            If Regex.IsMatch(lit, "^-?\d+(\.\d+)?([eE][+-]?\d+)?$") Then Return lit
            _i = start
            Fail("unexpected value """ & lit & """")
            Return ""
        End Function
    End Class
End Module

''' <summary>XML reader for feeds. DTDs are ignored and never resolved.</summary>
Public Module XmlTree

    Public Function Parse(text As String) As FeedNode
        Dim settings As New XmlReaderSettings With {.DtdProcessing = DtdProcessing.Ignore, .XmlResolver = Nothing}
        Using sr As New StringReader(text)
            Using xr = XmlReader.Create(sr, settings)
                Dim doc = XDocument.Load(xr)
                Return Convert(doc.Root)
            End Using
        End Using
    End Function

    Private Function Convert(el As XElement) As FeedNode
        Dim node As New FeedNode With {.Name = el.Name.LocalName}
        For Each a In el.Attributes()
            If Not a.IsNamespaceDeclaration Then node.Attributes(a.Name.LocalName) = a.Value
        Next
        Dim hasChildElements = False
        For Each c In el.Elements()
            hasChildElements = True
            node.Add(Convert(c))
        Next
        If Not hasChildElements Then node.Text = el.Value.Trim()
        Return node
    End Function
End Module
