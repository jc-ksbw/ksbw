Option Strict On
Option Infer On

Imports System.IO
Imports System.Text

''' <summary>Small RFC 4180 CSV reader (quotes, embedded commas/newlines, BOM, comma/semicolon/tab).</summary>
Public Module CsvParser

    Public Function ReadFile(path As String) As List(Of String())
        Return Parse(DecodeText(File.ReadAllBytes(path)))
    End Function

    Public Function DecodeText(bytes As Byte()) As String
        If bytes.Length >= 2 AndAlso bytes(0) = &HFF AndAlso bytes(1) = &HFE Then
            Return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2)
        End If
        If bytes.Length >= 2 AndAlso bytes(0) = &HFE AndAlso bytes(1) = &HFF Then
            Return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2)
        End If

        Dim start = 0
        If bytes.Length >= 3 AndAlso bytes(0) = &HEF AndAlso bytes(1) = &HBB AndAlso bytes(2) = &HBF Then start = 3

        Try
            Return New UTF8Encoding(False, True).GetString(bytes, start, bytes.Length - start)
        Catch ex As DecoderFallbackException
            ' Not valid UTF-8: Excel's "CSV (Comma delimited)" is Windows-1252.
            Dim legacy As Encoding
            Try
                legacy = Encoding.GetEncoding(1252)
            Catch
                legacy = Encoding.GetEncoding(28591)
            End Try
            Return legacy.GetString(bytes, start, bytes.Length - start)
        End Try
    End Function

    Public Function DetectDelimiter(text As String) As Char
        Dim counts As New Dictionary(Of Char, Integer) From {{","c, 0}, {";"c, 0}, {ControlChars.Tab, 0}}
        Dim inQuotes = False
        For Each c In text
            If c = """"c Then
                inQuotes = Not inQuotes
            ElseIf Not inQuotes Then
                If c = ControlChars.Lf OrElse c = ControlChars.Cr Then Exit For
                If counts.ContainsKey(c) Then counts(c) += 1
            End If
        Next
        Dim best = ","c
        For Each kv In counts
            If kv.Value > counts(best) Then best = kv.Key
        Next
        Return best
    End Function

    ''' <summary>Splits CSV text into records. Fully blank lines come back as a single empty field.</summary>
    Public Function Parse(text As String) As List(Of String())
        Dim delim = DetectDelimiter(text)
        Dim rows As New List(Of String())
        Dim row As New List(Of String)
        Dim field As New StringBuilder
        Dim inQuotes = False
        Dim i = 0

        While i < text.Length
            Dim c = text(i)
            If inQuotes Then
                If c = """"c Then
                    If i + 1 < text.Length AndAlso text(i + 1) = """"c Then
                        field.Append(""""c)
                        i += 1
                    Else
                        inQuotes = False
                    End If
                Else
                    field.Append(c)
                End If
            ElseIf c = """"c AndAlso field.Length = 0 Then
                inQuotes = True
            ElseIf c = delim Then
                row.Add(field.ToString())
                field.Clear()
            ElseIf c = ControlChars.Cr OrElse c = ControlChars.Lf Then
                If c = ControlChars.Cr AndAlso i + 1 < text.Length AndAlso text(i + 1) = ControlChars.Lf Then i += 1
                row.Add(field.ToString())
                field.Clear()
                rows.Add(row.ToArray())
                row = New List(Of String)
            Else
                field.Append(c)
            End If
            i += 1
        End While

        If field.Length > 0 OrElse row.Count > 0 Then
            row.Add(field.ToString())
            rows.Add(row.ToArray())
        End If
        Return rows
    End Function

End Module
