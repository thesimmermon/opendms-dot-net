﻿using System;
using System.IO;
using System.Net.Sockets;

namespace OpenDMS.Networking.Protocols.Http
{
    public class HttpNetworkStream : Stream
    {
        public enum DirectionType
        {
            None = 0,
            Upload = 1,
            Download = 2
        }

        private ulong _bytesReceived = 0;
        private ulong _bytesSent = 0;
        private ulong _contentLength = 0;
        private bool _unknownContentLength = false;
        private Socket _socket = null;
        private PrependableNetworkStream _stream = null;

        public DirectionType Direction { get; private set; }

        public HttpNetworkStream(DirectionType direction)
        {
            Direction = direction;
        }

        public HttpNetworkStream(DirectionType direction, ulong contentLength, byte[] prependToStream, 
            Socket socket, System.IO.FileAccess fileAccess, bool ownsSocket)
        {
            Direction = direction;
            _contentLength = contentLength;
            _socket = socket;
            _stream = new PrependableNetworkStream(socket, fileAccess, ownsSocket, prependToStream);
            _unknownContentLength = false;
        }

        public HttpNetworkStream(DirectionType direction, ulong contentLength, Socket socket,
            System.IO.FileAccess fileAccess, bool ownsSocket)
        {
            Direction = direction;
            _contentLength = contentLength;
            _socket = socket;
            _stream = new PrependableNetworkStream(socket, fileAccess, ownsSocket);
            _unknownContentLength = false;
        }

        public HttpNetworkStream(DirectionType direction, byte[] prependToStream, Socket socket, 
            System.IO.FileAccess fileAccess, bool ownsSocket)
        {
            Direction = direction;
            _socket = socket;
            _stream = new PrependableNetworkStream(socket, fileAccess, ownsSocket, prependToStream);
            _unknownContentLength = true;
        }

        public HttpNetworkStream(DirectionType direction, Socket socket,
            System.IO.FileAccess fileAccess, bool ownsSocket)
        {
            Direction = direction;
            _socket = socket;
            _stream = new PrependableNetworkStream(socket, fileAccess, ownsSocket);
            _unknownContentLength = true;
        }


        public override bool CanRead
        {
            get 
            {
                if (Direction == DirectionType.Upload) return _stream.CanRead;
                else return false;
            }
        }

        public override bool CanSeek
        {
            get
            {
                if (Direction == DirectionType.Upload) return _stream.CanSeek;
                else return false;
            }
        }

        public bool CanTimeout { get { return _stream.CanTimeout; } }

        public override bool CanWrite
        {
            get
            {
                if (Direction == DirectionType.Download) return _stream.CanWrite;
                else return false;
            }
        }

        public bool DataAvailable { get { return _stream.DataAvailable; } }

        public override long Length { get { return _stream.Length; } }

        public override long Position { get { return _stream.Position; } set { _stream.Position = value; } }

        public int ReadTimeout { get { return _stream.ReadTimeout; } set { _stream.ReadTimeout = value; } }

        public Socket Socket { get { return _socket; } }

        public int WriteTimeout { get { return _stream.WriteTimeout; } set { _stream.WriteTimeout = value; } }

        public delegate void CompleteBufferOperationDelegate(HttpNetworkStream sender, DirectionType direction, byte[] buffer, int offset, int length);
        public delegate void CompleteStreamOperationDelegate(HttpNetworkStream sender, System.IO.Stream stream);
        public delegate void CompleteStringOperationDelegate(HttpNetworkStream sender, string result);
        public delegate void ErrorDelegate(HttpNetworkStream sender, string message, Exception exception);
        public delegate void ProgressDelegate(HttpNetworkStream sender, DirectionType direction, int packetSize);
        public delegate void TimeoutDelegate(HttpNetworkStream sender);

        public event CompleteBufferOperationDelegate OnBufferOperationComplete;
        public event ErrorDelegate OnError;
        public event ProgressDelegate OnProgress;
        public event CompleteStreamOperationDelegate OnStreamOperationComplete;
        public event CompleteStringOperationDelegate OnStringOperationComplete;
        public event TimeoutDelegate OnTimeout;

