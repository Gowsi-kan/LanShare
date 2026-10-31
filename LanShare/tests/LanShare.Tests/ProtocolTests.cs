using System.Buffers.Binary;
using System.Text;
using LanShare.Core;

namespace LanShare.Tests;

public class ProtocolTests
{
    static Offer SampleOffer() =>
        new(
            Guid.NewGuid(),
            "Laptop-1",
            4,
            2 * 1024 * 1024,
            [
                new FileEntry("a.txt", 10, "k1"),
                new FileEntry("dir/b.bin", 5_000_000_000, "k2"),
                new FileEntry("empty", 0, "k3"),
            ]
        );

    static byte[] Serialize(Offer o)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            Wire.WriteOffer(w, o);
        return ms.ToArray();
    }

    static Offer Parse(byte[] data)
    {
        using var r = new BinaryReader(new MemoryStream(data), Encoding.UTF8);
        return Wire.ReadOffer(r);
    }

    static byte[] RawOffer(int streams, int chunk, int count, long size = 1)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            w.Write(new byte[16]);
            w.Write("sender");
            w.Write(streams);
            w.Write(chunk);
            w.Write(count);
            for (int i = 0; i < Math.Min(count, 3); i++)
            {
                w.Write($"f{i}");
                w.Write(size);
                w.Write("k");
            }
        }
        return ms.ToArray();
    }

    [Fact]
    public void Offer_RoundTrips()
    {
        var o = SampleOffer();
        var back = Parse(Serialize(o));
        Assert.Equal(o.SessionId, back.SessionId);
        Assert.Equal(o.SenderName, back.SenderName);
        Assert.Equal(o.Streams, back.Streams);
        Assert.Equal(o.ChunkSize, back.ChunkSize);
        Assert.Equal(o.Files, back.Files);
        Assert.Equal(5_000_000_010, back.TotalBytes);
    }

    [Fact]
    public void ValidRawOffer_Parses() =>
        Assert.Equal(2, Parse(RawOffer(4, 2 * 1024 * 1024, 2)).Files.Count);

    [Theory]
    [InlineData(0, 2097152, 1, 1)] // no streams
    [InlineData(65, 2097152, 1, 1)] // too many streams
    [InlineData(4, 1000, 1, 1)] // chunk too small
    [InlineData(4, 100_000_000, 1, 1)] // chunk too large
    [InlineData(4, 2097152, -1, 1)] // negative file count
    [InlineData(4, 2097152, 1_000_001, 1)] // too many files
    [InlineData(4, 2097152, 1, -5)] // negative size
    public void ReadOffer_RejectsInvalidValues(int streams, int chunk, int count, long size) =>
        Assert.Throws<InvalidDataException>(() => Parse(RawOffer(streams, chunk, count, size)));

    [Fact]
    public async Task Frame_RoundTrips()
    {
        using var ms = new MemoryStream();
        await Wire.WriteFrameAsync(
            ms,
            w =>
            {
                w.Write(42);
                w.Write("héllo");
            },
            default
        );
        ms.Position = 0;
        using var r = await Wire.ReadFrameAsync(ms, default);
        Assert.Equal(42, r.ReadInt32());
        Assert.Equal("héllo", r.ReadString());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    [InlineData(Protocol.MaxFrame + 1)]
    public async Task Frame_RejectsBadLengths(int length)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, length);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            using var r = await Wire.ReadFrameAsync(new MemoryStream(bytes), default);
        });
    }

    [Fact]
    public async Task Handshake_RoundTripsAndRejectsBadMagic()
    {
        using var ms = new MemoryStream();
        await Wire.WriteHandshakeAsync(ms, Protocol.KindData, default);
        ms.Position = 0;
        Assert.Equal(Protocol.KindData, await Wire.ReadHandshakeAsync(ms, default));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Wire.ReadHandshakeAsync(new MemoryStream("XXXX\u0001"u8.ToArray()), default)
        );
        await Assert.ThrowsAsync<EndOfStreamException>(() =>
            Wire.ReadHandshakeAsync(new MemoryStream([1, 2]), default)
        );
    }

    [Fact]
    public void Fuzz_ReadOffer_OnlyThrowsExpectedExceptions()
    {
        var rnd = new Random(1234);
        var valid = Serialize(SampleOffer());
        for (int i = 0; i < 20_000; i++)
        {
            byte[] data;
            switch (i % 3)
            {
                case 0: // pure noise
                    data = new byte[rnd.Next(0, 400)];
                    rnd.NextBytes(data);
                    break;
                case 1: // a valid offer with a few bytes corrupted
                    data = (byte[])valid.Clone();
                    for (int k = rnd.Next(1, 6); k > 0; k--)
                        data[rnd.Next(data.Length)] ^= (byte)rnd.Next(1, 256);
                    break;
                default: // a valid offer cut short
                    data = valid[..rnd.Next(0, valid.Length)];
                    break;
            }

            try
            {
                Parse(data);
            }
            catch (Exception e) when (e is InvalidDataException or IOException) { }
            catch (Exception e)
            {
                Assert.Fail(
                    $"iteration {i}: {e.GetType().Name}: {e.Message}\ninput: {Convert.ToHexString(data)}"
                );
            }
        }
    }

    [Fact]
    public async Task Fuzz_ReadFrame_OnlyThrowsExpectedExceptions()
    {
        var rnd = new Random(99);
        for (int i = 0; i < 1500; i++)
        {
            var data = new byte[rnd.Next(0, 64)];
            rnd.NextBytes(data);
            if (i % 2 == 0 && data.Length >= 4)
                BinaryPrimitives.WriteInt32LittleEndian(data, rnd.Next(-5, 200)); // plausible lengths

            try
            {
                using var r = await Wire.ReadFrameAsync(new MemoryStream(data), default);
            }
            catch (Exception e) when (e is InvalidDataException or IOException) { }
            catch (Exception e)
            {
                Assert.Fail(
                    $"iteration {i}: {e.GetType().Name}: {e.Message}\ninput: {Convert.ToHexString(data)}"
                );
            }
        }
    }

    [Fact]
    public void ReadOffer_MalformedStringLength_IsAProtocolError()
    {
        var data = new byte[16 + 10];
        for (int i = 16; i < data.Length; i++)
            data[i] = 0xFF; // a 7-bit length prefix that never ends
        Assert.Throws<InvalidDataException>(() => Parse(data));
    }
}
