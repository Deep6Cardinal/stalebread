namespace StaleBread.Windows.Audio;
public sealed record PcmWave(int SampleRate, short Channels, byte[] Samples)
{
    public static PcmWave Read(Stream stream)
    {
        try
        {
            using var r = new BinaryReader(stream, System.Text.Encoding.ASCII, leaveOpen: true);
            if (!stream.CanSeek || stream.Length > 1024 * 1024 || new string(r.ReadChars(4)) != "RIFF") throw new InvalidDataException("Invalid WAV");
            uint length = r.ReadUInt32();
            if (length + 8L != stream.Length || new string(r.ReadChars(4)) != "WAVE") throw new InvalidDataException("Invalid WAV bounds");
            int rate = 0; short channels = 0; byte[]? samples = null;
            while (stream.Position + 8 <= stream.Length)
            {
                string kind = new(r.ReadChars(4)); uint size = r.ReadUInt32();
                long end = stream.Position + size;
                if (end > stream.Length) throw new InvalidDataException("Truncated WAV chunk");
                if (kind == "fmt ")
                {
                    if (size < 16 || r.ReadInt16() != 1) throw new InvalidDataException("Only PCM supported");
                    channels = r.ReadInt16(); rate = r.ReadInt32(); int bytes = r.ReadInt32(); short align = r.ReadInt16(), bits = r.ReadInt16();
                    if (channels is not (1 or 2) || rate is < 8000 or > 96000 || bits != 16 || align != channels * 2 || bytes != rate * align)
                        throw new InvalidDataException("Unsupported PCM format");
                }
                else if (kind == "data") samples = r.ReadBytes(checked((int)size));
                stream.Position = end + (size % 2);
            }
            if (rate == 0 || samples is null || samples.Length == 0 || samples.Length % (channels * 2) != 0 || samples.Length > rate * channels * 2 * 2)
                throw new InvalidDataException("Missing or oversized PCM data");
            return new(rate, channels, samples);
        }
        catch (EndOfStreamException e) { throw new InvalidDataException("Truncated WAV", e); }
    }
    public PcmWave Scale(double volume)
    {
        if (!double.IsFinite(volume) || volume is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(volume));
        var scaled = new byte[Samples.Length];
        for (int i = 0; i < Samples.Length; i += 2)
            System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(scaled.AsSpan(i, 2),
                (short)Math.Round(System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(Samples.AsSpan(i, 2)) * volume));
        return this with { Samples = scaled };
    }
}