        public override void Flush()
        {
            throw new NotImplementedException();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotImplementedException();
        }

        public override void SetLength(long value)
        {
            throw new NotImplementedException();
        }

        public void Close()
        {
            _stream.Close();
            _stream.Dispose();
        }

        public void CopyTo(System.IO.Stream stream)
        {
            // Synchronous so let any exceptions bubble up for the higher level

            byte[] buffer = new byte[_socket.ReceiveBufferSize];
            int bytesRead = 0;

            while ((bytesRead = Read(buffer, 0, buffer.Length)) > 0)
            {
                _bytesReceived += (ulong)bytesRead;
                stream.Write(buffer, 0, bytesRead);

                try
                {
                    if (OnProgress != null) OnProgress(this, DirectionType.Download, bytesRead);
                }
                catch (Exception e)
                {
                    // Ignore it, its the higher level's job to deal with it.
                    Logger.Network.Error("An unhandled exception was caught by HttpNetworkStream.CopyTo in the OnProgress event.", e);
                    throw;
                }
            }
        }

        public void CopyToAsync(System.IO.Stream stream)
        {
            Timeout timeout = null;
            StreamAsyncEventArgs args = new StreamAsyncEventArgs();

            args.Complete = CopyToAsync_Callback;
            args.UserToken = new Tuple<Timeout, System.IO.Stream>(timeout, stream);
            args.SetBuffer(new byte[_socket.ReceiveBufferSize], 0, _socket.ReceiveBufferSize);

            if (!TryStartTimeout(_socket.ReceiveTimeout, out timeout,
                new Timeout.TimeoutEvent(CopyToAsync_OnTimeout)))
                return;

            try
            {
                _stream.ReadAsync(args);
            }
            catch (Exception e)
            {
                Logger.Network.Error("An exception occurred while calling CopyToAsync.", e);
                if (OnError != null) OnError(this, "Exception calling CopyToAsync", e);
                else throw;
            }
        }

        public override int Read(byte[] buffer, int offset, int length)
        {
            if (_unknownContentLength)
                return Read_UnknownContentLength(buffer, offset, length);
            else
                return Read_KnownContentLength(buffer, offset, length);
        }

        private int Read_KnownContentLength(byte[] buffer, int offset, int length)
        {
            int amount = 0;

            // Synchronous so let any exceptions bubble up for the higher level

            // We should only try if the total bytes received is <= the content length
            // otherwise we fall into an infinite wait
            if (_contentLength > 0 &&
                _bytesReceived <= _contentLength)
            {
                // Trim down the length to read to the content length if necessary
                if (_contentLength < (ulong)length)
                    length = (int)_contentLength;

                amount = _stream.Read(buffer, offset, length);
                _bytesReceived += (ulong)amount;
                try
                {
                    if (OnProgress != null) OnProgress(this, DirectionType.Download, amount);
                }
                catch (Exception e)
                {
                    // Ignore it, its the higher level's job to deal with it.
                    Logger.Network.Error("An unhandled exception was caught by HttpNetworkStream.Read in the OnProgress event.", e);
                    throw;
                }
            }

            return amount;
        }

        private int Read_UnknownContentLength(byte[] buffer, int offset, int length)
        {
            int amount = 0;

            // Synchronous so let any exceptions bubble up for the higher level

            amount = _stream.Read(buffer, offset, length);
            _bytesReceived += (ulong)amount;
            try
            {
                if (OnProgress != null) OnProgress(this, DirectionType.Download, amount);
            }
            catch (Exception e)
            {
                // Ignore it, its the higher level's job to deal with it.
                Logger.Network.Error("An unhandled exception was caught by HttpNetworkStream.Read in the OnProgress event.", e);
                throw;
            }

            return amount;
        }

        public byte ReadByte()
        {
            if (_unknownContentLength)
                return ReadByte_UnknownContentLength();
            else
                return ReadByte_KnownContentLength();
        }

