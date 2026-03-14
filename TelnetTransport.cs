using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;

namespace TermWrap
{
    internal sealed class TelnetSessionTransport : ISessionTransport
    {
        private readonly string _host;
        private readonly int _port;
        private readonly TcpClient _client;
        private TelnetInputStream _inputStream;
        private TelnetNegotiatingStream _outputStream;

        public TelnetSessionTransport(string host, int port)
        {
            _host = host;
            _port = port;
            _client = new TcpClient();
        }

        public string ProtocolName { get { return "telnet"; } }
        public string TargetDescription { get { return _host + ":" + _port.ToString(); } }
        public int RemotePid { get { return 0; } }
        public bool HasExited { get { return _inputStream != null && _inputStream.EndOfStream; } }
        public string DescribeState()
        {
            return "connected=" + _client.Connected.ToString() + " endOfStream=" + (_inputStream != null && _inputStream.EndOfStream).ToString();
        }
        public Stream InputStream { get { return _outputStream; } }
        public Stream OutputStream { get { return _inputStream; } }
        public Stream ErrorStream { get { return null; } }

        public void Start()
        {
            _client.Connect(_host, _port);
            NetworkStream network = _client.GetStream();
            _outputStream = new TelnetNegotiatingStream(network);
            _inputStream = new TelnetInputStream(network, _outputStream);
        }

        public void Stop()
        {
            try
            {
                _client.Close();
            }
            catch
            {
            }
        }

        public bool WaitForExit(int milliseconds)
        {
            int waited = 0;
            while (!HasExited && waited < milliseconds)
            {
                Thread.Sleep(50);
                waited += 50;
            }

            return HasExited;
        }

        public void Dispose()
        {
            Stop();
        }
    }

    internal sealed class TelnetInputStream : Stream
    {
        private readonly NetworkStream _network;
        private readonly TelnetNegotiatingStream _writer;
        private bool _endOfStream;

        public TelnetInputStream(NetworkStream network, TelnetNegotiatingStream writer)
        {
            _network = network;
            _writer = writer;
        }

        public bool EndOfStream { get { return _endOfStream; } }
        public override bool CanRead { get { return true; } }
        public override bool CanSeek { get { return false; } }
        public override bool CanWrite { get { return false; } }
        public override long Length { get { throw new NotSupportedException(); } }
        public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int written = 0;
            while (written < count)
            {
                int first = _network.ReadByte();
                if (first < 0)
                {
                    _endOfStream = true;
                    Logger.Info("telnet input eof phase=read-first");
                    break;
                }

                if (first != 255)
                {
                    buffer[offset + written] = (byte)first;
                    written++;
                    if (_network.DataAvailable)
                    {
                        continue;
                    }

                    break;
                }

                int command = _network.ReadByte();
                if (command < 0)
                {
                    _endOfStream = true;
                    Logger.Info("telnet input eof phase=read-command");
                    break;
                }

                if (command == 255)
                {
                    buffer[offset + written] = 255;
                    written++;
                    break;
                }

                if (command == 250)
                {
                    SkipSubNegotiation();
                    continue;
                }

                if (command == 251 || command == 252 || command == 253 || command == 254)
                {
                    int option = _network.ReadByte();
                    if (option < 0)
                    {
                        _endOfStream = true;
                        Logger.Info("telnet input eof phase=read-option");
                        break;
                    }

                    _writer.RespondToNegotiation((byte)command, (byte)option);
                    continue;
                }
            }

            return written;
        }

        private void SkipSubNegotiation()
        {
            int previous = -1;
            while (true)
            {
                int value = _network.ReadByte();
                if (value < 0)
                {
                    _endOfStream = true;
                    Logger.Info("telnet input eof phase=subnegotiation");
                    return;
                }

                if (previous == 255 && value == 240)
                {
                    return;
                }

                previous = value;
            }
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }
    }

    internal sealed class TelnetNegotiatingStream : Stream
    {
        private readonly NetworkStream _network;
        private readonly object _sync = new object();

        public TelnetNegotiatingStream(NetworkStream network)
        {
            _network = network;
        }

        public override bool CanRead { get { return false; } }
        public override bool CanSeek { get { return false; } }
        public override bool CanWrite { get { return true; } }
        public override long Length { get { throw new NotSupportedException(); } }
        public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }

        public override void Flush()
        {
            _network.Flush();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            lock (_sync)
            {
                for (int i = 0; i < count; i++)
                {
                    byte value = buffer[offset + i];
                    _network.WriteByte(value);
                    if (value == 255)
                    {
                        _network.WriteByte(255);
                    }
                }
            }
        }

        public void RespondToNegotiation(byte command, byte option)
        {
            // Some embedded telnet servers disconnect when they receive aggressive
            // WONT/DONT replies. For interoperability, consume negotiation bytes
            // but leave the socket untouched unless we need explicit support later.
        }
    }
}
