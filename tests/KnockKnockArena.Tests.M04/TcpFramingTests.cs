using KnockKnockArena.Shared.Protocol;
using KnockKnockArena.Shared.Protocol.Messages;

namespace KnockKnockArena.Tests.M04;

/// <summary>
/// Module 4: TCP message framing. TCP delivers a STREAM of bytes, not messages:
/// one Read may return half a header, or the end of one message and the start of the
/// next. These tests feed your TcpFraming exactly that - a stream that hands out one
/// byte per Read, and streams that cut at random places.
///
///   [payload length : int32, little-endian] [message type : byte] [payload : N bytes]
/// </summary>
public class TcpFramingTests
{
    /// <summary>A stream that returns at most <c>chunk</c> bytes per Read - what a slow
    /// network does to a real NetworkStream.</summary>
    private sealed class TrickleStream : Stream
    {
        private readonly byte[] _data;
        private readonly Func<int> _chunk;
        private int _position;

        public TrickleStream(byte[] data, Func<int> chunk)
        {
            _data = data;
            _chunk = chunk;
        }

        public int ReadCalls { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ReadCalls++;
            int n = Math.Min(Math.Min(count, _chunk()), _data.Length - _position);
            Array.Copy(_data, _position, buffer, offset, n);
            _position += n;
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static TrickleStream OneByteAtATime(byte[] data) => new(data, () => 1);

    private static byte[] Frame(MessageType type, byte[] payload)
    {
        using MemoryStream stream = new();
        TcpFraming.WriteMessage(stream, type, payload);
        return stream.ToArray();
    }

    private static byte[] Bytes(string dump) => dump.Split('-').Select(h => Convert.ToByte(h, 16)).ToArray();

    // The login every student sends first: test/test (the wiki's hex dump).
    private const string LoginTestTest = "0D-00-00-00-00-06-04-00-74-65-73-74-04-00-74-65-73-74";

    // The first chat line of Demo.HeadOfLine: bob (#2) says "gg".
    private const string ChatBobGg = "0B-00-00-00-66-06-02-03-00-62-6F-62-02-00-67-67";

    // ------------------------------------------------------------------ writing

    [Fact]
    public void WriteMessage_LoginTestTest_IsTheWikiDump()
    {
        byte[] payload = new LoginRequest { Username = "test", Password = "test" }.ToPayload();
        Assert.Equal(LoginTestTest, BitConverter.ToString(Frame(MessageType.LoginRequest, payload)));
    }

    [Fact]
    public void WriteMessage_ChatBroadcast_IsTheHeadOfLineDump()
    {
        byte[] payload = new ChatBroadcast { PlayerId = 2, Username = "bob", Text = "gg" }.ToPayload();
        Assert.Equal(ChatBobGg, BitConverter.ToString(Frame(MessageType.ChatBroadcast, payload)));
    }

    [Fact]
    public void WriteMessage_LengthCountsThePayloadOnly_NotTheTypeByte()
    {
        byte[] framed = Frame(MessageType.ChatSend, new byte[] { 1, 2, 3 });

        Assert.Equal(4 + 1 + 3, framed.Length);
        Assert.Equal("03-00-00-00", BitConverter.ToString(framed, 0, 4)); // 3, little-endian
        Assert.Equal((byte)MessageType.ChatSend, framed[4]);
    }

    [Fact]
    public void WriteMessage_LengthIsLittleEndian()
    {
        byte[] framed = Frame(MessageType.ChatSend, new byte[300]); // 300 = 0x012C
        Assert.Equal("2C-01-00-00", BitConverter.ToString(framed, 0, 4));
    }

    [Fact]
    public void WriteMessage_LeavesTheStreamOpen()
    {
        // Disposing a BinaryWriter closes its stream - on a NetworkStream, the connection.
        using MemoryStream stream = new();
        TcpFraming.WriteMessage(stream, MessageType.ChatSend, new byte[] { 1 });
        TcpFraming.WriteMessage(stream, MessageType.ChatSend, new byte[] { 2 });
        Assert.Equal(12, stream.ToArray().Length);
    }

    // ------------------------------------------------------------------ reading, one byte at a time

    [Fact]
    public void TryReadMessage_ReadsAMessage_DeliveredOneByteAtATime()
    {
        TrickleStream stream = OneByteAtATime(Bytes(LoginTestTest));

        Assert.True(TcpFraming.TryReadMessage(stream, out MessageType type, out byte[] payload));
        Assert.Equal(MessageType.LoginRequest, type);
        LoginRequest login = LoginRequest.FromPayload(payload);
        Assert.Equal("test", login.Username);
        Assert.Equal("test", login.Password);
        Assert.Equal(18, stream.ReadCalls); // it really did get one byte per Read
    }

    [Fact]
    public void TryReadMessage_ReadsTwoMessagesInARow_OneByteAtATime()
    {
        byte[] both = Bytes(LoginTestTest).Concat(Bytes(ChatBobGg)).ToArray();
        TrickleStream stream = OneByteAtATime(both);

        Assert.True(TcpFraming.TryReadMessage(stream, out MessageType first, out _));
        Assert.True(TcpFraming.TryReadMessage(stream, out MessageType second, out byte[] payload));

        Assert.Equal(MessageType.LoginRequest, first);
        Assert.Equal(MessageType.ChatBroadcast, second);
        Assert.Equal("gg", ChatBroadcast.FromPayload(payload).Text);
    }

    [Fact]
    public void TryReadMessage_ReturnsFalse_WhenTheStreamEndsBetweenMessages()
    {
        TrickleStream stream = OneByteAtATime(Bytes(ChatBobGg));

        Assert.True(TcpFraming.TryReadMessage(stream, out _, out _));
        Assert.False(TcpFraming.TryReadMessage(stream, out _, out _)); // clean close
    }

    [Fact]
    public void TryReadMessage_ReadsAnEmptyPayload()
    {
        TrickleStream stream = OneByteAtATime(Frame(MessageType.ChatSend, Array.Empty<byte>()));

        Assert.True(TcpFraming.TryReadMessage(stream, out MessageType type, out byte[] payload));
        Assert.Equal(MessageType.ChatSend, type);
        Assert.Empty(payload);
    }

    // ------------------------------------------------------------------ reading, cut anywhere

    public static IEnumerable<object[]> Seeds() => Enumerable.Range(1, 20).Select(seed => new object[] { seed });

    [Theory]
    [MemberData(nameof(Seeds))]
    public void TryReadMessage_ReadsTenMessages_CutAtRandomPlaces(int seed)
    {
        Random random = new(seed);
        List<string> sent = Enumerable.Range(1, 10).Select(i => new string('x', random.Next(0, 40)) + i).ToList();
        byte[] wire = sent.SelectMany(text => Frame(MessageType.ChatSend, new ChatSend { Text = text }.ToPayload())).ToArray();
        TrickleStream stream = new(wire, () => random.Next(1, 8));

        List<string> received = new();
        while (TcpFraming.TryReadMessage(stream, out MessageType type, out byte[] payload))
        {
            Assert.Equal(MessageType.ChatSend, type);
            received.Add(ChatSend.FromPayload(payload).Text);
        }

        Assert.Equal(sent, received);
    }

    // ------------------------------------------------------------------ broken streams

    public static IEnumerable<object[]> EveryCutInsideAMessage() =>
        Enumerable.Range(1, Bytes(LoginTestTest).Length - 1).Select(n => new object[] { n });

    [Theory]
    [MemberData(nameof(EveryCutInsideAMessage))]
    public void TryReadMessage_ConnectionClosedMidMessage_IsFatal(int bytesBeforeTheCut)
    {
        byte[] cut = Bytes(LoginTestTest).Take(bytesBeforeTheCut).ToArray();

        ProtocolException error = Assert.Throws<ProtocolException>(
            () => TcpFraming.TryReadMessage(OneByteAtATime(cut), out _, out _));
        Assert.True(error.IsFatal);
    }

    [Theory]
    [InlineData(-1, "FF-FF-FF-FF")]
    [InlineData(16385, "01-40-00-00")]        // MaxPayloadLength + 1
    [InlineData(999999, "3F-42-0F-00")]
    public void TryReadMessage_InvalidLength_IsFatal(int length, string lengthBytes)
    {
        byte[] wire = Bytes(lengthBytes + "-01");

        ProtocolException error = Assert.Throws<ProtocolException>(
            () => TcpFraming.TryReadMessage(OneByteAtATime(wire), out _, out _));
        Assert.True(error.IsFatal);
    }

    [Fact]
    public void TryReadMessage_AcceptsTheLargestAllowedPayload()
    {
        byte[] wire = Frame(MessageType.ChatSend, new byte[ProtocolConstants.MaxPayloadLength]);

        Assert.True(TcpFraming.TryReadMessage(new TrickleStream(wire, () => 1000), out _, out byte[] payload));
        Assert.Equal(16 * 1024, payload.Length);
    }

    // ------------------------------------------------------------------ the M04 message types

    [Theory]
    [InlineData(MessageType.LoginRequest, 0)]
    [InlineData(MessageType.ChatSend, 1)]
    [InlineData(MessageType.LoginResponse, 100)]
    [InlineData(MessageType.ServerInfo, 101)]
    [InlineData(MessageType.ChatBroadcast, 102)]
    [InlineData(MessageType.PlayerJoined, 103)]
    [InlineData(MessageType.PlayerLeft, 104)]
    public void MessageType_HasTheProtocolsNumbers(MessageType type, byte value)
    {
        Assert.Equal(value, (byte)type);
    }

    [Fact]
    public void LoginResponse_RoundTripsThroughTheFraming()
    {
        byte[] token = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
        byte[] wire = Frame(MessageType.LoginResponse,
            new LoginResponse { Success = true, PlayerId = 7, SessionToken = token }.ToPayload());

        Assert.True(TcpFraming.TryReadMessage(OneByteAtATime(wire), out MessageType type, out byte[] payload));
        LoginResponse back = LoginResponse.FromPayload(payload);

        Assert.Equal(MessageType.LoginResponse, type);
        Assert.True(back.Success);
        Assert.Equal(7, back.PlayerId);
        Assert.Equal(token, back.SessionToken);
        Assert.Equal(16, ProtocolConstants.SessionTokenLength);
    }
}