        private byte ReadByte_KnownContentLength()
        {
            byte retVal = 0;

            // Synchronous so let any exceptions bubble up for the higher level

            // We should only try if the total bytes received is <= the content length
            // otherwise we fall into an infinite wait
            if (_contentLength > 0 &&
                _bytesReceived <= _contentLength)
            {
                retVal = _stream.ReadByte();
                _bytesReceived++;
                try
                {
                    if (OnProgress != null) OnProgress(this, DirectionType.Download, 1);
                }
                catch (Exception e)
                {
                    // Ignore it, its the higher level's job to deal with it.
                    Logger.Network.Error("An unhandled exception was caught by HttpNetworkStream.Read in the OnProgress event.", e);
                    throw;
                }
            }

            return retVal;
        }

        private byte ReadByte_UnknownContentLength()
        {
            byte retVal = 0;

            // Synchronous so let any exceptions bubble up for the higher level

            retVal = _stream.ReadByte();
            _bytesReceived++;
            try
            {
                if (OnProgress != null) OnProgress(this, DirectionType.Download, 1);
            }
            catch (Exception e)
            {
                // Ignore it, its the higher level's job to deal with it.
                Logger.Network.Error("An unhandled exception was caught by HttpNetworkStream.Read in the OnProgress event.", e);
                throw;
            }

            return retVal;
        }

        public void ReadAsync(byte[] buffer, int offset, int count)
        {
            if (_unknownContentLength)
                ReadAsync_UnknownContentLength(buffer, offset, count);
            else
                ReadAsync_KnownContentLength(buffer, offset, count);
        }

        private void ReadAsync_KnownContentLength(byte[] buffer, int offset, int count)
        {
            StreamAsyncEventArgs args = new StreamAsyncEventArgs();
            Timeout timeout = null;
            int lengthMinusPrepend = count;

            if (!TryStartTimeout(_socket.ReceiveTimeout, out timeout,
                new Timeout.TimeoutEvent(ReadAsync_OnTimeout)))
                return;


            // We should only try if the total bytes received is less than the content length
            // otherwise we fall into an infinite wait
            if (_contentLength > 0 &&
                _bytesReceived < _contentLength)
            {
                args.Complete = ReadAsync_Callback;
                args.UserToken = timeout;

                // Trim down the length to read to the content length if necessary
                if (_contentLength < (ulong)args.Count + _bytesReceived)
                    throw new ContentLengthExceededException("Content length exceeded.");

                args.SetBuffer(buffer, offset, count);

                try
                {
                    _stream.ReadAsync(args);
                }
                catch (Exception e)
                {
                    Logger.Network.Error("An exception occurred while calling _stream.BeginRead.", e);
                    if (OnError != null) OnError(this, "Exception calling _stream.BeginRead", e);
                    else throw;
                }
            }
            else
                throw new ContentLengthExceededException("The read was requested starting outside the content length.");
        }

        private void ReadAsync_UnknownContentLength(byte[] buffer, int offset, int count)
        {
            StreamAsyncEventArgs args = new StreamAsyncEventArgs();
            Timeout timeout = null;
            int lengthMinusPrepend = count;

            if (!TryStartTimeout(_socket.ReceiveTimeout, out timeout,
                new Timeout.TimeoutEvent(ReadAsync_OnTimeout)))
                return;

            args.Complete = ReadAsync_Callback;
            args.UserToken = timeout;

            args.SetBuffer(buffer, offset, count);

            try
            {
                _stream.ReadAsync(args);
            }
            catch (Exception e)
            {
                Logger.Network.Error("An exception occurred while calling _stream.BeginRead.", e);
                if (OnError != null) OnError(this, "Exception calling _stream.BeginRead", e);
                else throw;
            }
        }

        public string ReadToEnd()
        {
            if (_unknownContentLength)
                throw new HttpNetworkStreamException("Content length must be set to call WriteAsync.");

            // Synchronous so let any exceptions bubble up for the higher level

            byte[] buffer = new byte[_socket.ReceiveBufferSize];
            int bytesRead = 0;
            ulong totalBytesRead = 0;
            string str = "";

            while ((bytesRead = Read(buffer, 0, buffer.Length)) > 0)
            {
                totalBytesRead += (ulong)bytesRead;
                str += System.Text.Encoding.UTF8.GetString(buffer, 0, bytesRead);

                if (totalBytesRead > _contentLength)
                    throw new System.IO.InternalBufferOverflowException("The content extended beyond the specified content-length header.");
                else if (totalBytesRead == _contentLength)
                    break;
            }

            return str;
        }

