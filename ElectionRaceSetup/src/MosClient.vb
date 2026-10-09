Option Strict On
Option Infer On

Imports System.Collections.Concurrent
Imports System.Net.Sockets
Imports System.Text
Imports System.Threading

''' <summary>
''' Keeps a TCP connection open to the MOS server Election Manager is attached to, sends queued
''' messages in order, reconnects after a failure and sends a CRLF heartbeat when idle (as the
''' sample's Socket.Connect call does). Events are raised on worker threads.
''' </summary>
Public Class MosClient
    Implements IDisposable

    Private Class Item
        Public Data As String
        Public Tag As Object
    End Class

    Public Event Log(text As String)
    Public Event ConnectionChanged(connected As Boolean)
    Public Event MessageSent(tag As Object)

    Private ReadOnly _queue As New ConcurrentQueue(Of Item)
    Private ReadOnly _wake As New AutoResetEvent(False)
    Private _stop As ManualResetEventSlim
    Private _worker As Thread

    Private _host As String = ""
    Private _port As Integer
    Private _client As TcpClient
    Private _connected As Boolean
    Private _dead As Boolean

    ''' <summary>Pause after each message so Election Manager is not flooded (the sample uses 500 ms).</summary>
    Public Property SendDelayMs As Integer = 500

    Public Property HeartbeatSeconds As Integer = 30

    Public ReadOnly Property IsConnected As Boolean
        Get
            Return _connected
        End Get
    End Property

    Public ReadOnly Property IsRunning As Boolean
        Get
            Return _worker IsNot Nothing AndAlso _worker.IsAlive
        End Get
    End Property

    Public ReadOnly Property PendingCount As Integer
        Get
            Return _queue.Count
        End Get
    End Property

    Public Sub Start(host As String, port As Integer)
        [Stop]()
        _host = host
        _port = port
        _stop = New ManualResetEventSlim(False)
        _worker = New Thread(AddressOf Run) With {.IsBackground = True, .Name = "MosClient"}
        _worker.Start()
    End Sub

    Public Sub [Stop]()
        If _worker Is Nothing Then Return
        _stop.Set()
        _wake.Set()
        _worker.Join(3000)
        _worker = Nothing
        ClearQueue()
    End Sub

    Public Sub Enqueue(data As String, Optional tag As Object = Nothing)
        _queue.Enqueue(New Item With {.Data = data, .Tag = tag})
        _wake.Set()
    End Sub

    ''' <summary>Drops everything not yet written. Returns how many messages were dropped.</summary>
    Public Function ClearQueue() As Integer
        Dim n = 0
        Dim item As Item = Nothing
        While _queue.TryDequeue(item)
            n += 1
        End While
        Return n
    End Function

    Private Sub Run()
        Dim stopEvt = _stop
        Dim lastSend = DateTime.UtcNow

        While Not stopEvt.IsSet
            If _client IsNot Nothing AndAlso _dead Then Disconnect("Connection closed by server.")

            If _client Is Nothing Then
                If Not TryConnect() Then
                    stopEvt.Wait(5000)
                    Continue While
                End If
                lastSend = DateTime.UtcNow
            End If

            Dim item As Item = Nothing
            If _queue.TryPeek(item) Then
                Try
                    Write(item.Data)
                    _queue.TryDequeue(item)
                    lastSend = DateTime.UtcNow
                    RaiseEvent MessageSent(item.Tag)
                    If SendDelayMs > 0 Then stopEvt.Wait(SendDelayMs)
                Catch ex As Exception
                    Disconnect("Send failed: " & ex.Message)
                End Try
            Else
                _wake.WaitOne(1000)
                If _client IsNot Nothing AndAlso HeartbeatSeconds > 0 AndAlso
                   (DateTime.UtcNow - lastSend).TotalSeconds >= HeartbeatSeconds Then
                    Try
                        Write(vbCrLf)
                        lastSend = DateTime.UtcNow
                    Catch ex As Exception
                        Disconnect("Heartbeat failed: " & ex.Message)
                    End Try
                End If
            End If
        End While

        Disconnect("Disconnected.")
    End Sub

    Private Function TryConnect() As Boolean
        Dim c As New TcpClient With {.NoDelay = True}
        Try
            RaiseEvent Log($"Connecting to {_host}:{_port} ...")
            Dim ar = c.BeginConnect(_host, _port, Nothing, Nothing)
            If Not ar.AsyncWaitHandle.WaitOne(5000) Then
                c.Close()
                RaiseEvent Log("Connect timed out, retrying in 5 s.")
                Return False
            End If
            c.EndConnect(ar)
        Catch ex As Exception
            c.Close()
            RaiseEvent Log("Connect failed: " & ex.Message & " - retrying in 5 s.")
            Return False
        End Try

        _dead = False
        _client = c
        _connected = True
        RaiseEvent Log("Connected.")
        RaiseEvent ConnectionChanged(True)

        Dim reader As New Thread(AddressOf ReadLoop) With {.IsBackground = True, .Name = "MosClient.Read"}
        reader.Start(c)
        Return True
    End Function

    ''' <summary>Drains anything the server sends (logged) and notices when the server closes the connection.</summary>
    Private Sub ReadLoop(state As Object)
        Dim c = DirectCast(state, TcpClient)
        Dim buf(4095) As Byte
        Try
            Dim stream = c.GetStream()
            Do
                Dim n = stream.Read(buf, 0, buf.Length)
                If n <= 0 Then Exit Do
                Dim text = Encoding.UTF8.GetString(buf, 0, n).Trim()
                If text <> "" Then RaiseEvent Log("<< " & text)
            Loop
        Catch
        End Try
        If Object.ReferenceEquals(c, _client) Then _dead = True
        _wake.Set()
    End Sub

    Private Sub Write(data As String)
        Dim bytes = New UTF8Encoding(False).GetBytes(data)
        Dim stream = _client.GetStream()
        stream.Write(bytes, 0, bytes.Length)
        stream.Flush()
    End Sub

    Private Sub Disconnect(reason As String)
        Dim c = _client
        _client = Nothing
        If c IsNot Nothing Then
            Try
                c.Close()
            Catch
            End Try
        End If
        If _connected Then
            _connected = False
            RaiseEvent Log(reason)
            RaiseEvent ConnectionChanged(False)
        End If
    End Sub

    Public Sub Dispose() Implements IDisposable.Dispose
        [Stop]()
    End Sub

End Class