        public void ReadToEndAsync()
        {
            if (_unknownContentLength)
                throw new HttpNetworkStreamException("Content length must be set to call WriteAsync.");

            Timeout timeout = null;
            StreamAsyncEventArgs args = new StreamAsyncEventArgs();

            if (!TryStartTimeout(_socket.ReceiveTimeout, out timeout,
                new Timeout.TimeoutEvent(ReadToEndAsync_OnTimeout)))
                return;

            args.Complete = ReadToEndAsync_Callback;
            args.UserToken = new Tuple<Timeout, string>(timeout, "");
            args.SetBuffer(new byte[_socket.ReceiveBufferSize], 0, _socket.ReceiveBufferSize);

            try
            {
                _stream.ReadAsync(args);
            }
            catch (Exception e)
            {
                Logger.Network.Error("An exception occurred while calling ReadToEndAsync.", e);
                if (OnError != null) OnError(this, "Exception calling ReadToEndAsync", e);
                else throw;
            }
        }

        public override void Write(byte[] buffer, int offset, int length)
        {
            // Synchronous so let any exceptions bubble up for the higher level

            _stream.Write(buffer, offset, length);
            _bytesSent += (ulong)length;
            try
            {
                if (OnProgress != null) OnProgress(this, DirectionType.Upload, length);
            }
            catch (Exception e)
            {
                // Ignore it, its the higher level's job to deal with it.
                Logger.Network.Error("An unhandled exception was caught by HttpNetworkStream.Write in the OnProgress event.", e);
                throw;
            }
        }

        public void WriteAsync(StreamAsyncEventArgs e)
        {
            if (_unknownContentLength)
                throw new HttpNetworkStreamException("Content length must be set to call WriteAsync.");

            Timeout timeout = null;

            if (!TryStartTimeout(_socket.SendTimeout, out timeout,
                new Timeout.TimeoutEvent(WriteAsync_OnTimeout)))
                return;

            if (_unknownContentLength)
                throw new HttpNetworkStreamException("Content length must be set to call WriteAsync.");

            if (_bytesSent >= _contentLength)
                throw new ContentLengthExceededException("The read was requested starting outside the content length.");

            e.Complete = WriteAsync_Callback;
            e.UserToken = timeout;

            try
            {
                _stream.WriteAsync(e);
            }
            catch (Exception ex)
            {
                Logger.Network.Error("An exception occurred while calling _stream.BeginWrite.", ex);
                if (OnError != null) OnError(this, "Exception calling _stream.BeginWrite", ex);
                else throw;
            }
        }
        // Private Methods (10) 

        private void CopyToAsync_Callback(StreamAsyncEventArgs e)
        {
            if (_unknownContentLength)
                throw new HttpNetworkStreamException("Content length must be set to call WriteAsync.");

            Timeout timeout = null;
            Tuple<Timeout, System.IO.Stream> userToken = (Tuple<Timeout, System.IO.Stream>)e.UserToken;

            if (!TryStopTimeout(userToken.Item1))
                return;

            _bytesReceived += (ulong)e.Count;

            if (_bytesReceived > _contentLength)
                throw new ContentLengthExceededException("Received more bytes than the content length specified.");

            try
            {
                userToken.Item2.Write(e.Buffer, e.Offset, e.Count);
            }
            catch (Exception ex)
            {
                Logger.Network.Error("An exception occurred while writing to the argument stream.", ex);
                if (OnError != null)
                {
                    OnError(this, "Exception writing to stream.", ex);
                    return;
                }
                else throw;
            }

            // Progress Event
            try
            {
                if (OnProgress != null)
                    OnProgress(this, DirectionType.Download, e.Count);
            }
            catch (Exception ex)
            {
                // Ignore it, its the higher level's job to deal with it.
                Logger.Network.Error("An unhandled exception was caught by HttpNetworkStream.CopyToAsync_Callback in the OnProgress event.", ex);
            }

            // End?
            if (_bytesReceived == _contentLength)
            {
                try
                {
                    if (OnStreamOperationComplete != null)
                        OnStreamOperationComplete(this, userToken.Item2);
                }
                catch (Exception ex)
                {
                    // Ignore it, its the higher level's job to deal with it.
                    Logger.Network.Error("An unhandled exception was caught by HttpNetworkStream.CopyToAsync_Callback in the OnStreamOperationComplete event.", ex);
                }
            }
            else
            {
                // content left to stream
                if (!TryStartTimeout(_socket.ReceiveTimeout, out timeout,
                    new Timeout.TimeoutEvent(ReadToEndAsync_OnTimeout)))
                    return;

                e.UserToken = new Tuple<Timeout, System.IO.Stream>(timeout, userToken.Item2);
                e.SetBuffer(e.Buffer, 0, _socket.ReceiveBufferSize);

                try
                {
                    _stream.ReadAsync(e);
                }
                catch (Exception ex)
                {
                    Logger.Network.Error("An exception occurred while calling ReadAsync.", ex);
                    if (OnError != null) OnError(this, "Exception calling ReadAsync", ex);
                    else throw;
                }
            }
        }

        private void CopyToAsync_OnTimeout()
        {
            Logger.Network.Error("Timeout during CopyTo.");
            try
            {
                if (OnTimeout != null) OnTimeout(this);
            }
            catch (Exception ex)
            {
                // Ignore it, its the higher level's job to deal with it.
                Logger.Network.Error("An unhandled exception was caught by HttpNetworkStream.CopyToAsync_OnTimeout in the OnTimeout event.", ex);
                throw;
            }
        }

        private void ReadAsync_Callback(StreamAsyncEventArgs e)
        {
            _bytesReceived += (ulong)e.Count;

            if (!TryStopTimeout((Timeout)e.UserToken))
                return;

            try
            {
                if (OnProgress != null)
                    OnProgress(this, DirectionType.Download, e.Count);
            }
            catch (Exception ex)
            {
                // Ignore it, its the higher level's job to deal with it.
                Logger.Network.Error("An unhandled exception was caught by HttpNetworkStream.ReadAsync_Callback in the OnProgress event.", ex);
            }

            try
            {
                if (OnBufferOperationComplete != null)
                    OnBufferOperationComplete(this, DirectionType.Download,
                        e.Buffer, e.Offset, e.Count);
            }
            catch (Exception ex)
            {
                // Ignore it, its the higher level's job to deal with it.
                Logger.Network.Error("An unhandled exception was caught by HttpNetworkStream.ReadAsync_Callback in the OnBufferOperationComplete event.", ex);
            }

        }

        private void ReadAsync_OnTimeout()
        {
            Logger.Network.Error("Timeout during read.");
            try
            {
                if (OnTimeout != null) OnTimeout(this);
            }
            catch (Exception ex)
            {
                // Ignore it, its the higher level's job to deal with it.
                Logger.Network.Error("An unhandled exception was caught by HttpNetworkStream.ReadAsync_OnTimeout in the OnTimeout event.", ex);
                throw;
            }
        }

        private void ReadToEndAsync_Callback(StreamAsyncEventArgs e)
        {
            Timeout timeout = null;
            Tuple<Timeout, string> userToken = (Tuple<Timeout, string>)e.UserToken;
            string str = userToken.Item2;

            if (!TryStopTimeout(userToken.Item1))
                return;

            _bytesReceived += (ulong)e.Count;

            if (_bytesReceived > _contentLength)
                throw new ContentLengthExceededException("Content received was longer than the Content Length specified.");

            try
            {
                str += System.Text.Encoding.ASCII.GetString(e.Buffer, e.Offset, e.Count);
            }
            catch (Exception ex)
            {
                Logger.Network.Error("An exception occurred while getting a string from the buffer.", ex);
                if (OnError != null)
                {
                    OnError(this, "Exception while getting a string from the buffer.", ex);
                    return;
                }
            }

            // Progress Event
            try
            {
                if (OnProgress != null)
                    OnProgress(this, DirectionType.Download, e.Count);
            }
            catch (Exception ex)
            {
                // Ignore it, its the higher level's job to deal with it.
                Logger.Network.Error("An unhandled exception was caught by HttpNetworkStream.ReadToEndAsync_Callback in the OnProgress event.", ex);
            }

            // End?
            if (_bytesReceived == _contentLength)
            {
                try
                {
                    if (OnStringOperationComplete != null)
                        OnStringOperationComplete(this, str);
                }
                catch (Exception ex)
                {
                    // Ignore it, its the higher level's job to deal with it.
                    Logger.Network.Error("An unhandled exception was caught by HttpNetworkStream.ReadToEndAsync_Callback in the OnStringOperationComplete event.", ex);
                }
            }
            else
            {
                // content left to stream
                if (!TryStartTimeout(_socket.ReceiveTimeout, out timeout,
                    new Timeout.TimeoutEvent(ReadToEndAsync_OnTimeout)))
                    return;

                e.UserToken = new Tuple<Timeout, string>(timeout, str);
                e.SetBuffer(e.Buffer, 0, _socket.ReceiveBufferSize);

                try
                {
                    _stream.ReadAsync(e);
                }
                catch (Exception ex)
                {
                    Logger.Network.Error("An exception occurred while calling ReadAsync.", ex);
                    if (OnError != null) OnError(this, "Exception calling ReadAsync", ex);
                    else throw;
                }
            }
        }

        private void ReadToEndAsync_OnTimeout()
        {
            Logger.Network.Error("Timeout during ReadToEnd.");
            try
            {
                if (OnTimeout != null) OnTimeout(this);
            }
            catch (Exception ex)
            {
                // Ignore it, its the higher level's job to deal with it.
                Logger.Network.Error("An unhandled exception was caught by HttpNetworkStream.ReadToEndAsync_OnTimeout in the OnTimeout event.", ex);
                throw;
            }
        }

        private bool TryStartTimeout(int milliseconds, out Timeout timeout, Timeout.TimeoutEvent onTimeout)
        {
            timeout = null;

            try
            {
                timeout = new Timeout(_socket.ReceiveTimeout).Start();
                timeout.OnTimeout += onTimeout;
            }
            catch (Exception e)
            {
                Logger.Network.Error("An exception occurred while starting the timeout.", e);
                if (OnError != null)
                {
                    OnError(this, "Exception while starting timeout.", e);
                    return false;
                }
                else throw;
            }

            return true;
        }

        private bool TryStopTimeout(Timeout timeout)
        {
            try
            {
                timeout.Stop();
            }
            catch (Exception ex)
            {
                Logger.Network.Error("An exception occurred while stopping the timeout.", ex);
                if (OnError != null)
                {
                    OnError(this, "Exception while stopping timeout.", ex);
                    return false;
                }
                else throw;
            }

            return true;
        }

        private void WriteAsync_Callback(StreamAsyncEventArgs e)
        {
            _bytesSent += (ulong)e.Count;

            if (!TryStopTimeout((Timeout)e.UserToken))
                return;

            try
            {
                if (OnProgress != null)
                    OnProgress(this, DirectionType.Upload, e.Count);

            }
            catch (Exception ex)
            {
                // Ignore it, its the higher level's job to deal with it.
                Logger.Network.Error("An unhandled exception was caught by HttpNetworkStream.WriteAsync_Callback in the OnProgress event.", ex);
            }

            try
            {
                if (OnBufferOperationComplete != null)
                    OnBufferOperationComplete(this, DirectionType.Upload,
                        e.Buffer, e.Offset, e.Count);

            }
            catch (Exception ex)
            {
                // Ignore it, its the higher level's job to deal with it.
                Logger.Network.Error("An unhandled exception was caught by HttpNetworkStream.WriteAsync_Callback in the OnBufferOperationComplete event.", ex);
            }
        }

        private void WriteAsync_OnTimeout()
        {
            Logger.Network.Error("Timeout during write.");
            try
            {
                if (OnTimeout != null) OnTimeout(this);
            }
            catch (Exception ex)
            {
                // Ignore it, its the higher level's job to deal with it.
                Logger.Network.Error("An unhandled exception was caught by HttpNetworkStream.WriteAsync_OnTimeout in the OnTimeout event.", ex);
                throw;
            }
        }
    }
}
